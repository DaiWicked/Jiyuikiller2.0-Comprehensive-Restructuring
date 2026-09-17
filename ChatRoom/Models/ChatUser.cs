using System;
using System.Net;

namespace ChatRoom.Models
{
    public class ChatUser
    {
        public string IP { get; set; } = "";
        public string Nickname { get; set; } = "";
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 8;
        public bool IsMe { get; set; } = false;

        public override string ToString()
        {
            return $"{Nickname} ({IP})";
        }
    }
}
