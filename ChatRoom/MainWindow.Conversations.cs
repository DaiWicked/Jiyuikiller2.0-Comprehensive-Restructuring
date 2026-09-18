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

        /// <summary>未读徽标：显示"当前会话"的未读数（会话分离后不再全局混算）</summary>
        private void UpdateUnreadBadge()
        {
            int n = CurrentConversation.Unread;
            TextUnread.Text = n + " 条新消息";
            BtnUnread.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>入站消息的会话键：图片按 scope(G/P)，文字按来源 IP</summary>
        private static string IncomingConvKey(string scope, string ip)
        {
            return scope == "P" ? Conversation.PeerKey(ip) : Conversation.GroupKey;
        }

        /// <summary>在会话分离模式下取消息的显示时间（侧栏/排序用）</summary>
        private void TouchConversation(string key, string title, bool isGroup, string peerIP, string time)
        {
            Conversation c = EnsureConversation(key, title, isGroup, peerIP);
            c.LastTime = time;
        }
    }
}