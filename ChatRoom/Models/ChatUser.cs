using System;
using System.ComponentModel;

namespace ChatRoom.Models
{
    /// <summary>
    /// 侧栏的一个在线用户。
    ///
    /// ★ 第三轮复核修复：实现 INotifyPropertyChanged。
    ///   原来没有 INPC，`Unread`/`Nickname` 变了界面**不会自己更新** ——
    ///   要么等 2 秒的整表 Refresh（红点迟到 + 列表闪），要么永远不更新。
    ///   现在 Unread 一变，侧栏红点、未读数、跑马灯立刻跟着变，不用再刷新整张表。
    /// </summary>
    public class ChatUser : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null)
            {
                try { h(this, new PropertyChangedEventArgs(name)); } catch { }
            }
        }

        public string IP { get; set; } = "";

        private string _nickname = "";
        public string Nickname
        {
            get { return _nickname; }
            set
            {
                if (_nickname == value) return;
                _nickname = value;
                Raise("Nickname");
                Raise("Initial");        // 头像首字派生自昵称
                Raise("AvatarBrush");    // 头像底色按 IP 派生，昵称变了也要重新取（缓存失效）
                _avatarBrush = null;
            }
        }

        public DateTime LastSeen { get; set; } = DateTime.Now;
        public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 8;
        public bool IsMe { get; set; } = false;

        private int _unread;
        /// <summary>该对象发来的未读消息数（会话分离后必须逐行提示，否则在群聊里收到的私聊会被漏看）</summary>
        public int Unread
        {
            get { return _unread; }
            set
            {
                if (_unread == value) return;
                _unread = value;
                Raise("Unread");
                Raise("UnreadText");
                Raise("UnreadVisibility");
            }
        }

        public string UnreadText { get { return Unread > 0 ? (Unread > 99 ? "99+" : Unread.ToString()) : ""; } }

        public System.Windows.Visibility UnreadVisibility
        {
            get { return Unread > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed; }
        }

        /// <summary>头像上显示的首字（Telegram 式：没有头像就显示昵称首字）</summary>
        public string Initial
        {
            get { return string.IsNullOrEmpty(Nickname) ? "?" : Nickname.Substring(0, 1); }
        }

        private System.Windows.Media.Brush _avatarBrush;

        /// <summary>
        /// 头像底色：由 IP 稳定派生（同一台机器颜色固定，双方看到的也一致）。
        /// 只是 UI 便利属性，不参与协议。
        /// 缓存一次：原来每次取值都新建一个 SolidColorBrush，行被反复绑定时纯属白扔对象。
        /// </summary>
        public System.Windows.Media.Brush AvatarBrush
        {
            get
            {
                if (_avatarBrush != null) return _avatarBrush;
                string[] colors = { "#FFE17076", "#FF7BC862", "#FFE5CA77", "#FF65AADD", "#FFA695E7", "#FFEE7AAE" };
                int h = 0;
                string ip = IP ?? "";
                for (int i = 0; i < ip.Length; i++) h = (h * 31 + ip[i]) & 0x7FFFFFFF;
                _avatarBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[h % colors.Length]));
                return _avatarBrush;
            }
        }

        public override string ToString()
        {
            return $"{Nickname} ({IP})";
        }
    }
}
