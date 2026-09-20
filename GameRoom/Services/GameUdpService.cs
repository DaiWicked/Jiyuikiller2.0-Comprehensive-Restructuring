using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GameRoom
{
    /// <summary>
    /// 游戏UDP通信服务：广播发现 + 单播消息
    /// 消息格式: TYPE|NICK|DATA (简单文本，无需第三方库)
    /// 端口47061（ChatRoom是47060）
    /// </summary>
    public class GameUdpService
    {
        public const int Port = 47061;
        public string LocalIP { get; private set; } = "";
        public string Nick { get; set; } = "玩家";

        private UdpClient _udp;
        private Thread _recvThread;
        private Thread _heartbeatThread;
        private volatile bool _running;

        private readonly ConcurrentDictionary<string, GameUser> _users = new ConcurrentDictionary<string, GameUser>();
        private const int HeartbeatInterval = 3000; // 3秒

        public event Action<string> OnLog;
        public event Action<List<GameUser>> OnUserListChanged;
        public event Action<GameUser, string> OnGameMessage;

        public void Start(string nick)
        {
            Nick = nick;
            LocalIP = GetLocalIP();

            try
            {
                Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.ExclusiveAddressUse = true;
                sock.Bind(new IPEndPoint(IPAddress.Any, Port));
                _udp = new UdpClient();
                _udp.Client = sock;
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("端口 " + Port + " 已被占用", ex);
            }

            _running = true;
            _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "GameRecv" };
            _recvThread.Start();

            _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "GameHB" };
            _heartbeatThread.Start();

            Broadcast("HELLO|" + Nick + "|");
            Log("GameRoom已启动，端口 " + Port);
        }

        public void Stop()
        {
            _running = false;
            try { Broadcast("BYE|" + Nick + "|"); } catch { }
            try { _udp?.Close(); } catch { }
        }

        public List<GameUser> SnapshotUsers()
        {
            var now = DateTime.Now;
            foreach (var k in _users.Keys.Where(k => (now - _users[k].LastSeen).TotalSeconds > 6).ToList())
                _users.TryRemove(k, out _);
            return _users.Values.ToList();
        }

        public void Broadcast(string msg)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(msg);
                _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, Port));
            }
            catch (Exception ex) { Log("广播失败: " + ex.Message); }
        }

        public void SendTo(IPEndPoint ep, string msg)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(msg);
                _udp.Send(data, data.Length, ep);
            }
            catch (Exception ex) { Log("单播失败: " + ex.Message); }
        }

        private void RecvLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, Port);
            while (_running)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remote);
                    string msg = Encoding.UTF8.GetString(data);
                    string[] parts = msg.Split('|');
                    if (parts.Length < 2) continue;
                    string type = parts[0];
                    string nick = parts[1];
                    string data2 = parts.Length > 2 ? parts[2] : "";

                    switch (type)
                    {
                        case "HELLO":
                            AddUser(remote, nick);
                            // 回复心跳
                            SendTo(remote, "HB|" + Nick + "|");
                            break;
                        case "HB":
                            AddUser(remote, nick);
                            break;
                        case "BYE":
                            RemoveUser(remote, nick);
                            break;
                        case "GAME":
                            var user = _users.Values.FirstOrDefault(u => u.Endpoint.Address.Equals(remote.Address));
                            OnGameMessage?.Invoke(user, data2);
                            break;
                    }
                }
                catch { }
            }
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                try
                {
                    Broadcast("HB|" + Nick + "|");
                    OnUserListChanged?.Invoke(SnapshotUsers());
                }
                catch { }
                Thread.Sleep(HeartbeatInterval);
            }
        }

        private void AddUser(IPEndPoint ep, string nick)
        {
            string key = ep.Address.ToString();
            bool isNew = !_users.ContainsKey(key);
            _users[key] = new GameUser { Nick = nick, Endpoint = ep, LastSeen = DateTime.Now };
            if (isNew) Log(nick + " 加入了房间");
        }

        private void RemoveUser(IPEndPoint ep, string nick)
        {
            string key = ep.Address.ToString();
            GameUser u;
            if (_users.TryRemove(key, out u))
                Log(u.Nick + " 离开了");
        }

        private void Log(string s) { OnLog?.Invoke(s); }

        public static string GetLocalIP()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
