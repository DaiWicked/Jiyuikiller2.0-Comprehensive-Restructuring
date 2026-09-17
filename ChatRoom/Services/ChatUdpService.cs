using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using ChatRoom.Models;

namespace ChatRoom.Services
{
    public class ChatUdpService
    {
        public const int Port = 47060;
        private const int HeartbeatInterval = 3000; // 3秒
        private const int TimeoutSeconds = 8;

        private UdpClient _udp;
        private Thread _recvThread;
        private Thread _heartbeatThread;
        private volatile bool _running = false;

        public Dictionary<string, ChatUser> Users = new Dictionary<string, ChatUser>();
        public string Nickname { get; set; } = "神秘人";
        public string LocalIP { get; set; } = "";

        public event Action<ChatUser> OnUserJoined;
        public event Action<ChatUser> OnUserLeft;
        public event Action<ChatUser, string> OnGroupMessage;
        public event Action<ChatUser, string> OnPrivateMessage;
        public event Action<string> OnLog;

        public void Start()
        {
            try
            {
                LocalIP = GetLocalIP();
                _udp = new UdpClient(Port);
                _udp.EnableBroadcast = true;

                // 把自己加入列表
                Users[LocalIP] = new ChatUser { IP = LocalIP, Nickname = Nickname, IsMe = true, LastSeen = DateTime.Now };

                _running = true;
                _recvThread = new Thread(RecvLoop) { IsBackground = true };
                _recvThread.Start();

                _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true };
                _heartbeatThread.Start();

                OnLog?.Invoke($"聊天服务已启动，端口{Port}，本机{LocalIP}，昵称「{Nickname}」");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"启动失败: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _udp?.Close(); } catch { }
            try { _recvThread?.Join(1000); } catch { }
            try { _heartbeatThread?.Join(1000); } catch { }
            OnLog?.Invoke("聊天服务已停止");
        }

        public void SendGroup(string message)
        {
            byte[] data = EncodePacket("GBRD", Nickname, message);
            SendBroadcast(data);
        }

        public void SendPrivate(string targetIP, string message)
        {
            byte[] data = EncodePacket("PMSG", Nickname, message);
            SendTo(targetIP, data);
        }

        // === 内部方法 ===

        private void RecvLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, Port);
            while (_running)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remote);
                    if (data.Length < 4) continue;

                    string magic = Encoding.ASCII.GetString(data, 0, 4);
                    string payload = Encoding.UTF8.GetString(data, 4, data.Length - 4);

                    // payload格式: nickname + "\0" + message
                    int sep = payload.IndexOf('\0');
                    string nick = sep > 0 ? payload.Substring(0, sep) : payload;
                    string msg = sep > 0 ? payload.Substring(sep + 1) : "";

                    string senderIP = remote.Address.ToString();

                    switch (magic)
                    {
                        case "CHAT":
                            HandleHeartbeat(senderIP, nick);
                            break;
                        case "GBRD":
                            HandleGroupMessage(senderIP, nick, msg);
                            break;
                        case "PMSG":
                            HandlePrivateMessage(senderIP, nick, msg);
                            break;
                    }
                }
                catch (SocketException) { /* 正常关闭 */ }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"接收错误: {ex.Message}");
                }
            }
        }

        private void HandleHeartbeat(string ip, string nick)
        {
            if (ip == LocalIP) return;

            bool isNew = !Users.ContainsKey(ip);
            if (!Users.ContainsKey(ip))
            {
                Users[ip] = new ChatUser { IP = ip, Nickname = nick, LastSeen = DateTime.Now };
                OnUserJoined?.Invoke(Users[ip]);
            }
            else
            {
                Users[ip].Nickname = nick;
                Users[ip].LastSeen = DateTime.Now;
            }

            // 回复心跳让对方发现我
            SendBroadcast(EncodePacket("CHAT", Nickname, ""));
        }

        private void HandleGroupMessage(string ip, string nick, string msg)
        {
            if (ip == LocalIP) return;
            if (!Users.ContainsKey(ip))
            {
                Users[ip] = new ChatUser { IP = ip, Nickname = nick, LastSeen = DateTime.Now };
                OnUserJoined?.Invoke(Users[ip]);
            }
            else
            {
                Users[ip].Nickname = nick;
                Users[ip].LastSeen = DateTime.Now;
            }
            OnGroupMessage?.Invoke(Users[ip], msg);
        }

        private void HandlePrivateMessage(string ip, string nick, string msg)
        {
            if (ip == LocalIP) return;
            if (!Users.ContainsKey(ip))
            {
                Users[ip] = new ChatUser { IP = ip, Nickname = nick, LastSeen = DateTime.Now };
                OnUserJoined?.Invoke(Users[ip]);
            }
            else
            {
                Users[ip].Nickname = nick;
                Users[ip].LastSeen = DateTime.Now;
            }
            OnPrivateMessage?.Invoke(Users[ip], msg);
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                try
                {
                    SendBroadcast(EncodePacket("CHAT", Nickname, ""));

                    // 清理离线用户
                    var expired = Users.Values.Where(u => !u.IsMe && (DateTime.Now - u.LastSeen).TotalSeconds > TimeoutSeconds).ToList();
                    foreach (var u in expired)
                    {
                        Users.Remove(u.IP);
                        OnUserLeft?.Invoke(u);
                    }
                }
                catch { }
                Thread.Sleep(HeartbeatInterval);
            }
        }

        private byte[] EncodePacket(string magic, string nick, string msg)
        {
            byte[] magicBytes = Encoding.ASCII.GetBytes(magic);
            byte[] nickBytes = Encoding.UTF8.GetBytes(nick);
            byte[] nullByte = new byte[1] { 0 };
            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            byte[] result = new byte[magicBytes.Length + nickBytes.Length + nullByte.Length + msgBytes.Length];
            Buffer.BlockCopy(magicBytes, 0, result, 0, magicBytes.Length);
            Buffer.BlockCopy(nickBytes, 0, result, magicBytes.Length, nickBytes.Length);
            Buffer.BlockCopy(nullByte, 0, result, magicBytes.Length + nickBytes.Length, 1);
            Buffer.BlockCopy(msgBytes, 0, result, magicBytes.Length + nickBytes.Length + 1, msgBytes.Length);
            return result;
        }

        private void SendBroadcast(byte[] data)
        {
            try { _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, Port)); } catch { }
        }

        private void SendTo(string ip, byte[] data)
        {
            try { _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), Port)); } catch { }
        }

        private string GetLocalIP()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch { return "127.0.0.1"; }
        }
    }
}
