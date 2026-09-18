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
            else if (!string.IsNullOrEmpty(title)) c.Title = title;
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
            CurrentConversation.Unread = 0;
            ClearPeerUnread(key);   // 侧栏行徽标同步清零
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

        /// <summary>清掉某个会话在侧栏行上的未读标记</summary>
        private void ClearPeerUnread(string convKey)
        {
            foreach (var kv in _conversations)
            {
                if (kv.Key == convKey)
                {
                    kv.Value.Unread = 0;
                    if (!kv.Value.IsGroup) SetPeerUnread(kv.Value.PeerIP, 0);
                }
            }
        }

        // ==================== 内嵌消息弹窗（豆包需求 #5 / Q6）====================

        private System.Windows.Threading.DispatcherTimer _toastTimer;
        private string _toastConvKey;

        /// <summary>
        /// 右下角内嵌小卡片：同会话连续消息合并成"N 条新消息"；3.5 秒自动收起；
        /// 点击跳到该会话。不抢焦点、不闪任务栏、无声音（机房环境）。
        /// </summary>
        private void ShowToast(string convKey, string title, string preview)
        {
            Conversation c = EnsureConversation(convKey, title, convKey == Conversation.GroupKey, "");
            _toastConvKey = convKey;

            TextToastTitle.Text = c.IsGroup ? ("群聊 · " + c.Unread + " 条新消息") : (title + " · " + c.Unread + " 条新消息");
            TextToastText.Text = preview ?? "";

            ToastCard.Visibility = Visibility.Visible;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ToastCard.BeginAnimation(OpacityProperty, fade);

            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
                _toastTimer.Tick += (s, e) => HideToast();
            }
            _toastTimer.Stop();
            _toastTimer.Start();
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
                string ip = key.Substring("peer:".Length);
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