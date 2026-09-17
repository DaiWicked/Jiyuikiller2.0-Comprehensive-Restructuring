using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatRoom.Models;
using ChatRoom.Services;

namespace ChatRoom
{
    /// <summary>
    /// MainWindow 的第三阶段扩展（partial，独立文件，尽量不改主文件）：
    ///   · 历史搜索（Ctrl+F / 顶栏 🔍）：把消息列表临时切换成"命中列表"，关闭即恢复
    ///   · emoji 面板（Ctrl+E / 输入区 😀）：本地 Unicode，无网络依赖
    ///   · 键盘操作：Ctrl+F 搜索、Ctrl+E emoji、Esc 关闭/清空、↑↓ 选会话
    ///   · 托盘增强：图标 + 未读数提示 + 右键菜单（显示/隐藏、设置、退出）+ 双击唤回
    ///
    /// 为什么单独成文件：主窗口文件已经很大，而且它是我和豆包都要小心的地方；
    /// 新增功能放 partial 里，冲突面最小。
    /// </summary>
    public partial class MainWindow
    {
        // ==================== 历史搜索 ====================

        private readonly ObservableCollection<ChatMessageItem> _searchView = new ObservableCollection<ChatMessageItem>();
        private bool _searchMode = false;

        /// <summary>窗口加载完成后做一次性装配（搜索框、emoji 面板、托盘）</summary>
        private void Extras_Loaded(object sender, RoutedEventArgs e)
        {
            BuildEmojiPanel();
            InitTray();
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_searchMode) CloseSearch();
            else OpenSearch();
        }

        private void OpenSearch()
        {
            _searchMode = true;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
            RunSearch();
        }

        private void CloseSearch()
        {
            _searchMode = false;
            SearchBar.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
            ChatItems.ItemsSource = _messages;      // 恢复完整列表（搜索期间新消息仍进 _messages）
            ChatScroll.ScrollToEnd();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchMode) RunSearch();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        }

        private void BtnSearchClose_Click(object sender, RoutedEventArgs e)
        {
            CloseSearch();
        }

        /// <summary>在内存消息里做不区分大小写的包含搜索，命中列表直接替换显示源</summary>
        private void RunSearch()
        {
            string q = (SearchBox.Text ?? "").Trim();
            _searchView.Clear();

            if (q.Length > 0)
            {
                foreach (ChatMessageItem m in _messages)
                {
                    if (m.IsImage) continue;
                    if ((m.Message ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || (m.Sender ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _searchView.Add(m);
                    }
                }
            }

            ChatItems.ItemsSource = _searchView;
            TextSearchInfo.Text = q.Length == 0
                ? "输入关键词搜索聊天记录"
                : ("找到 " + _searchView.Count + " 条");
        }

        // ==================== emoji 面板 ====================

        private void BuildEmojiPanel()
        {
            if (EmojiPanel == null || EmojiPanel.Children.Count > 0) return;

            // Win7 没有 Segoe UI Emoji（也没有任何彩色 emoji 字体），U+1F600 以上的字符会显示成方框。
            // => 老系统改用 BMP 区段的符号集（Segoe UI Symbol / Arial Unicode 都自带），字体写成回退链。
            bool legacy = Environment.OSVersion.Version < new Version(6, 2);   // 6.2 = Windows 8
            string[] emojis = legacy ? new[]
            {
                "☺","☻","☹","☀","☁","☂","☃","★","☆","♥","♦","♣","♠","♪","♫","✿",
                "✔","✖","✚","❄","❖","●","○","◆","◇","■","□","▲","▼","◀","▶","※",
                "←","→","↑","↓","↔","↕","➤","⚠","⚡","⚙","⚔","⚖","⌛","☕","☎","✎",
                "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫","⑬","⑭","⑮","⑯"
            } : new[]
            {
                "😀","😄","😁","😆","😅","😂","🙂","😉","😊","😍","😘","😜","🤔","😐","😴","😭",
                "😡","👍","👎","👌","✌️","🙏","👏","💪","🤝","❤️","💔","⭐","🔥","✨","🎉","🎁",
                "🍚","🍜","🍎","☕","⚽","🎮","📚","💻","📱","🌈","☀️","🌙","⚡","💧","🌸","🍀",
                "✅","❌","❓","❗","⚠️","🔔","📢","🎵","🚀","🏆","💰","🕐","📍","📷","📎","💬"
            };

            foreach (string s in emojis)
            {
                var b = new Button
                {
                    Content = s,
                    Width = 34,
                    Height = 32,
                    Margin = new Thickness(1),
                    FontSize = 17,
                    // 字体回退链：Win8.1+ 用 Segoe UI Emoji（彩色）；Win7 只能靠 Segoe UI Symbol
                    FontFamily = new FontFamily(legacy ? "Segoe UI Symbol, Arial Unicode MS, Segoe UI" : "Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = s
                };
                b.Click += Emoji_Click;
                EmojiPanel.Children.Add(b);
            }
        }

        private void BtnEmoji_Click(object sender, RoutedEventArgs e)
        {
            EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
            if (EmojiPopup.IsOpen) BuildEmojiPanel();
        }

        /// <summary>把 emoji 插到光标处（不是简单追加，保持用户正在编辑的位置）</summary>
        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            var b = sender as Button;
            if (b == null) return;
            string s = b.Content as string ?? "";
            if (s.Length == 0) return;

            InputBox.Text = InputBox.Text.Insert(InputBox.CaretIndex, s);
            InputBox.CaretIndex += s.Length;
            InputBox.Focus();
            EmojiPopup.IsOpen = false;
        }

        // ==================== 键盘操作 ====================

        /// <summary>
        /// 窗口级快捷键（用 PreviewKeyDown，不干扰输入框自己的 Enter 发送逻辑）。
        /// Ctrl+F 搜索 / Ctrl+E emoji / Esc 关搜索或清空输入 / ↑↓ 切换会话。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (ctrl && e.Key == Key.F) { OpenSearch(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.E) { BtnEmoji_Click(null, null); e.Handled = true; return; }

            if (e.Key == Key.Escape)
            {
                if (EmojiPopup != null && EmojiPopup.IsOpen) { EmojiPopup.IsOpen = false; e.Handled = true; return; }
                if (_searchMode) { CloseSearch(); e.Handled = true; return; }
                if (InputBox.Text.Length > 0) { InputBox.Clear(); e.Handled = true; return; }
                return;
            }

            // 输入框获得焦点时不抢上下键（要留给文本光标移动）
            if (InputBox.IsKeyboardFocusWithin) return;

            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                if (_userList.Count == 0) return;
                int idx = UserList.SelectedIndex;
                idx += (e.Key == Key.Down) ? 1 : -1;
                if (idx < 0) idx = _userList.Count - 1;
                if (idx >= _userList.Count) idx = 0;
                UserList.SelectedIndex = idx;
                UserList.ScrollIntoView(UserList.SelectedItem);
                e.Handled = true;
            }
        }

        // ==================== 托盘增强 ====================

        private System.Windows.Forms.NotifyIcon _tray;
        private DispatcherTimer _trayTimer;

        private void InitTray()
        {
            try
            {
                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = LoadTrayIcon(),
                    Text = "小小聊天",
                    Visible = true
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("显示 / 隐藏窗口", null, (s, a) => ToggleWindowVisible());
                menu.Items.Add("设置…", null, (s, a) => BtnSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("退出", null, (s, a) => { _isExiting = true; Application.Current.Shutdown(); });
                _tray.ContextMenuStrip = menu;

                _tray.DoubleClick += (s, a) => { Show(); WindowState = WindowState.Normal; Activate(); };

                // 未读数同步到托盘提示（NotifyIcon.Text 上限 63 字符，这里很短）
                _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _trayTimer.Tick += (s, a) =>
                {
                    if (_tray == null) return;
                    _tray.Text = _unreadCount > 0 ? ("小小聊天 · " + _unreadCount + " 条新消息") : "小小聊天";
                };
                _trayTimer.Start();

                Closed += (s, a) =>
                {
                    try { if (_trayTimer != null) _trayTimer.Stop(); } catch { }
                    try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
                };
            }
            catch { /* 托盘失败不影响主功能 */ }
        }

        private void ToggleWindowVisible()
        {
            if (IsVisible) Hide();
            else { Show(); WindowState = WindowState.Normal; Activate(); }
        }

        /// <summary>取程序图标；取不到就退回系统默认图标（不让托盘初始化失败）</summary>
        private System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/ChatRoom;component/Assets/chat.ico");
                var stream = Application.GetResourceStream(uri);
                if (stream != null) return new System.Drawing.Icon(stream.Stream);
            }
            catch { }

            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "chat.ico");
                if (File.Exists(p)) return new System.Drawing.Icon(p);
            }
            catch { }

            return System.Drawing.SystemIcons.Application;
        }
    }
}
