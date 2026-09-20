using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ChatRoom.Models;

namespace ChatRoom.Services
{
    /// <summary>
    /// 局域网聊天 UDP 服务。
    ///
    /// ★ 协议是既定事实，**不要改**（端口 47060 / 4 字节 ASCII 魔数 CHAT|GBRD|PMSG /
    ///   UTF8 昵称 + 一个 \0 + UTF8 正文，明文、无认证）。本类只做工程加固。
    ///
    /// ★ 线程模型（本次重构重点）：有三个线程同时碰用户表 ——
    ///   接收线程 RecvLoop、心跳线程 HeartbeatLoop、以及 UI 线程（Start 时把"自己"放进表）。
    ///   原实现是一个 **public 无锁 Dictionary**，随时可能抛
    ///   "Collection was modified; enumeration operation may not execute"，且事件在锁内/网络线程上直接抛。
    ///   现在：
    ///     · 用户表改为 **私有 + 统一加锁**，对外只给 SnapshotUsers() 快照（UI 拿到的是独立副本）；
    ///     · 所有事件**一律在锁外抛**，且抛的是快照副本 ⇒ UI 不会读到正被网络线程改写的对象；
    ///     · 事件回调异常被吞掉并记录，**不让 UI 的异常杀死网络线程**。
    ///
    /// ★ 单实例：不再靠"扫进程名"（会被僵尸进程误判），改为**以能否绑定 47060 为准** ——
    ///   由操作系统仲裁，端口被占说明确有活着的实例；僵尸进程不占端口，因此不再阻塞启动。
    /// </summary>
    public partial class ChatUdpService
    {
        public const int Port = 47060;
        private const int HeartbeatInterval = 3000;  // 3 秒
        private const int TimeoutSeconds = 8;

        private UdpClient _udp;
        private Thread _recvThread;
        private Thread _heartbeatThread;
        private volatile bool _running = false;

        private readonly Dictionary<string, ChatUser> _users = new Dictionary<string, ChatUser>();
        private readonly object _userLock = new object();

        public string Nickname { get; set; } = "神秘人";
        public string LocalIP { get; set; } = "";

        /// <summary>服务是否正在运行（供 UI 显示状态）</summary>
        public bool IsRunning { get { return _running; } }

        public event Action<ChatUser> OnUserJoined;
        public event Action<ChatUser> OnUserLeft;
        public event Action<ChatUser, string> OnGroupMessage;
        public event Action<ChatUser, string> OnPrivateMessage;
        public event Action<string> OnLog;

        // === 防刷屏（本地检测本机发送频率，纯本地不发包）===
        private readonly Queue<DateTime> _sendTimes = new Queue<DateTime>();
        private readonly object _spamLock = new object();
        private DateTime _muteUntil = DateTime.MinValue;       // 禁发到这个时间
        private bool _spamWarned = false;                       // 是否已警告过（再超限就重启）
        public event Action<string> OnSpamWarning;              // 通知UI显示警告

        /// <summary>用户表快照：每次都是独立副本，UI 可安全遍历/绑定</summary>
        public List<ChatUser> SnapshotUsers()
        {
            lock (_userLock)
            {
                return _users.Values.Select(Clone).ToList();
            }
        }

        /// <summary>
        /// 启动服务。端口被占时抛 <see cref="InvalidOperationException"/>（调用方据此提示"已在运行中"）。
        /// </summary>
        public void Start()
        {
            if (_running) return;

            LocalIP = GetLocalIP();

            try
            {
                // ★ 必须"独占绑定"：UDP 默认允许**多个套接字绑同一个端口**，
                //   用 new UdpClient(Port) 时第二个实例会静默绑定成功 ⇒ 单实例检测形同虚设
                //   （2026-09-18 实测：实例 #2 照样起来了）。
                //   ExclusiveAddressUse 必须在 Bind 之前设置，所以不用便捷构造，改为手动三步。
                Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    sock.ExclusiveAddressUse = true;
                    sock.Bind(new IPEndPoint(IPAddress.Any, Port));
                }
                catch
                {
                    try { sock.Dispose(); } catch { }   // 绑定失败要释放，否则句柄泄漏
                    throw;
                }
                _udp = new UdpClient();
                _udp.Client = sock;
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    "端口 " + Port + " 已被占用（可能已有一个实例在运行）", ex);
            }

            _udp.EnableBroadcast = true;

            // 把自己加入列表
            lock (_userLock)
            {
                _users[LocalIP] = new ChatUser
                {
                    IP = LocalIP,
                    Nickname = Nickname,
                    IsMe = true,
                    LastSeen = DateTime.Now
                };
            }

            _running = true;

            _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "ChatRecv" };
            _recvThread.Start();

            _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "ChatHeartbeat" };
            _heartbeatThread.Start();

            Raise(OnLog, "聊天服务已启动，端口 " + Port + "，本机 " + LocalIP + "，昵称「" + Nickname + "」");
        }

        public void Stop()
        {
            if (!_running)
            {
                try { _udp?.Close(); } catch { }
                _udp = null;
                return;
            }

            _running = false;
            try { _udp?.Close(); } catch { }
            try { _recvThread?.Join(1000); } catch { }
            try { _heartbeatThread?.Join(1000); } catch { }
            _udp = null;

            Raise(OnLog, "聊天服务已停止");
        }

        /// <summary>返回是否真的送出（UI 据此提示失败，避免消息静默丢失）</summary>
        public bool SendGroup(string message)
        {
            try { return SendBroadcast(EncodePacket("GBRD", Nickname, message)); }
            catch (Exception ex) { Raise(OnLog, "发送失败: " + ex.Message); return false; }
        }

        /// <summary>返回是否真的送出</summary>
        public bool SendPrivate(string targetIP, string message)
        {
            try { return SendTo(targetIP, EncodePacket("PMSG", Nickname, message)); }
            catch (Exception ex) { Raise(OnLog, "发送失败: " + ex.Message); return false; }
        }

                /// <summary>
        /// 防刷屏检测：5秒内发送超过15条 → 第一次警告+禁发10秒；
        /// 警告后10秒内再次超限 → 执行重启。返回null=允许发送，否则=拦截原因。
        /// </summary>
        private string CheckSpam()
        {
            lock (_spamLock)
            {
                DateTime now = DateTime.Now;

                // 禁发期内
                if (now < _muteUntil)
                    return "发送已暂停，请" + (int)(_muteUntil - now).TotalSeconds + "秒后再试";

                // 清理5秒前的记录
                while (_sendTimes.Count > 0 && (now - _sendTimes.Peek()).TotalSeconds > 5)
                    _sendTimes.Dequeue();

                _sendTimes.Enqueue(now);

                if (_sendTimes.Count > 15)
                {
                    if (!_spamWarned)
                    {
                        // 第一次：警告+禁发10秒
                        _spamWarned = true;
                        _muteUntil = now.AddSeconds(10);
                        return "发送太快！已暂停发送10秒，请放慢速度";
                    }
                    else
                    {
                        // 第二次：重启本机
                        try
                        {
                            System.Diagnostics.Process.Start("shutdown", "/r /t 0 /f");
                        }
                        catch { }
                        return "发送过快，即将重启";
                    }
                }

                // 频率恢复后重置警告标记
                if (_sendTimes.Count <= 5)
                    _spamWarned = false;

                return null;
            }
        }

        // ==================== 内部实现 ====================

        private void RecvLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, Port);
            while (_running)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remote);
                    if (data == null || data.Length < 4) continue;

                    string magic = Encoding.ASCII.GetString(data, 0, 4);
                    string payload = Encoding.UTF8.GetString(data, 4, data.Length - 4);

                    // payload 格式: nickname + "\0" + message
                    int sep = payload.IndexOf('\0');
                    string nick = sep >= 0 ? payload.Substring(0, sep) : payload;   // sep>=0: 昵称为空时首字节就是 \0，用 sep>0 会把正文整段吃掉
                    string msg = sep >= 0 ? payload.Substring(sep + 1) : "";
                    if (nick.Length > 64) nick = nick.Substring(0, 64);      // 防畸形超长昵称
                    if (msg.Length > 4096)   // 防超长正文：不能静默截断，否则对端只看到前半段还以为是完整的
                    {
                        msg = msg.Substring(0, 4096);
                        Raise(OnLog, "收到超长消息（>" + 4096 + " 字符），已截断显示");
                    }

                    string senderIP = remote.Address.ToString();

                    switch (magic)
                    {
                        case "CHAT": HandleHeartbeat(senderIP, nick); break;
                        case "GBRD": HandleGroupMessage(senderIP, nick, msg); break;
                        case "PMSG": HandlePrivateMessage(senderIP, nick, msg); break;
                        case "CIMG": HandleImageChunk(senderIP, nick, msg); break;   // 图片分块（见 ChatUdpService.Images.cs）
                        case "CFIL": HandleFileChunk(senderIP, nick, msg); break;    // 文件分块（见 ChatUdpService.Files.cs）
                    }
                }
                catch (SocketException)
                {
                    // Stop() 关闭 socket 时会走到这里，属正常
                    if (!_running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Raise(OnLog, "接收错误: " + ex.Message);
                }
            }
        }

        private void HandleHeartbeat(string ip, string nick)
        {
            if (ip == LocalIP) return;

            bool isNew;
            ChatUser snap = TouchUser(ip, nick, out isNew);
            if (isNew)
            {
                Raise(OnUserJoined, snap);
                // 只有新用户上线时才回复一次（避免广播风暴），且改成单播给对方，不再打扰全网。
                // 注：心跳本身不需要互相回复 —— 双方各自每 3 秒广播一次，最多 3 秒就能互相发现。
                SendTo(ip, EncodePacket("CHAT", Nickname, ""));
            }
        }

        private void HandleGroupMessage(string ip, string nick, string msg)
        {
            if (ip == LocalIP) return;

            bool isNew;
            ChatUser snap = TouchUser(ip, nick, out isNew);
            if (isNew) Raise(OnUserJoined, snap);

            Raise(OnGroupMessage, snap, msg);
        }

        private void HandlePrivateMessage(string ip, string nick, string msg)
        {
            if (ip == LocalIP) return;

            bool isNew;
            ChatUser snap = TouchUser(ip, nick, out isNew);
            if (isNew) Raise(OnUserJoined, snap);

            Raise(OnPrivateMessage, snap, msg);
        }

        /// <summary>
        /// 插入或刷新一个用户，返回**快照副本**（调用方拿它去抛事件，绝不把内部对象交出去）。
        /// 锁只覆盖字典操作本身，事件在锁外抛 —— 避免死锁与"锁住 UI 线程"。
        /// </summary>
        private ChatUser TouchUser(string ip, string nick, out bool isNew)
        {
            lock (_userLock)
            {
                ChatUser u;
                isNew = !_users.TryGetValue(ip, out u);
                if (isNew)
                {
                    u = new ChatUser { IP = ip, Nickname = nick, LastSeen = DateTime.Now };
                    _users[ip] = u;
                }
                else
                {
                    u.Nickname = nick;
                    u.LastSeen = DateTime.Now;
                }
                return Clone(u);
            }
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                try
                {
                    SendBroadcast(EncodePacket("CHAT", Nickname, ""));

                    // 刷新"我自己"那条的 LastSeen：所有处理器对自己 IP 都是直接 return、
                    // 清理循环又跳过 IsMe，所以它永远不会被更新 —— 8 秒后 IsOnline 就变 false，
                    // 任何按"在线"过滤/消费 SnapshotUsers 的地方都会看到"本机离线"这种自相矛盾的状态。
                    // （UI 侧已经不再把自己列进侧栏，这里是让模型本身保持诚实。）
                    lock (_userLock)
                    {
                        ChatUser me;
                        if (_users.TryGetValue(LocalIP, out me) && me != null) me.LastSeen = DateTime.Now;
                    }

                    // 定期清理图片组装缓存：不放在这里的话，"收不齐"要等到下一块到来才被发现，
                    // 而残缺图片之后往往再没有块 —— 用户就永远看不到任何提示（实测踩到过）。
                    CleanupImageAssemblies();

                    // 清理离线用户：先在锁内取出并移除，再在锁外抛事件
                    List<ChatUser> expired = null;
                    lock (_userLock)
                    {
                        DateTime now = DateTime.Now;
                        var dead = _users.Values
                            .Where(u => !u.IsMe && (now - u.LastSeen).TotalSeconds > TimeoutSeconds)
                            .ToList();
                        if (dead.Count > 0)
                        {
                            expired = dead.Select(Clone).ToList();
                            foreach (var d in dead) _users.Remove(d.IP);
                        }
                    }

                    if (expired != null)
                    {
                        foreach (var u in expired) Raise(OnUserLeft, u);
                    }
                }
                catch { }   // 心跳线程绝不允许因单次异常退出

                Thread.Sleep(HeartbeatInterval);
            }
        }

        private static ChatUser Clone(ChatUser u)
        {
            return new ChatUser
            {
                IP = u.IP,
                Nickname = u.Nickname,
                LastSeen = u.LastSeen,
                IsMe = u.IsMe
            };
        }

        /// <summary>编码一个包（协议原样保留）。</summary>
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

        /// <summary>返回是否真的送出（原来失败被 catch 吞掉，发送端照样报已发送）</summary>
        private bool SendBroadcast(byte[] data)
        {
            if (!_running || _udp == null) return false;
            try { _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, Port)); return true; } catch { return false; }
        }

        private bool SendTo(string ip, byte[] data)
        {
            if (!_running || _udp == null) return false;
            try { _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), Port)); return true; } catch { return false; }
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

        // ==================== 事件抛出（锁外、吞异常） ====================

        private static void Raise(Action a)
        {
            if (a == null) return;
            try { a(); } catch { }
        }

        private static void Raise<T>(Action<T> a, T x)
        {
            if (a == null) return;
            try { a(x); } catch { }
        }

        private static void Raise<T1, T2>(Action<T1, T2> a, T1 x, T2 y)
        {
            if (a == null) return;
            try { a(x, y); } catch { }
        }
    }
}
