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
        private string _pidFilePath;
        private ObservableCollection<ChatUser> _userList = new ObservableCollection<ChatUser>();
        private ObservableCollection<ChatMessageItem> _messages = new ObservableCollection<ChatMessageItem>();

        public MainWindow()
        {
            InitializeComponent();
            UserList.ItemsSource = _userList;
            ChatItems.ItemsSource = _messages;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 加载设置
            _settings = ChatSettings.Load();

            // 命令行参数 --nick= 覆盖设置中的昵称
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--nick="))
                    _settings.Nickname = args[i].Substring(7);
            }

            // PID单实例检测
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _pidFilePath = Path.Combine(baseDir, "ChatRoom.pid");
            if (CheckSingleInstance())
            {
                _chat = new ChatUdpService { Nickname = _settings.Nickname };
                _chat.OnUserJoined += OnUserJoined;
                _chat.OnUserLeft += OnUserLeft;
                _chat.OnGroupMessage += OnGroupMessage;
                _chat.OnPrivateMessage += OnPrivateMessage;
                _chat.OnLog += OnServiceLog;
                _chat.Start();

                WritePidFile();
                ApplySettings();
            }
            else
            {
                MessageBox.Show("小小聊天已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
            }
        }

        private void ApplySettings()
        {
            Topmost = _settings.TopMost;
            InputBox.FontSize = _settings.FontSize;
        }

        // === PID单实例 ===

        private bool CheckSingleInstance()
        {
            try
            {
                if (File.Exists(_pidFilePath))
                {
                    string pidStr = File.ReadAllText(_pidFilePath).Trim();
                    if (int.TryParse(pidStr, out int pid))
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(pid);
                            if (!proc.HasExited) return false;
                        }
                        catch { }
                    }
                    File.Delete(_pidFilePath);
                }
            }
            catch { }
            return true;
        }

        private void WritePidFile()
        {
            try { File.WriteAllText(_pidFilePath, System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); } catch { }
        }

        private void CleanPidFile()
        {
            try { if (File.Exists(_pidFilePath)) File.Delete(_pidFilePath); } catch { }
        }

        // === 聊天服务事件 ===

        private void OnUserJoined(ChatUser user)
        {
            Dispatcher.Invoke(() =>
            {
                if (!_userList.Any(u => u.IP == user.IP))
                    _userList.Add(user);
                AddMessage("系统", $"{user.Nickname} 加入了聊天", "#8E8E93", "Left");
            });
        }

        private void OnUserLeft(ChatUser user)
        {
            Dispatcher.Invoke(() =>
            {
                var existing = _userList.FirstOrDefault(u => u.IP == user.IP);
                if (existing != null) _userList.Remove(existing);
                AddMessage("系统", $"{user.Nickname} 离开了聊天", "#8E8E93", "Left");
            });
        }

        private void OnGroupMessage(ChatUser from, string msg)
        {
            Dispatcher.Invoke(() => AddMessage(from.Nickname, msg, "#E8E8ED", "Left"));
        }

        private void OnPrivateMessage(ChatUser from, string msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (_currentTarget != null && _currentTarget.IP == from.IP)
                    AddMessage(from.Nickname + " [私聊]", msg, "#E8E8ED", "Left");
                else
                    AddMessage("📩 " + from.Nickname, msg, "#FFF3CD", "Left");
            });
        }

        private void OnServiceLog(string log)
        {
            Dispatcher.Invoke(() => AddMessage("系统", log, "#F0F0F5", "Left"));
        }

        // === UI事件 ===

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

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _settings = dlg.Settings;
                ApplySettings();
                // 如果昵称变了，更新广播中的昵称
                if (_chat != null)
                {
                    _chat.Nickname = _settings.Nickname;
                }
            }
        }

        private void SendMessage()
        {
            string msg = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            if (_currentTarget != null)
            {
                _chat.SendPrivate(_currentTarget.IP, msg);
                AddMessage("我 → " + _currentTarget.Nickname, msg, "#4A90D9", "Right", "#FFFFFF");
            }
            else
            {
                _chat.SendGroup(msg);
                AddMessage("我", msg, "#4A90D9", "Right", "#FFFFFF");
            }

            InputBox.Clear();
        }

        private void AddMessage(string sender, string message, string bgColor, string align, string textColor = "#000000")
        {
            _messages.Add(new ChatMessageItem
            {
                Sender = sender,
                Message = message,
                FontSize = _settings.FontSize,
                BgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                Align = align == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(align == "Right" ? 80 : 0, 4, align == "Right" ? 0 : 80, 4),
                TextBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textColor))
            });
            ChatScroll.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _chat?.Stop();
            CleanPidFile();
            if (_settings.ClearOnExit) _messages.Clear();
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
