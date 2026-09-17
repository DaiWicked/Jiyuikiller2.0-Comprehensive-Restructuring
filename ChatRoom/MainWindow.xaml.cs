using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatRoom.Models;
using ChatRoom.Services;

namespace ChatRoom
{
    public partial class MainWindow : Window
    {
        private ChatUdpService _chat;
        private ChatSettings _settings;
        private ChatUser _currentTarget;

        private ObservableCollection<ChatUser> _userList = new ObservableCollection<ChatUser>();
        private ObservableCollection<ChatMessageItem> _messages = new ObservableCollection<ChatMessageItem>();
        private string _historyPath;

        public MainWindow()
        {
            InitializeComponent();
            UserList.ItemsSource = _userList;
            ChatItems.ItemsSource = _messages;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _settings = ChatSettings.Load();
            _historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat_history.txt");

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--nick="))
                    _settings.Nickname = args[i].Substring(7);
            }

            LoadHistory();
            _chat = new ChatUdpService { Nickname = _settings.Nickname };
            _chat.OnUserJoined += OnUserJoined;
            _chat.OnUserLeft += OnUserLeft;
            _chat.OnGroupMessage += OnGroupMessage;
            _chat.OnPrivateMessage += OnPrivateMessage;
            _chat.OnLog += OnServiceLog;
            _chat.OnImageReceived += OnImageReceived;

            // 单实例判定 = "能否绑定 47060"，由操作系统仲裁。
            // 旧实现靠扫进程名 + PID 文件：被僵尸进程误判（2026-09-17 实测有 8 个不可杀的旧实例，
            // 它们占不到端口却让新实例启动被拒）。僵尸进程不持有端口 ⇒ 现在不会再误伤。
            try
            {
                _chat.Start();
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show("小小聊天已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("聊天服务启动失败：" + Environment.NewLine + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            ApplySettings();

            // 恢复上次的窗口位置/大小（越界时夹回可见区域，避免显示器变化后窗口跑到屏幕外）
            if (_settings.WindowWidth > 200 && _settings.WindowHeight > 150)
            {
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;

                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
                double left = Math.Min(Math.Max(_settings.WindowLeft, vl), vr - 120);
                double top = Math.Min(Math.Max(_settings.WindowTop, vt), vb - 60);
                Left = left;
                Top = top;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        private void ApplySettings()
        {
            Topmost = _settings.TopMost;
            InputBox.FontSize = _settings.FontSize;
        }

        // === 单实例 ===
        // 原 CheckSingleInstance/WritePidFile/CleanPidFile 三段已删除（2026-09-17）：
        // 扫进程名 + PID 文件的做法会把"不可杀的僵尸实例"也算作冲突，导致程序无法启动；
        // 现在改由 ChatUdpService.Start() 绑定 47060 失败来判定，见构造函数上的说明。

        // === 消息历史持久化 ===

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                var lines = File.ReadAllLines(_historyPath, System.Text.Encoding.UTF8);
                var recent = lines.Skip(Math.Max(0, lines.Length - 100)).ToList();
                foreach (string line in recent)
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        string sender = parts[1].Trim();
                        // 消息里本身可能含 '|'（图片占位写作 [图片]|<路径>），所以第 3 段起要拼回来
                        string msg = parts.Length > 3 ? string.Join("|", parts.Skip(2)).Trim() : parts[2].Trim();
                        string lineTime = parts[0].Trim().Trim('[', ']');

                        bool isMe = sender.StartsWith("我");
                        // 图片消息：历史行记的是 [图片]|<落盘路径>，把图还原成图片气泡
                        if (msg.StartsWith("[图片]"))
                        {
                            string imgPath = msg.Length > 4 && msg[4] == '|' ? msg.Substring(5) : "";
                            BitmapImage hisImg = null;
                            try { if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath)) hisImg = ChatImageCodec.Decode(File.ReadAllBytes(imgPath)); } catch { }

                            if (hisImg != null)
                                AddImageMessage(sender, hisImg, imgPath, isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, lineTime);
                            else
                                AddMessage(sender, "[图片]（图片已过期）", isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, false, lineTime);
                            continue;
                        }
                        AddMessage(sender, msg, isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, false, lineTime);
                    }
                }
                if (_messages.Count > 0)
                    AddMessage("系统", "--- 以下为新消息 ---", BubbleKind.Service);
            }
            catch { }
        }

        private void SaveHistoryLine(string sender, string message)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm}] | {sender} | {message}";
                File.AppendAllText(_historyPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// 从网络线程切回 UI 线程。<b>用 BeginInvoke 而不是 Invoke</b>：
        /// 接收/心跳线程绝不能被 UI 线程阻塞（原来 5 处 Dispatcher.Invoke 会让 UDP 收发等 UI，
        /// UI 一忙就卡住收发，极端情况还会与"UI 等网络线程"形成死锁）。
        /// 已经在 UI 线程时直接执行，避免无谓的二次排队。
        /// </summary>
        private void OnUI(Action action)
        {
            if (action == null) return;
            if (Dispatcher.CheckAccess()) { action(); return; }
            Dispatcher.BeginInvoke(new Action(action));
        }

        // === 聊天服务事件 ===

        private void OnUserJoined(ChatUser user)
        {
            OnUI(() =>
            {
                if (!_userList.Any(u => u.IP == user.IP))
                    _userList.Add(user);
                UpdateUserCount();
                AddMessage("系统", $"{user.Nickname} 加入了聊天", BubbleKind.Service);
            });
        }

        private void OnUserLeft(ChatUser user)
        {
            OnUI(() =>
            {
                var existing = _userList.FirstOrDefault(u => u.IP == user.IP);
                if (existing != null) _userList.Remove(existing);
                UpdateUserCount();
                AddMessage("系统", $"{user.Nickname} 离开了聊天", BubbleKind.Service);
            });
        }

        private void UpdateUserCount()
        {
            int count = _userList.Count(u => u.IsOnline);
            UserCount.Text = $"({count})";
        }

        private void OnGroupMessage(ChatUser from, string msg)
        {
            OnUI(() =>
            {
                AddMessage(from.Nickname, msg, BubbleKind.Incoming);
                SaveHistoryLine(from.Nickname, msg);
            });
        }

        private void OnPrivateMessage(ChatUser from, string msg)
        {
            OnUI(() =>
            {
                if (_currentTarget != null && _currentTarget.IP == from.IP)
                    AddMessage(from.Nickname + " [私聊]", msg, BubbleKind.Incoming);
                else
                    AddMessage("📩 " + from.Nickname, msg, BubbleKind.Incoming);
                SaveHistoryLine(from.Nickname + "[私聊]", msg);
            });
        }

        private void OnServiceLog(string log)
        {
            OnUI(() => AddMessage("系统", log, BubbleKind.Service));
        }

        // === 图片 ===

        /// <summary>收到一张完整图片：解码 → 落盘 → 显示 → 历史只记 [图片]</summary>
        private void OnImageReceived(ChatUser from, byte[] jpeg, string scope)
        {
            OnUI(() =>
            {
                BitmapImage bmp = ChatImageCodec.Decode(jpeg);
                if (bmp == null)
                {
                    AddMessage("系统", "收到一张无法解码的图片", BubbleKind.Service);
                    return;
                }

                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat_images");
                string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (from.Nickname ?? "未知");
                string saved = ChatImageCodec.SaveTo(dir, name, jpeg);

                string label = scope == "P" ? (from.Nickname + " [私聊图片]") : from.Nickname;
                AddImageMessage(label, bmp, saved, BubbleKind.Incoming);
                SaveHistoryLine(from.Nickname + (scope == "P" ? "[私聊]" : ""), "[图片]|" + saved);
            });
        }

        private void BtnImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要发送的图片",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                byte[] jpeg = ChatImageCodec.EncodeFile(dlg.FileName);
                if (jpeg == null || jpeg.Length == 0) { MessageBox.Show("图片读取失败。", "提示"); return; }

                if (jpeg.Length / 3 * 4 > ChatUdpService.ImageMaxChars)
                {
                    MessageBox.Show("这张图太大了（压缩后 " + (jpeg.Length / 1024) + "KB），请换一张更小的。", "提示");
                    return;
                }

                string target = _currentTarget != null ? _currentTarget.IP : null;
                _chat.SendImage(jpeg, target);

                BitmapImage bmp = ChatImageCodec.Decode(jpeg);
                string label = target == null ? "我（群发）" : ("我 → " + _currentTarget.Nickname);
                AddImageMessage(label, bmp, "", BubbleKind.Outgoing);
                SaveHistoryLine("我", "[图片]");
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送图片失败：" + ex.Message, "错误");
            }
        }

        /// <summary>点图片用系统查看器打开（发送端已缩到 320x240，线上没有更高分辨率的原图）</summary>
        private void Image_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ChatMessageItem item = (sender as FrameworkElement)?.DataContext as ChatMessageItem;
            if (item == null || string.IsNullOrEmpty(item.ImagePath)) return;
            try { System.Diagnostics.Process.Start(item.ImagePath); }
            catch (Exception ex) { MessageBox.Show("打开图片失败：" + ex.Message, "提示"); }
        }

        /// <summary>追加一条图片消息（气泡样式与文字消息同一套主题）</summary>
        private void AddImageMessage(string sender, System.Windows.Media.ImageSource image, string path, BubbleKind kind, string time = null)
        {
            ApplyBubbleStyle(kind, out Brush bg, out Brush border, out Brush fg, out Brush secondary);
            bool right = kind == BubbleKind.Outgoing;

            _messages.Add(new ChatMessageItem
            {
                Sender = sender,
                Message = "[图片]",
                IsImage = true,
                Image = image,
                ImagePath = path,
                FontSize = _settings.FontSize,
                Kind = kind,
                BgBrush = bg,
                BorderBrush = border,
                TextBrush = fg,
                SecondaryBrush = secondary,
                Time = DateTime.Now.ToString("HH:mm"),
                Align = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(right ? 100 : 0, 4, right ? 0 : 100, 4)
            });

            // 未读：自己发的不算；正看着窗口也不算；未读分隔线本身也不算
            if (kind == BubbleKind.Incoming) TrackUnread(true);   // 只计真实来消息：服务消息(上线/日志)与启动横幅不算，否则每次启动都会冒出一个未读
            AutoScroll();
        }

        // === 未读 / 滚动 ===

        private int _unreadCount = 0;
        private bool _unreadSeparatorShown = false;

        /// <summary>
        /// 未读计数：只在用户"没在看"时累加（最小化或窗口不在前台）。
        /// 第一次出现未读时插入一条服务分隔线（Telegram 的 unread divider 概念），
        /// 之后回来的用户往上翻就能看到"从这里开始是新消息"。
        /// </summary>
        private void TrackUnread(bool incoming)
        {
            if (!incoming) return;
            if (IsActive && WindowState != WindowState.Minimized) return;

            _unreadCount++;
            TextUnread.Text = _unreadCount + " 条新消息";
            BtnUnread.Visibility = Visibility.Visible;

            if (!_unreadSeparatorShown)
            {
                _unreadSeparatorShown = true;
                // 分隔线本身是服务消息，而服务消息不计未读，所以不会自增计数
                AddMessage("系统", "── 以下为新消息 ──", BubbleKind.Service);            }
        }

        private void ClearUnread()
        {
            _unreadCount = 0;
            _unreadSeparatorShown = false;
            TextUnread.Text = "0 条新消息";
            BtnUnread.Visibility = Visibility.Collapsed;
        }

        private void BtnUnread_Click(object sender, RoutedEventArgs e)
        {
            ChatScroll.ScrollToEnd();
            ClearUnread();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_unreadCount > 0) ClearUnread();   // 回到前台即视为已读（分隔线记录保留）
        }

        /// <summary>
        /// 只有"本来就在底部附近"才自动滚动 —— 否则用户正在往上翻历史，被强行拽到底部很烦。
        /// </summary>
        private void AutoScroll()
        {
            if (ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 40) ChatScroll.ScrollToEnd();
        }

        // === UI事件 ===

        /// <summary>
        /// 无边框窗口的拖动：WindowStyle=None 之后系统不再提供标题栏拖动，必须自己实现。
        /// （按压缩下状态才拖，避免单击就移动；拖动期间异常（如按住时窗口被关）直接忽略。）
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        }

        private void UserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var user = UserList.SelectedItem as ChatUser;
            if (user == null || user.IsMe) return;
            _currentTarget = user;
            ChatTitle.Text = $"私聊: {user.Nickname}";
        }

        private void BtnGroupChat_Click(object sender, RoutedEventArgs e)
        {
            _currentTarget = null;
            ChatTitle.Text = "# 群聊";
            UserList.SelectedItem = null;
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool sendNow = false;
            if (_settings.SendWithEnter && e.Key == System.Windows.Input.Key.Enter)
                sendNow = true;
            else if (!_settings.SendWithEnter && e.Key == System.Windows.Input.Key.Return &&
                     (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
                sendNow = true;

            if (sendNow)
            {
                SendMessage();
                e.Handled = true;
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _settings = dlg.Settings;
                ApplySettings();
                if (_chat != null) _chat.Nickname = _settings.Nickname;
            }
        }

        private void SendMessage()
        {
            string msg = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            if (_currentTarget != null)
            {
                _chat.SendPrivate(_currentTarget.IP, msg);
                AddMessage("我 → " + _currentTarget.Nickname, msg, BubbleKind.Outgoing);
                SaveHistoryLine("我[私聊]", msg);
            }
            else
            {
                _chat.SendGroup(msg);
                AddMessage("我", msg, BubbleKind.Outgoing);
                SaveHistoryLine("我", msg);
            }

            InputBox.Clear();
        }

        /// <summary>
        /// 追加一条消息。颜色**统一从 Theme 取**（原来 9 处调用各写各的硬编码色值，
        /// 结果收到的是 #FFFFFF 白气泡压在 #FAFAFA 浅灰底上，几乎看不见）。
        /// </summary>
        private void AddMessage(string sender, string message, BubbleKind kind,
                                bool saveToHistory = true, string time = null)
        {
            ApplyBubbleStyle(kind, out Brush bg, out Brush border, out Brush fg, out Brush secondary);
            bool right = kind == BubbleKind.Outgoing;

            _messages.Add(new ChatMessageItem
            {
                Sender = sender,
                Message = message,
                FontSize = _settings.FontSize,
                Kind = kind,
                BgBrush = bg,
                BorderBrush = border,
                TextBrush = fg,
                SecondaryBrush = secondary,
                Time = string.IsNullOrEmpty(time) ? DateTime.Now.ToString("HH:mm") : time,
                Align = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(right ? 100 : 0, 4, right ? 0 : 100, 4)
            });

            // 未读：自己发的不算；正看着窗口也不算；未读分隔线本身也不算
            if (kind == BubbleKind.Incoming) TrackUnread(true);   // 只计真实来消息：服务消息(上线/日志)与启动横幅不算，否则每次启动都会冒出一个未读
            AutoScroll();
        }

        /// <summary>按气泡类型从当前主题取一组颜色</summary>
        private static void ApplyBubbleStyle(BubbleKind kind, out Brush bg, out Brush border, out Brush fg, out Brush secondary)
        {
            secondary = Theme.Get("TextSecondary");
            switch (kind)
            {
                case BubbleKind.Outgoing:
                    bg = Theme.Get("BubbleOutBg"); border = Theme.Get("BubbleOutBorder"); fg = Theme.Get("BubbleOutFg");
                    break;
                case BubbleKind.Service:
                    bg = Theme.Get("ServiceBg"); border = Theme.Get("ServiceBg"); fg = Theme.Get("ServiceFg");
                    secondary = Theme.Get("ServiceFg");
                    break;
                default:
                    bg = Theme.Get("BubbleInBg"); border = Theme.Get("BubbleInBorder"); fg = Theme.Get("BubbleInFg");
                    break;
            }
        }

        /// <summary>切换明暗主题：换资源 + 重着色已有气泡 + 存盘（气泡颜色是代码赋的，不会自己跟着资源变）</summary>
        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            Theme.Apply(!Theme.IsDark);
            BtnTheme.Content = Theme.IsDark ? "☀" : "☾";
            RecolorMessages();
            _settings.DarkMode = Theme.IsDark;
            _settings.Save();
        }

        private void RecolorMessages()
        {
            foreach (ChatMessageItem item in _messages)
            {
                ApplyBubbleStyle(item.Kind, out Brush bg, out Brush border, out Brush fg, out Brush secondary);
                item.BgBrush = bg; item.BorderBrush = border; item.TextBrush = fg; item.SecondaryBrush = secondary;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _chat?.Stop();

            // 记住窗口位置/大小（下次打开恢复）
            try
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.Save();
            }
            catch { }

            if (_settings.ClearOnExit)
            {
                try { if (File.Exists(_historyPath)) File.Delete(_historyPath); } catch { }
            }
            base.OnClosed(e);
        }
    }

    /// <summary>气泡类型：决定用主题里的哪一组颜色</summary>
    public enum BubbleKind { Incoming, Outgoing, Service }

    public class ChatMessageItem
    {
        public string Sender { get; set; }
        public string Message { get; set; }
        public int FontSize { get; set; } = 13;
        public Brush BgBrush { get; set; }
        public Brush BorderBrush { get; set; }
        public Brush TextBrush { get; set; }
        public Brush SecondaryBrush { get; set; }
        public string Time { get; set; } = "";
        public BubbleKind Kind { get; set; } = BubbleKind.Incoming;
        /// <summary>是否图片消息（气泡模板据此显示缩略图并隐藏文字）</summary>
        public bool IsImage { get; set; }
        public System.Windows.Media.ImageSource Image { get; set; }
        /// <summary>落盘路径（点开查看用；自己发出的那条本地显示没有路径）</summary>
        public string ImagePath { get; set; } = "";
        public HorizontalAlignment Align { get; set; }
        public Thickness Margin { get; set; }
    }
}
