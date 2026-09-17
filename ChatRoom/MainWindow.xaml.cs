using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                        string msg = parts[2].Trim();
                        bool isMe = sender.StartsWith("我");
                        AddMessage(sender, msg,
                            isMe ? "#5B8DEF" : "#FFFFFF",
                            isMe ? "Right" : "Left",
                            isMe ? "#FFFFFF" : "#000000",
                            false);
                    }
                }
                if (_messages.Count > 0)
                    AddMessage("系统", "--- 以下为新消息 ---", "#8E8E93", "Left", "#8E8E93", false);
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
                AddMessage("系统", $"{user.Nickname} 加入了聊天", "#8E8E93", "Left");
            });
        }

        private void OnUserLeft(ChatUser user)
        {
            OnUI(() =>
            {
                var existing = _userList.FirstOrDefault(u => u.IP == user.IP);
                if (existing != null) _userList.Remove(existing);
                UpdateUserCount();
                AddMessage("系统", $"{user.Nickname} 离开了聊天", "#8E8E93", "Left");
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
                AddMessage(from.Nickname, msg, "#FFFFFF", "Left");
                SaveHistoryLine(from.Nickname, msg);
            });
        }

        private void OnPrivateMessage(ChatUser from, string msg)
        {
            OnUI(() =>
            {
                if (_currentTarget != null && _currentTarget.IP == from.IP)
                    AddMessage(from.Nickname + " [私聊]", msg, "#FFD699", "Left");
                else
                    AddMessage("📩 " + from.Nickname, msg, "#FFD699", "Left");
                SaveHistoryLine(from.Nickname + "[私聊]", msg);
            });
        }

        private void OnServiceLog(string log)
        {
            OnUI(() => AddMessage("系统", log, "#F0F0F5", "Left"));
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
                AddMessage("我 → " + _currentTarget.Nickname, msg, "#5B8DEF", "Right", "#FFFFFF");
                SaveHistoryLine("我[私聊]", msg);
            }
            else
            {
                _chat.SendGroup(msg);
                AddMessage("我", msg, "#5B8DEF", "Right", "#FFFFFF");
                SaveHistoryLine("我", msg);
            }

            InputBox.Clear();
        }

        private void AddMessage(string sender, string message, string bgColor, string align,
                                string textColor = "#000000", bool saveToHistory = true)
        {
            _messages.Add(new ChatMessageItem
            {
                Sender = sender,
                Message = message,
                FontSize = _settings.FontSize,
                BgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                Align = align == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(align == "Right" ? 100 : 0, 4, align == "Right" ? 0 : 100, 4),
                TextBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textColor))
            });
            ChatScroll.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _chat?.Stop();

            if (_settings.ClearOnExit)
            {
                try { if (File.Exists(_historyPath)) File.Delete(_historyPath); } catch { }
            }
            base.OnClosed(e);
        }
    }

    public class ChatMessageItem
    {
        public string Sender { get; set; }
        public string Message { get; set; }
        public int FontSize { get; set; } = 13;
        public Brush BgBrush { get; set; }
        public Brush TextBrush { get; set; }
        public HorizontalAlignment Align { get; set; }
        public Thickness Margin { get; set; }
    }
}
