using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
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
        private const int MaxMessages = 500;  // 内存中最多保留500条消息，超出裁剪

        private void TrimMessages()
        {
            while (_messages.Count > MaxMessages)
            {
                // 释放旧图片资源
                var old = _messages[0];
                if (old != null)
                {
                    old.Image = null;                              // 释放位图
                    old.FileBytes = null;                          // 释放收到的文件字节（每条最多 200KB，之前漏了）
                    if (_searchView.Contains(old)) _searchView.Remove(old);   // 搜索结果视图里也去掉引用
                }
                _messages.RemoveAt(0);
            }
        }
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
            _historyPath = Path.Combine(ChatSettings.DataDir, "chat_history.txt");

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
            _chat.OnFileReceived += OnFileReceived;

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
                RotateHistoryIfNeeded();   // 先轮转再读：防止历史文件无限增长
                if (!File.Exists(_historyPath)) return;
                var lines = File.ReadAllLines(_historyPath, System.Text.Encoding.UTF8);
                var recent = lines.Skip(Math.Max(0, lines.Length - 100)).ToList();
                foreach (string line in recent)
                {
                    _loadingHistory = true;   // 见 Bubble_Loaded：历史条目不播放入场动画
                string[] parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        // 新格式: [HH:mm] | conv=<key> | 发送者 | 正文 ；旧格式没有 conv 字段 ⇒ 归入群聊（老历史不丢）
                        int ci = 1;
                        string convKey = Conversation.GroupKey;
                        if (parts.Length > 1 && parts[1].Trim().StartsWith("conv="))
                        {
                            convKey = parts[1].Trim().Substring(5);
                            ci = 2;
                        }
                        if (parts.Length <= ci) continue;
                        string sender = parts[ci].Trim();
                        string msg = parts.Length > ci + 1 ? string.Join("|", parts.Skip(ci + 1)).Trim() : "";
                        string lineTime = parts[0].Trim().Trim('[', ']');
                        _addConvKey = convKey;

                        bool isMe = sender.StartsWith("我");
                        // 图片消息：历史行记的是 [图片]|<落盘路径>，把图还原成图片气泡
                        if (msg.StartsWith("[图片]"))
                        {
                            string imgPath = msg.Length > 4 && msg[4] == '|' ? msg.Substring(5) : "";

                            // 只信任我们自己落盘目录下的图片：历史行可以被对端伪造
                            // （发一句 [图片]|C:\任意路径.jpg 就会被写进历史，下次启动本机去读那个文件并显示）。
                            // 同时兼容"迁移前老数据仍在程序目录"的情况。
                            if (!IsUnderChatImages(imgPath)) imgPath = "";
                            BitmapImage hisImg = null;
                            try { if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath)) hisImg = ChatImageCodec.Decode(File.ReadAllBytes(imgPath)); } catch { }

                            if (hisImg != null)
                                AddImageMessage(sender, hisImg, imgPath, isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, lineTime);
                            else
                                AddMessage(sender, "[图片]（图片已过期）", isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, false, lineTime);
                            _addConvKey = null;   // ★ 图片分支原来漏了这句：continue 会跳过循环尾的复位，
                            continue;             //   于是"以下为新消息"分隔线会被记到这条图片所属的会话里（显示错会话）
                        }
                        // 文件消息：历史行记的是 [文件]|<文件名>|<字节数>。
                        // ★ 故意**不记路径**：历史行可以被对端伪造，若记路径，
                        //   对方发一句 [文件]|x|1|C:\Windows\System32\calc.exe 就能让本机去打开那个本地文件。
                        if (msg.StartsWith("[文件]"))
                        {
                            string rest = msg.Length > 4 && msg[4] == '|' ? msg.Substring(5) : "";
                            string fname = rest;
                            long fsize = 0;
                            int bar = rest.IndexOf('|');
                            if (bar >= 0)
                            {
                                fname = rest.Substring(0, bar);
                                long.TryParse(rest.Substring(bar + 1), out fsize);
                            }
                            AddFileMessage(sender, fname, fsize, null, "",
                                isMe ? BubbleKind.Outgoing : BubbleKind.Incoming);
                            _addConvKey = null;   // continue 会跳过循环尾的复位（同图片分支，必须在这里清）
                            continue;
                        }
                        AddMessage(sender, msg, isMe ? BubbleKind.Outgoing : BubbleKind.Incoming, false, lineTime);
                _addConvKey = null;
                    }
                }
                _loadingHistory = false;   // 必须复位！否则启动后所有新消息都被标记成历史，入场动画永久失效
                if (_messages.Count > 0)
                    AddMessage("系统", "--- 以下为新消息 ---", BubbleKind.Service);
            }
            catch { }
            finally
            {
                // ★ 异常路径也要复位：这里原来只在 try 正常结束时复位，
                //   一旦中途出异常（比如某条历史行触发了某个边界情况），
                //   _loadingHistory 会永久停在 true —— 之后所有真实消息都被当成历史（不播入场动画），
                //   而 _addConvKey 也会一直指着最后那条历史行的会话。
                _loadingHistory = false;
                _addConvKey = null;
            }
        }

        /// <summary>
        /// 聊天记录轮转（豆包 Q12-1）：文件超过 512KB 时，把较早的行归档到 chat_history.1.txt，
        /// 本文件只留最近 2000 行。备份是**覆盖式**写入，所以总量恒定在"512KB + 2000 行"以内，
        /// 不会像"每次追加一个备份"那样越滚越多。
        /// </summary>
        private void RotateHistoryIfNeeded()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;

                long len = new FileInfo(_historyPath).Length;
                if (len < HistoryRotateBytes) return;

                string[] lines = File.ReadAllLines(_historyPath, System.Text.Encoding.UTF8);
                if (lines.Length <= HistoryKeepLines) return;

                int cut = lines.Length - HistoryKeepLines;
                File.WriteAllLines(Path.Combine(ChatSettings.DataDir, "chat_history.1.txt"),
                                   lines.Take(cut), System.Text.Encoding.UTF8);
                File.WriteAllLines(_historyPath, lines.Skip(cut), System.Text.Encoding.UTF8);

                AddMessage("系统", "聊天记录超过上限，较早的 " + cut + " 条已归档到 chat_history.1.txt", BubbleKind.Service);
            }
            catch { }
        }

        private const long HistoryRotateBytes = 512 * 1024;
        private const int HistoryKeepLines = 2000;

        /// <summary>路径是否位于"我们自己落盘的图片目录"内（数据目录或程序目录，兼容迁移前老数据）</summary>
        private static bool IsUnderChatImages(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                foreach (string baseDir in new[] { ChatSettings.DataDir, AppDomain.CurrentDomain.BaseDirectory })
                {
                    string root = Path.GetFullPath(Path.Combine(baseDir, "chat_images"))
                                      .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private void SaveHistoryLine(string sender, string message)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm}] | conv={_addConvKey ?? _currentConvKey} | {sender} | {message}";   // 必须用消息所属会话，不是当前会话
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
            // ★ 第三轮审查修复：关闭过程中调度器已经在关，此时网络线程再来事件，
            //   BeginInvoke 会抛 TaskCanceledException/InvalidOperationException —— 异常发生在**网络接收线程**上，
            //   会被 RecvLoop 的 catch 记成"接收错误"刷日志，看起来像网络故障，其实是关机竞态。
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                if (Dispatcher.CheckAccess()) { action(); return; }
                Dispatcher.BeginInvoke(new Action(action));
            }
            catch (System.Threading.Tasks.TaskCanceledException) { }   // 关闭中排队失败：正常，忽略
            catch (InvalidOperationException) { }    // 同上
        }

        // === 聊天服务事件 ===

        private void OnUserJoined(ChatUser user)
        {
            OnUI(() =>
            {
                // 自己那条（IsMe）不进侧栏 —— 服务侧启动时会把自己放进 _users 用于标识本机
                if (user == null || user.IsMe) return;

                bool isNew = !_userList.Any(u => u.IP == user.IP);
                if (isNew)
                {
                    // ★ 第三轮复核修复：他离线时那一行被移除、行上的红点跟着消失，
                    //   但**会话里的未读还在** —— 新行默认 Unread=0，不恢复就会出现
                    //   "顶栏还数着 1 条、侧栏那一行却没红点"的不一致。
                    //   （心跳上线的路径走这里直接加行，所以恢复逻辑必须放在这，不能只放同步循环里）
                    Conversation pc;
                    if (_conversations.TryGetValue(Conversation.PeerKey(user.IP), out pc) && pc.Unread > 0)
                        user.Unread = pc.Unread;

                    _userList.Add(user);
                    MarkJustJoined(user.IP);   // 动效 2：这一行播放 3s 渐入
                }
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
            // 只数**别人**（自己那条 IsMe 不进侧栏，也不能算进在线人数）
            int count = _userList.Count(u => u.IsOnline && !u.IsMe);
            UserCount.Text = $"({count})";
        }

        private void OnGroupMessage(ChatUser from, string msg)
        {
            OnUI(() =>
            {
                _addConvKey = Conversation.GroupKey;   // 群聊消息固定归群聊会话
                AddMessage(from.Nickname, msg, BubbleKind.Incoming);
                SaveHistoryLine(from.Nickname, msg);
                _addConvKey = null;   // 写历史必须用消息所属会话
            });
        }

        private void OnPrivateMessage(ChatUser from, string msg)
        {
            OnUI(() =>
            {
                if (_currentTarget != null && _currentTarget.IP == from.IP)
                {
                    _addConvKey = Conversation.PeerKey(from.IP);   // 私聊消息归该会话
                    AddMessage(from.Nickname + " [私聊]", msg, BubbleKind.Incoming);
                }
                else
                {
                    _addConvKey = Conversation.PeerKey(from.IP);
                    AddMessage("📩 " + from.Nickname, msg, BubbleKind.Incoming);
                    _addConvKey = null;
                }
                _addConvKey = Conversation.PeerKey(from.IP);   // 写历史必须用消息所属会话
                SaveHistoryLine(from.Nickname + "[私聊]", msg);
                _addConvKey = null;
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

                string dir = Path.Combine(ChatSettings.DataDir, "chat_images");
                string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (from.Nickname ?? "未知");
                string saved = ChatImageCodec.SaveTo(dir, name, jpeg);

                string label = scope == "P" ? (from.Nickname + " [私聊图片]") : from.Nickname;
                _addConvKey = IncomingConvKey(scope, from.IP);   // 图片按 scope 归会话
                AddImageMessage(label, bmp, saved, BubbleKind.Incoming);
                _addConvKey = IncomingConvKey(scope, from.IP);   // 写历史必须用消息所属会话
                SaveHistoryLine(from.Nickname + (scope == "P" ? "[私聊]" : ""), "[图片]|" + saved);
                _addConvKey = null;
                _addConvKey = null;
            });
        }

        // === 图片 / 文件 ===

        /// <summary>按扩展名判定"这是图片"的集合（其余一律走文件通道）</summary>
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
        /// <summary>收到这些扩展名要红色警告（豆包 Q5 点名的四种）</summary>
        private static readonly string[] RiskyExtensions = { ".exe", ".bat", ".cmd", ".scr" };

        /// <summary>
        /// 📎 按钮：一个入口同时发图片和文件（豆包 Q5 选 A），按扩展名自动分流。
        /// 图片走压缩→CIMG；其余走原字节→CFIL。
        /// </summary>
        private void BtnImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要发送的图片或文件",
                Filter = "图片或文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.txt;*.log;*.json;*.xml;*.csv;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.zip;*.rar;*.7z|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string ext = "";
            try { ext = Path.GetExtension(dlg.FileName).ToLowerInvariant(); } catch { }

            if (Array.IndexOf(ImageExtensions, ext) >= 0) SendImageFile(dlg.FileName);
            else SendDataFile(dlg.FileName);
        }

        /// <summary>把 50KB 这类字节数说成"12.3 KB"</summary>
        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / 1048576.0).ToString("0.#") + " MB";
        }

        private static bool IsRiskyFile(string name)
        {
            string ext = "";
            try { ext = Path.GetExtension(name ?? "").ToLowerInvariant(); } catch { }
            return Array.IndexOf(RiskyExtensions, ext) >= 0;
        }

        /// <summary>取安全的文件名：只留文件名部分 + 去掉非法字符（防止对方用 "..\..\x.exe" 这类名字做路径穿越）</summary>
        private static string SafeFileName(string name)
        {
            try
            {
                string n = Path.GetFileName(name ?? "");
                foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
                n = (n ?? "").Trim();
                return n.Length == 0 ? "file" : n;
            }
            catch { return "file"; }
        }

        /// <summary>把图片压缩后经 CIMG 发出</summary>
        private void SendImageFile(string path)
        {
            try
            {
                byte[] jpeg = ChatImageCodec.EncodeFile(path);
                if (jpeg == null || jpeg.Length == 0) { MessageBox.Show("图片读取失败。", "提示"); return; }

                if (jpeg.Length / 3 * 4 > ChatUdpService.ImageMaxChars)
                {
                    MessageBox.Show("这张图太大了（压缩后 " + (jpeg.Length / 1024) + "KB），请换一张更小的。", "提示");
                    return;
                }

                // 目标离线时单播等于发给空气。但绝不能"静默改群发" —— 私聊图片被广播给全组是隐私事故，
                // 所以这里是**询问**而不是自动决定（用户确认后才群发）。
                string target = _currentTarget != null ? _currentTarget.IP : null;
                if (_currentTarget != null && !_currentTarget.IsOnline)
                {
                    var ask = MessageBox.Show(
                        _currentTarget.Nickname + " 似乎已离线。是否改为群发这张图片？",
                        "对方离线", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) return;
                    target = null;
                }
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

        /// <summary>
        /// 把文件原字节经 CFIL 发出。上限 200KB（豆包 Q4 选 A：**发送前直接拒绝 + 红字提示**，不发分片）。
        /// </summary>
        private void SendDataFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { MessageBox.Show("文件读不到。", "提示"); return; }
                if (fi.Length == 0) { MessageBox.Show("这是个空文件，没有内容可发。", "提示"); return; }

                if (fi.Length > ChatUdpService.FileMaxBytes)
                {
                    AddMessage("系统",
                        "文件「" + fi.Name + "」" + FormatSize(fi.Length) + " 超过 " +
                        (ChatUdpService.FileMaxBytes / 1024) + "KB 上限，未发送",
                        BubbleKind.Warning);
                    return;
                }

                string target = _currentTarget != null ? _currentTarget.IP : null;
                if (_currentTarget != null && !_currentTarget.IsOnline)
                {
                    var ask = MessageBox.Show(
                        _currentTarget.Nickname + " 似乎已离线。是否改为群发这个文件？",
                        "对方离线", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) return;
                    target = null;
                }

                byte[] data = File.ReadAllBytes(path);
                string name = Path.GetFileName(path);
                _chat.SendFile(data, name, target);

                string label = target == null ? "我（群发）" : ("我 → " + _currentTarget.Nickname);
                AddFileMessage(label, name, data.Length, null, path, BubbleKind.Outgoing);
                SaveHistoryLine("我", "[文件]|" + name + "|" + data.Length);
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送文件失败：" + ex.Message, "错误");
            }
        }

        /// <summary>收到一个完整文件（豆包 #6）：显示文件卡片，**不自动落盘**，等用户点它再自己选位置</summary>
        private void OnFileReceived(ChatUser from, string fileName, byte[] data, string scope)
        {
            OnUI(() =>
            {
                string label = scope == "P" ? (from.Nickname + " [私聊文件]") : from.Nickname;
                _addConvKey = IncomingConvKey(scope, from.IP);   // 文件按 scope 归会话

                AddFileMessage(label, fileName, data.Length, data, "", BubbleKind.Incoming);
                SaveHistoryLine(from.Nickname + (scope == "P" ? "[私聊]" : ""),
                    "[文件]|" + fileName + "|" + data.Length);

                // 可执行文件：追加一条红色警告（豆包 Q5 要求"红色警告"）
                if (IsRiskyFile(fileName))
                    AddMessage("系统", "⚠ 收到可执行文件「" + fileName + "」，请确认来源可信后再保存/运行", BubbleKind.Warning);

                _addConvKey = null;
            });
        }

        /// <summary>追加一条文件消息（气泡里显示文件名 + 大小 + 提示；可执行文件名标红）</summary>
        private void AddFileMessage(string sender, string fileName, long sizeBytes, byte[] bytes, string localPath, BubbleKind kind)
        {
            ApplyBubbleStyle(kind, out Brush bg, out Brush border, out Brush fg, out Brush secondary);
            bool right = kind == BubbleKind.Outgoing;
            bool risky = IsRiskyFile(fileName);
            string safe = SafeFileName(fileName);
            string sizeText = FormatSize(sizeBytes);

            string hint = bytes != null && bytes.Length > 0 ? "点击另存为…"
                        : (!string.IsNullOrEmpty(localPath) && File.Exists(localPath)) ? "点击打开"
                        : "内容不在本机（历史只保留文件名和大小）";

            _messages.Add(new ChatMessageItem
            {
                Sender = sender,
                Message = "[文件] " + safe + " (" + sizeText + ")",
                IsFile = true,
                FileName = safe,
                FileSizeText = sizeText,
                FileBytes = bytes,
                FilePath = !string.IsNullOrEmpty(localPath) && File.Exists(localPath) ? localPath : "",
                FileIsRisky = risky,
                FileHint = hint,
                FileNameBrush = risky ? Theme.Get("WarnFg") : fg,
                FileCardBg = Theme.Get("FileCardBg"),
                FileCardBorder = Theme.Get("FileCardBorder"),
                IsHistory = _loadingHistory,
                ConvKey = _addConvKey ?? _currentConvKey,
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

            if (kind == BubbleKind.Incoming) TrackUnread(true);
            AutoScroll();
            TrimMessages();
        }

        /// <summary>点文件卡片：收到的文件→弹出"另存为"让用户自己选位置；自己发的→打开；历史条目→如实提示</summary>
        private void File_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ChatMessageItem item = (sender as FrameworkElement)?.DataContext as ChatMessageItem;
            if (item == null || !item.IsFile) return;
            e.Handled = true;   // 别让这次点击再穿透到气泡本身

            if (item.FileBytes != null && item.FileBytes.Length > 0)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存收到的文件",
                    FileName = SafeFileName(item.FileName),
                    Filter = "所有文件|*.*",
                    AddExtension = false
                };
                try
                {
                    string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (Directory.Exists(dl)) dlg.InitialDirectory = dl;
                }
                catch { }
                if (dlg.ShowDialog() != true) return;

                try
                {
                    File.WriteAllBytes(dlg.FileName, item.FileBytes);
                    AddMessage("系统", "已保存到 " + dlg.FileName, BubbleKind.Service);
                }
                catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message, "提示"); }
                return;
            }

            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                try { System.Diagnostics.Process.Start(item.FilePath); }
                catch (Exception ex) { MessageBox.Show("打开文件失败：" + ex.Message, "提示"); }
                return;
            }

            MessageBox.Show("这个文件的内容不在本机（历史记录只保留文件名和大小）。\n需要的话请让对方重发一次。", "提示");
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
                IsHistory = _loadingHistory,
                ConvKey = _addConvKey ?? _currentConvKey,
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
            TrimMessages();   // 500 条上限（这个方法原来定义了但从未被调用 ⇒ 上限完全没生效）
        }

        // === 未读 / 滚动 ===

        private int _unreadCount = 0;
        private bool _loadingHistory = false;   // 历史批量加载时不逐条播放入场动画

        /// <summary>
        /// 未读计数：只在用户"没在看"时累加（最小化或窗口不在前台）。
        /// 第一次出现未读时插入一条服务分隔线（Telegram 的 unread divider 概念），
        /// 之后回来的用户往上翻就能看到"从这里开始是新消息"。
        /// </summary>
        /// <summary>
        /// 未读计数 + 侧栏行徽标 + 内嵌弹窗。
        ///
        /// 判定语义（之前写错过，用户实测踩到）：**这条消息不属于我正在看的那个会话**就算未读，
        /// 与"窗口是否在前台"无关 —— 原来只判前台，导致"我在群聊里聊天时别人私聊我"既不计数也不提示。
        /// </summary>
        /// <summary>把会话键翻成给人看的标题：群聊 -> 群聊，peer:IP -> 该用户昵称（找不到就退回 IP）</summary>
        private string NicknameOfConvKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key == Conversation.GroupKey) return "群聊";
            string ip = key.StartsWith("peer:") ? key.Substring(5) : key;
            foreach (ChatUser u in _userList) { if (u.IP == ip) return u.Nickname; }
            foreach (ChatUser u in _chat != null ? _chat.SnapshotUsers() : new System.Collections.Generic.List<ChatUser>())
                if (u.IP == ip) return u.Nickname;
            return ip;
        }

        private void TrackUnread(bool incoming)
        {
            if (!incoming) return;

            // ★ 历史回放不是"新消息"：LoadHistory 也走 AddMessage，所以必须在这里显式挡住。
            //   之前这里没有这道判断，启动时是否冒出幽灵未读完全取决于"加载那一刻 IsActive 是不是 false、
            //   以及随后 SwitchConversation 有没有清掉当前会话"—— 实测结果是 0（正确），
            //   但那是时序巧合，任何一处改动都可能让用户每次启动都看到一堆假未读。现在把它写死。
            if (_loadingHistory) return;

            string key = _addConvKey ?? _currentConvKey;
            bool viewingThisConv = (key == _currentConvKey) && IsActive && WindowState != WindowState.Minimized;
            if (viewingThisConv) return;

            Conversation c = EnsureConversation(key,
                key == Conversation.GroupKey ? "群聊" : NicknameOfConvKey(key), key == Conversation.GroupKey, "");
            c.Unread++;

            // 私聊要同步侧栏那一行的徽标。IP 从会话键反解，不能读 c.PeerIP：
            // 首次收到某人私聊时会话是这一行才建的，PeerIP 还是空串，用它等于没更新。
            if (!c.IsGroup) SetPeerUnread(Conversation.PeerIpOf(c.Key), c.Unread);
            c.SeparatorShown = true;
            UpdateUnreadBadge();
            PopElement(BtnUnread);

            ShowToast(c.Key, c.Title, LastPreview());   // 同会话连续消息会合并成"N 条新消息"（Q6）
        }

        /// <summary>弹窗里的预览文本：取该会话最后一条消息</summary>
        private string LastPreview()
        {
            string key = _addConvKey ?? _currentConvKey;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                ChatMessageItem m = _messages[i];
                if (m != null && m.ConvKey == key)
                    return m.IsImage ? "[图片]" : (m.Sender + ": " + m.Message);
            }
            return "";
        }

        private void ClearUnread()
        {
            // 清掉所有会话的未读数，否则下次 UpdateUnreadBadge 会从旧数据重新累加。
            // ★ 侧栏行必须用会话键反解 IP（c.PeerIP 可能是空串，见 ClearConversationUnread 的说明）
            foreach (var c in _conversations.Values)
            {
                c.Unread = 0;
                if (!c.IsGroup) SetPeerUnread(Conversation.PeerIpOf(c.Key), 0);
            }
            UpdateUnreadBadge();   // 内部会把顶栏总数、托盘计数、跑马灯一起同步
        }

        private void BtnUnread_Click(object sender, RoutedEventArgs e)
        {
            ChatScroll.ScrollToEnd();
            ClearUnread();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            // ★ 第三轮复核修复：回到前台只能说明"用户看到了**当前这个**会话"，
            //   绝不能把其它会话的未读一起清掉 —— 否则用户切出去（比如去看浏览器）再切回来，
            //   那些压根没看过的私聊未读就被静默清空了，侧栏红点也跟着消失。
            //   原来这里调的是 ClearUnread()（清全部），属于误清。
            if (CurrentConversation.Unread > 0) ClearConversationUnread(_currentConvKey);
        }

        /// <summary>
        /// 只有"本来就在底部附近"才自动滚动 —— 否则用户正在往上翻历史，被强行拽到底部很烦。
        /// </summary>
        private void AutoScroll()
        {
            if (ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 40) ChatScroll.ScrollToEnd();
        }

        // === 动效（参考 COUI：spring / animateFloatAsState / graphicsLayer） ===

        /// <summary>
        /// 气泡入场：淡入 + 上移 8px，缓动用 BackEase（近似 COUI 的 spring，略微过冲后回落）。
        /// 历史条目直接跳过 —— 否则启动时上百条会一起飞入。
        /// </summary>
        private void Bubble_Loaded(object sender, RoutedEventArgs e)
        {
            FrameworkElement el = sender as FrameworkElement;
            if (el == null) return;

            ChatMessageItem item = el.DataContext as ChatMessageItem;
            if (item != null && item.IsHistory) return;

            el.Opacity = 0;
            var tt = new TranslateTransform(0, 8);
            el.RenderTransform = tt;

            var sb = new Storyboard();
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));

            var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, tt);
            Storyboard.SetTargetProperty(slide, new PropertyPath("Y"));

            sb.Children.Add(fade);
            sb.Children.Add(slide);
            sb.Begin();
        }

        /// <summary>元素"弹一下"（未读徽标出现/数字变化时用；一次性，不循环，避免一直闪）</summary>
        private static void PopElement(FrameworkElement el)
        {
            if (el == null) return;
            var st = new ScaleTransform(0.75, 0.75);
            el.RenderTransform = st;

            var sb = new Storyboard();
            var sx = new DoubleAnimation(0.75, 1.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new BackEase { Amplitude = 0.8, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(sx, st);
            Storyboard.SetTargetProperty(sx, new PropertyPath("ScaleX"));
            var sy = sx.Clone();
            Storyboard.SetTarget(sy, st);
            Storyboard.SetTargetProperty(sy, new PropertyPath("ScaleY"));
            sb.Children.Add(sx);
            sb.Children.Add(sy);
            sb.Begin();
        }

        // === 右键菜单 ===


        private void MsgMenu_Copy(object sender, RoutedEventArgs e)
        {
            ChatMessageItem item = ((FrameworkElement)sender).DataContext as ChatMessageItem;
            if (item == null) return;
            try { Clipboard.SetText(item.Message ?? ""); }
            catch { }
        }

        /// <summary>引用回复：把引文拼在输入框前面（纯文本，不动协议，对方看到的也是普通文字）</summary>
        private void MsgMenu_Quote(object sender, RoutedEventArgs e)
        {
            ChatMessageItem item = ((FrameworkElement)sender).DataContext as ChatMessageItem;
            if (item == null) return;
            string quoted = item.Message ?? "";
            if (quoted.Length > 60) quoted = quoted.Substring(0, 60) + "…";
            InputBox.Text = "> " + item.Sender + "：" + quoted + "  →  " + InputBox.Text;
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        private void MsgMenu_Delete(object sender, RoutedEventArgs e)
        {
            ChatMessageItem item = ((FrameworkElement)sender).DataContext as ChatMessageItem;
            if (item == null) return;
            _messages.Remove(item);

            // 历史文件里也删掉对应的那一条（只删第一条匹配的）
            try
            {
                if (File.Exists(_historyPath))
                {
                    string needle = "| " + item.Sender + " | " + item.Message;
                    var kept = File.ReadAllLines(_historyPath, System.Text.Encoding.UTF8)
                                   .Where(l => !l.Contains(needle)).ToArray();
                    File.WriteAllLines(_historyPath, kept, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>导出当前会话为文本（含时间/发送者/内容）</summary>
        private void MsgMenu_Export(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出聊天记录",
                Filter = "文本文件|*.txt",
                FileName = "chat_export_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (ChatMessageItem m in _messages)
                    sb.AppendLine("[" + m.Time + "] " + m.Sender + ": " + (m.IsImage ? "[图片] " + m.ImagePath : m.Message));
                File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                AddMessage("系统", "已导出 " + _messages.Count + " 条到 " + System.IO.Path.GetFileName(dlg.FileName), BubbleKind.Service);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "错误");
            }
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
            SwitchConversation(Conversation.PeerKey(user.IP), user.Nickname, false, user.IP);   // 会话分离：切换到这个私聊
            ChatTitle.Text = $"私聊: {user.Nickname}";
        }

        private void BtnGroupChat_Click(object sender, RoutedEventArgs e)
        {
            _currentTarget = null;
            SwitchConversation(Conversation.GroupKey, "群聊", true, "");   // 会话分离：切回群聊
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
            // 统一走 Close()：由 OnClosing 判定"托盘可用就藏起来、托盘不可用就直接退出"。
            // 原来这里直接 Hide() 会绕过那个判定 ⇒ 托盘失败时窗口藏了既唤不回也退不掉。
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

            bool sentOk;
            if (_currentTarget != null)
            {
                sentOk = _chat.SendPrivate(_currentTarget.IP, msg);
                AddMessage("我 → " + _currentTarget.Nickname, msg, BubbleKind.Outgoing);
                SaveHistoryLine("我[私聊]", msg);
            }
            else
            {
                sentOk = _chat.SendGroup(msg);
                AddMessage("我", msg, BubbleKind.Outgoing);
                SaveHistoryLine("我", msg);
            }

            // 动效 1：气泡出现时从「发送」按钮位置扩一圈光圈（总开关关闭时内部直接返回）
            PlaySendRipple();

            // 发送失败要给可见反馈（对端离线、正文超过单个 UDP 包上限等 ⇒ 原来静默丢失）
            if (!sentOk) AddMessage("系统", "消息发送失败（对方可能已离线，或内容过长超过单个 UDP 包上限）", BubbleKind.Service);

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
                IsHistory = _loadingHistory,
                ConvKey = _addConvKey ?? _currentConvKey,
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
            TrimMessages();   // 500 条上限（这个方法原来定义了但从未被调用 ⇒ 上限完全没生效）
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
                case BubbleKind.Warning:
                    // 警告行（文件超限、收到可执行文件）：底同服务消息，文字走红色
                    bg = Theme.Get("ServiceBg"); border = Theme.Get("ServiceBg"); fg = Theme.Get("WarnFg");
                    secondary = Theme.Get("WarnFg");
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

        private bool _isExiting = false;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                // 只有托盘真的可用时才"关到托盘"；托盘失败时必须放行关闭，
                // 否则窗口藏起来又没托盘，用户既唤不回也退不掉，只能杀进程。
                if (_trayReady)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _chat?.Stop();

            // === 第三轮审查：释放 UI 侧资源 ===
            // 托盘图标/1s 托盘定时器已经在 InitTray 的 Closed 里停掉并 Dispose 了，这里补上剩下的：
            // ① 提醒窗的定时器与窗口本身（Topmost 小窗，不主动关要等进程退出才消失）
            // ② 静态事件 Application.SessionEnding 的退订（它是**静态**事件，
            //    订阅了不退订会让已关闭的窗口被 Application 一直引用）
            try { if (_toastTimer != null) { _toastTimer.Stop(); _toastTimer = null; } } catch { }
            try { if (_toastWindow != null) { _toastWindow.Close(); _toastWindow = null; } } catch { }
            try { if (Application.Current != null) Application.Current.SessionEnding -= OnSessionEnding; } catch { }
            // ③ 退订服务事件（Stop() 已经先停掉了线程，这里是双保险）
            try
            {
                if (_chat != null)
                {
                    _chat.OnUserJoined -= OnUserJoined;
                    _chat.OnUserLeft -= OnUserLeft;
                    _chat.OnGroupMessage -= OnGroupMessage;
                    _chat.OnPrivateMessage -= OnPrivateMessage;
                    _chat.OnLog -= OnServiceLog;
                    _chat.OnImageReceived -= OnImageReceived;
                    _chat.OnFileReceived -= OnFileReceived;
                }
            }
            catch { }

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
    public enum BubbleKind { Incoming, Outgoing, Service, Warning }

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
        /// <summary>历史加载出来的条目：不播放入场动画（否则启动时上百条一起飞入）</summary>
        public bool IsHistory { get; set; }

        /// <summary>所属会话键（"group" 或 "peer:IP"）——会话分离用</summary>
        public string ConvKey { get; set; } = Models.Conversation.GroupKey;
        public bool IsImage { get; set; }
        public System.Windows.Media.ImageSource Image { get; set; }
        /// <summary>落盘路径（点开查看用；自己发出的那条本地显示没有路径）</summary>
        public string ImagePath { get; set; } = "";

        // === 文件分享（豆包需求 #6）===
        /// <summary>是否文件消息（气泡模板据此显示文件卡片、并隐藏正文）</summary>
        public bool IsFile { get; set; }
        public string FileName { get; set; } = "";
        public string FileSizeText { get; set; } = "";
        /// <summary>收到的文件内容。**只存在内存里**（豆包 Q3：不自动落盘，等用户点气泡自己选位置）</summary>
        public byte[] FileBytes { get; set; }
        /// <summary>自己发出的文件在本机的路径（仅本次运行；历史行不写路径，避免"历史可被伪造成本地任意文件"）</summary>
        public string FilePath { get; set; } = "";
        /// <summary>可执行文件（.exe/.bat/.cmd/.scr）：文件名标红 + 追加一条红色警告行</summary>
        public bool FileIsRisky { get; set; }
        /// <summary>卡片第二行的提示文案：点击另存为 / 点击打开 / 内容不在本机</summary>
        public string FileHint { get; set; } = "";
        public Brush FileNameBrush { get; set; }
        public Brush FileCardBg { get; set; }
        public Brush FileCardBorder { get; set; }
        public HorizontalAlignment Align { get; set; }
        public Thickness Margin { get; set; }
    }
}
