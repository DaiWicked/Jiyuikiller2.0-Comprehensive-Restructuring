using System.Collections.ObjectModel;

namespace ChatRoom.Models
{
    /// <summary>
    /// 一个会话（群聊或与某个人的私聊）。
    ///
    /// 设计说明：消息实体仍然只存一份（MainWindow._messages），每个消息用
    /// <see cref="ChatMessageItem.ConvKey"/> 标记归属；界面用 CollectionView 按会话过滤显示。
    /// 这样做的好处：收发/历史/搜索/防伪造这些已经验证过的逻辑都**不用推倒重来**，
    /// 只是"显示哪一份"，不会出现"私聊和群聊内容混在一起"。
    /// </summary>
    public class Conversation
    {
        /// <summary>会话键："group" 或 "peer:192.168.1.5"</summary>
        public string Key { get; set; } = GroupKey;

        /// <summary>侧栏显示的标题：群聊 / 对方昵称</summary>
        public string Title { get; set; } = "群聊";

        public bool IsGroup { get; set; } = true;

        /// <summary>私聊对象的 IP（群聊为空）</summary>
        public string PeerIP { get; set; } = "";

        /// <summary>该会话的未读数（与顶栏徽标联动）</summary>
        public int Unread { get; set; }

        /// <summary>该会话是否已插入过"以下为新消息"分隔线</summary>
        public bool SeparatorShown { get; set; }

        /// <summary>最后一条消息的时间（用于侧栏排序与显示）</summary>
        public string LastTime { get; set; } = "";

        public const string GroupKey = "group";

        public static string PeerKey(string ip)
        {
            return "peer:" + (ip ?? "");
        }

        /// <summary>
        /// 从会话键反解私聊对象的 IP（群聊返回空串）。
        /// 为什么需要它：刚收到一条陌生人的私聊时，会话是**那一刻才建**的，
        /// 建的时候只传了 key 没传 IP，PeerIP 是空的 —— 直接拿 PeerIP 去更新侧栏徽标会拿不到东西。
        /// </summary>
        public static string PeerIpOf(string key)
        {
            return (!string.IsNullOrEmpty(key) && key.StartsWith("peer:")) ? key.Substring(5) : "";
        }
    }
}
