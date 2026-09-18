using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using ChatRoom.Models;

namespace ChatRoom
{
    /// <summary>
    /// 会话分离（豆包需求 #1）：解决"私聊和群聊界面重合"。
    ///
    /// 做法（低风险）：消息实体仍只存一份 MainWindow._messages，每条消息用 ConvKey 标记归属；
    /// 界面用 CollectionView 按当前会话过滤显示。切换会话只改过滤条件并 Refresh，
    /// 收发 / 历史 / 搜索 / 图片防伪造这些已验证的逻辑都不用推倒重来。
    ///
    /// 会话选择器复用现有侧栏：点某个在线用户 = 进入与他的私聊会话；点"群聊广播" = 回群聊会话。
    /// （后续可把侧栏升级成带未读徽标的 Telegram 式会话列表，属纯外观演进。）
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 应用聊天区壁纸（豆包需求 #7，按她的修正版实现）。
        ///
        /// ★ 语义修正：滑块调的是**模糊度**，不是透明度。
        ///   壁纸始终完全不透明显示（不再设 ImageBrush.Opacity），模糊只加在壁纸层上；
        ///   气泡和输入区在壁纸层之上、保持不透明与清晰。
        ///   没有壁纸时不加模糊，直接用主题色。
        /// </summary>
        public void ApplyWallpaper()
        {
            try
            {
                string p = _settings != null ? _settings.WallpaperPath : null;
                if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(p, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();

                    WallpaperBrush.ImageSource = bmp;
                    WallpaperBlurEffect.Radius = Math.Max(0, Math.Min(10, _settings.WallpaperBlur));
                    WallpaperLayer.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch { }

            // 没壁纸（或图片读不出来）：隐藏壁纸层 ⇒ 模糊自然不生效，露出主题色底
            WallpaperLayer.Visibility = Visibility.Collapsed;
            WallpaperBrush.ImageSource = null;
        }
        private readonly Dictionary<string, Conversation> _conversations = new Dictionary<string, Conversation>();
        private string _currentConvKey = Conversation.GroupKey;
        private CollectionView _messagesView;

        /// <summary>当前要写入消息的会话键；为空时用"当前会话"（自己发的消息走这里）</summary>
        private string _addConvKey;

        /// <summary>建/取会话</summary>
        private Conversation EnsureConversation(string key, string title, bool isGroup, string peerIP)
        {
            Conversation c;
            if (!_conversations.TryGetValue(key, out c))
            {
                c = new Conversation { Key = key, Title = title, IsGroup = isGroup, PeerIP = peerIP ?? "" };
                _conversations[key] = c;
            }
            else
            {
                if (!string.IsNullOrEmpty(title)) c.Title = title;
                if (string.IsNullOrEmpty(c.PeerIP) && !string.IsNullOrEmpty(peerIP)) c.PeerIP = peerIP;
            }
            return c;
        }

        private Conversation CurrentConversation
        {
            get
            {
                if (!_conversations.ContainsKey(_currentConvKey))
                    EnsureConversation(_currentConvKey, _currentConvKey == Conversation.GroupKey ? "群聊" : _currentConvKey, _currentConvKey == Conversation.GroupKey, "");
                return _conversations[_currentConvKey];
            }
        }

        /// <summary>切换会话：只换过滤条件，历史/搜索等骨架不动</summary>
        private void SwitchConversation(string key, string title, bool isGroup, string peerIP)
        {
            EnsureConversation(key, title, isGroup, peerIP);
            _currentConvKey = key;
            ClearConversationUnread(key);   // 会话未读 + 侧栏行 + 顶栏总数 + 跑马灯，一次到位
            CurrentConversation.SeparatorShown = false;
            RefreshConversationView();
            UpdateUnreadBadge();
        }

        /// <summary>重新套用当前会话过滤（消息增删/切换会话后调用）</summary>
        private void RefreshConversationView()
        {
            if (_messagesView == null)
            {
                _messagesView = (CollectionView)CollectionViewSource.GetDefaultView(_messages);
                _messagesView.Filter = o =>
                {
                    ChatMessageItem m = o as ChatMessageItem;
                    return m != null && m.ConvKey == _currentConvKey;
                };
            }
            _messagesView.Refresh();
            ChatScroll.ScrollToEnd();
            UpdateConvTitle();
        }

        private void UpdateConvTitle()
        {
            Conversation c = CurrentConversation;
            ChatTitle.Text = c.IsGroup ? "# 群聊" : ("私聊: " + c.Title);
        }

        /// <summary>
        /// 未读徽标：显示**所有会话**的未读总和（侧栏保持在线用户列表，没有逐会话徽标位置）。
        /// 切进某个会话时该会话的未读清零，总和随之减少。
        /// </summary>
        private void UpdateUnreadBadge()
        {
            int n = 0;
            foreach (var c in _conversations.Values) n += c.Unread;
            _unreadCount = n;                       // 托盘提示也用这个数
            TextUnread.Text = n + " 条新消息";
            BtnUnread.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

            // ★ 未读数变了就顺手同步跑马灯：这是所有未读变化的唯一必经点，
            //   放在这里就不用去 TrackUnread / SwitchConversation / ClearUnread 各处补调用
            //   （原来只在 2 秒的用户同步里对齐 ⇒ 未读清了跑马灯还要多滚 2 秒）。
            SyncMarquees();
        }

        /// <summary>入站消息的会话键：图片按 scope(G/P)，文字按来源 IP</summary>
        private static string IncomingConvKey(string scope, string ip)
        {
            return scope == "P" ? Conversation.PeerKey(ip) : Conversation.GroupKey;
        }

        /// <summary>把某个 IP 的未读数写到侧栏那一行（会话分离后这是"谁给我发了消息"的唯一可见提示）</summary>
        private void SetPeerUnread(string ip, int n)
        {
            if (string.IsNullOrEmpty(ip)) return;
            foreach (ChatUser u in _userList)
            {
                if (u.IP == ip) { u.Unread = n; break; }
            }
        }

        /// <summary>
        /// 清掉某个会话的未读数（会话对象 + 侧栏行 + 顶栏总数 + 跑马灯）。
        /// ★ 第三轮复核修复：侧栏那一行必须用**会话键反解出来的 IP**，不能用 c.PeerIP ——
        ///   首次收到某人私聊时会话是 TrackUnread 那一刻建的，当时传的 peerIP 是空串，
        ///   SetPeerUnread("") 会直接 return，于是**侧栏红点清不掉**（用户实测"查看后没有重新计数"就是这个）。
        /// </summary>
        private void ClearConversationUnread(string convKey)
        {
            if (string.IsNullOrEmpty(convKey)) return;
            Conversation c;
            if (!_conversations.TryGetValue(convKey, out c)) return;

            c.Unread = 0;
            if (!c.IsGroup) SetPeerUnread(Conversation.PeerIpOf(c.Key), 0);
            UpdateUnreadBadge();   // 内部会一并同步跑马灯
        }

        /// <summary>清掉某个会话在侧栏行上的未读标记（保留旧名，语义已并入 ClearConversationUnread）</summary>
        private void ClearPeerUnread(string convKey)
        {
            ClearConversationUnread(convKey);
        }

        // ==================== 内嵌消息弹窗（豆包需求 #5 / Q6）====================

        private System.Windows.Threading.DispatcherTimer _toastTimer;
        private string _toastConvKey;

        private ToastWindow _toastWindow;

        /// <summary>
        /// 后台/最小化/隐藏到托盘时的提醒走独立小窗（主窗内的卡片这时根本看不见）。
        /// 判断依据：主窗口当前是否"可见且在前台"。
        /// </summary>
        private void ShowToast(string convKey, string title, string preview)
        {
            // 豆包需求 #5：设置里可关闭弹窗。只拦"弹窗"这一层，未读红点/托盘计数照常更新
            //（TrackUnread 在调本方法之前就已经加过未读，所以这里直接返回不会丢未读）。
            if (_settings != null && !_settings.ToastEnabled) return;

            Conversation c = EnsureConversation(convKey, title, convKey == Conversation.GroupKey, "");
            _toastConvKey = convKey;
            string line = c.IsGroup ? ("群聊 · " + c.Unread + " 条新消息") : (title + " · " + c.Unread + " 条新消息");

            bool canSeeCard = IsVisible && IsActive && WindowState != WindowState.Minimized;
            if (!canSeeCard)
            {
                // 独立提醒窗（不抢焦点/不闪任务栏/无声音），同会话连续消息只更新内容
                if (_toastWindow == null) _toastWindow = new ToastWindow(this);
                _toastWindow.ShowMessage(line, preview ?? "", convKey);
                return;
            }

            TextToastTitle.Text = line;
            TextToastText.Text = preview ?? "";
            ToastCard.Visibility = Visibility.Visible;
            if (Models.ChatSettings.AnimationsOn)
            {
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                ToastCard.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                // 动效总开关关闭：清掉可能在跑的动画，直接落到终态
                ToastCard.BeginAnimation(OpacityProperty, null);
                ToastCard.Opacity = 1;
            }
            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
                _toastTimer.Tick += (s, e) => HideToast();
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /// <summary>把主窗口唤回前台并跳到指定会话（提醒窗点击时调用）</summary>
        public void RestoreAndOpenConversation(string convKey)
        {
            try
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (string.IsNullOrEmpty(convKey)) return;
                if (convKey == Conversation.GroupKey) SwitchConversation(convKey, "群聊", true, "");
                else
                {
                    string ip = convKey.StartsWith("peer:") ? convKey.Substring(5) : convKey;
                    ChatUser target = null;
                    foreach (ChatUser u in _userList) { if (u.IP == ip) { target = u; break; } }
                    if (target != null) { _currentTarget = target; UserList.SelectedItem = target; }
                    SwitchConversation(convKey, NicknameOfConvKey(convKey), false, ip);
                }
            }
            catch { }
        }

        private void HideToast()
        {
            if (_toastTimer != null) _toastTimer.Stop();
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (s, e) => { ToastCard.Visibility = Visibility.Collapsed; };
            ToastCard.BeginAnimation(OpacityProperty, fade);
        }

        private void Toast_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string key = _toastConvKey;
            HideToast();
            if (string.IsNullOrEmpty(key)) return;

            if (key == Conversation.GroupKey) SwitchConversation(key, "群聊", true, "");
            else
            {
                // 用反解 helper 而不是 key.Substring(5)：万一 key 不是 peer: 开头（理论上不该发生），
                // 直接 Substring 会抛异常把整段点击吞掉。
                string ip = Conversation.PeerIpOf(key);
                ChatUser target = null;
                foreach (ChatUser u in _userList) { if (u.IP == ip) { target = u; break; } }
                if (target != null)
                {
                    _currentTarget = target;
                    UserList.SelectedItem = target;
                    SwitchConversation(key, target.Nickname, false, ip);
                }
            }
        }

        /// <summary>在会话分离模式下取消息的显示时间（侧栏/排序用）</summary>
        private void TouchConversation(string key, string title, bool isGroup, string peerIP, string time)
        {
            Conversation c = EnsureConversation(key, title, isGroup, peerIP);
            c.LastTime = time;
        }
    }
}
