using System;
using System.Net;

namespace GameRoom
{
    /// <summary>在线玩家</summary>
    public class GameUser
    {
        public string Nick { get; set; } = "";
        public IPEndPoint Endpoint { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public string Status { get; set; } = "空闲";  // 空闲/游戏中/观战
    }
}
