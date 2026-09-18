using System;

namespace ChatRoom.Models
{
    public class ChatUser
    {
        public string IP { get; set; } = "";
        public string Nickname { get; set; } = "";
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 8;
        public bool IsMe { get; set; } = false;

        /// <summary>该对象发来的未读消息数（会话分离后必须逐行提示，否则在群聊里收到的私聊会被漏看）</summary>
        public int Unread { get; set; }

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

        /// <summary>
        /// 头像底色：由 IP 稳定派生（同一台机器颜色固定，双方看到的也一致）。
        /// 只是 UI 便利属性，不参与协议。
        /// </summary>
        public System.Windows.Media.Brush AvatarBrush
        {
            get
            {
                string[] colors = { "#FFE17076", "#FF7BC862", "#FFE5CA77", "#FF65AADD", "#FFA695E7", "#FFEE7AAE" };
                int h = 0;
                string ip = IP ?? "";
                for (int i = 0; i < ip.Length; i++) h = (h * 31 + ip[i]) & 0x7FFFFFFF;
                return new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[h % colors.Length]));
            }
        }

        public override string ToString()
        {
            return $"{Nickname} ({IP})";
        }
    }
}
