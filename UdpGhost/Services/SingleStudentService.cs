using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UdpGhost.Services
{
    /// <summary>
    /// 单学生连接模式：测试验证学生端是否接受单播心跳
    /// </summary>
    public class SingleStudentService : IDisposable
    {
        private const int MAIN_PORT = 4705;
        private const int SESSION_PORT_BASE = 5000;
        private const string TEACHER_GUID = "196d6af9295bb946ab958a143ecddc26"; // C# Guid.ToByteArray()字节序
        private const string LPNT_GUID = "be8a3aaa902b4566908ea29526218540"; // C# Guid.ToByteArray()字节序
        private const string DMOC_GUID = "38fd90cef53d4c84857fa35183c051f3"; // C# Guid.ToByteArray()字节序

        private UdpClient _mainSock;
        private UdpClient _sessionSock;
        private Thread _heartbeatThread;
        private Thread _sessionAnnoThread;
        private Thread _recvThread;
        private Thread _sessionRecvThread;
        private volatile bool _running;
        private string _targetIP;
        private string _localIP;
        private int _channel;
        private string _teacherName;
        private int _ooncSeq;
        private bool _connected;

        public event Action<string> OnLog;
        public event Action<string> OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected => _connected;
        public string TargetIP => _targetIP;

        public SingleStudentService(string localIP, int channel = 1)
        {
            _localIP = localIP;
            _channel = channel;
        }

        public void Start(string targetIP, int channel = 1, string teacherName = "Teacher")
        {
            _targetIP = targetIP;
            _channel = channel;
            _teacherName = string.IsNullOrEmpty(teacherName) ? "Teacher" : teacherName;
            _running = true;
            _connected = false;
            _ooncSeq = 0;  // teacher_sim初始seq=0

            int sessionPort = SESSION_PORT_BASE + _channel * 512;

            _mainSock = new UdpClient();
            _mainSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _mainSock.Client.Bind(new IPEndPoint(IPAddress.Any, MAIN_PORT));
            _mainSock.Client.ReceiveTimeout = 1000;
            _mainSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _mainSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
            // 指定组播出站接口（teacher_sim也设置了这个）
            _mainSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(_localIP).GetAddressBytes());

            try
            {
                byte[] membership = new byte[8];
                Array.Copy(IPAddress.Parse("224.50.50.42").GetAddressBytes(), 0, membership, 0, 4);
                Array.Copy(IPAddress.Parse(_localIP).GetAddressBytes(), 0, membership, 4, 4);
                _mainSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, membership);
                Log("[初始化] 已加入组播组 224.50.50.42");
            }
            catch (Exception ex)
            {
                Log($"[初始化] 加入组播组失败: {ex.Message}");
            }

            _sessionSock = new UdpClient();
            _sessionSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _sessionSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _sessionSock.Client.Bind(new IPEndPoint(IPAddress.Any, sessionPort));
            _sessionSock.Client.ReceiveTimeout = 1000;
            _sessionSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);

            // 会话组播地址: 225.2.{channel+1}.1
            string sessionMcast = $"225.2.{_channel + 1}.1";
            try
            {
                byte[] smembership = new byte[8];
                Array.Copy(IPAddress.Parse(sessionMcast).GetAddressBytes(), 0, smembership, 0, 4);
                Array.Copy(IPAddress.Parse(_localIP).GetAddressBytes(), 0, smembership, 4, 4);
                _sessionSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, smembership);
                _sessionSock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(_localIP).GetAddressBytes());
                Log($"[初始化] 会话socket已加入组播 {sessionMcast}:{sessionPort}");
            }
            catch (Exception ex)
            {
                Log($"[初始化] 会话组播加入失败: {ex.Message}");
            }

            Log($"[初始化] 本机IP={_localIP}, 频道={_channel}, 会话端口={sessionPort}");
            Log($"[初始化] 目标学生={_targetIP}");

            _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true };
            _heartbeatThread.Start();

            _sessionAnnoThread = new Thread(SessionAnnoLoop) { IsBackground = true };
            _sessionAnnoThread.Start();

            _recvThread = new Thread(RecvLoop) { IsBackground = true };
            _recvThread.Start();

            _sessionRecvThread = new Thread(SessionRecvLoop) { IsBackground = true };
            _sessionRecvThread.Start();
        }

        private void HeartbeatLoop()
        {
            int tick = 0;
            // 单学生连接模式：只发单播到目标IP，不发组播/广播，避免影响其他学生
            string[] targets = { _targetIP };

            while (_running)
            {
                try
                {
                    // 阶段1: 只发OONC（teacher_sim先发OONC，等0.5秒）
                    byte[] oonc = BuildOONC();
                    foreach (var tgt in targets)
                    {
                        try { _mainSock.Send(oonc, oonc.Length, tgt, MAIN_PORT); }
                        catch { }
                    }
                    tick++;
                    if (tick == 1)
                    {
                        Log($"[心跳] OONC hex: {BitConverter.ToString(oonc).Replace("-"," ")}");
                    }
                    if (tick % 4 == 0 && !_connected)
                    {
                        Log($"[心跳] OONC 第{tick}次 seq={_ooncSeq}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[心跳] OONC失败: {ex.Message}");
                }

                // 等待0.5秒（teacher_sim的间隔）
                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(50);

                if (!_running) break;

                try
                {
                    // 阶段2: 发NANC + CANC
                    byte[] nanc = BuildNANC();
                    byte[] canc = BuildCANC();
                    foreach (var tgt in targets)
                    {
                        try { _mainSock.Send(nanc, nanc.Length, tgt, MAIN_PORT); }
                        catch { }
                        try { _mainSock.Send(canc, canc.Length, tgt, MAIN_PORT); }
                        catch { }
                    }
                    if (tick == 1)
                    {
                        Log($"[心跳] NANC hex: {BitConverter.ToString(nanc).Replace("-"," ")}");
                        Log($"[心跳] CANC hex: {BitConverter.ToString(canc).Replace("-"," ")}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[心跳] NANC/CANC失败: {ex.Message}");
                }

                // 等待0.5秒
                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(50);
            }
        }

        /// <summary>
        /// 会话端口ANNO广播（teacher_sim的session_anno）
        /// </summary>
        private void SessionAnnoLoop()
        {
            int sessionPort = SESSION_PORT_BASE + _channel * 512;
            string sessionMcast = $"225.2.{_channel + 1}.1";
            // 单学生连接模式：会话ANNO只发单播
            string[] targets = { _targetIP };

            Log("[会话ANNO] 启动");

            while (_running)
            {
                try
                {
                    // type1: ANNO + version
                    byte[] pkt1 = new byte[8];
                    BitConverter.GetBytes((uint)0x4F4E4E41).CopyTo(pkt1, 0);
                    BitConverter.GetBytes((uint)1).CopyTo(pkt1, 4);
                    foreach (var tgt in targets)
                    {
                        try { _sessionSock.Send(pkt1, pkt1.Length, tgt, sessionPort); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[会话ANNO] type1失败: {ex.Message}");
                }

                for (int i = 0; i < 6 && _running; i++) Thread.Sleep(50);
                if (!_running) break;

                try
                {
                    // type2: 复杂ANNO包
                    byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms))
                    {
                        bw.Write((uint)0x4F4E4E41); // ANNO
                        bw.Write((uint)1);
                        bw.Write((uint)1);
                        bw.Write((long)0); // 8 bytes zero
                        bw.Write(ip);
                        bw.Write((uint)0x0D5AD030);
                        bw.Write((uint)0);
                        bw.Write((uint)0x0D5AD030);
                        bw.Write((uint)1);
                        bw.Write(new byte[32]); // 32 bytes zero
                        byte[] pkt2 = ms.ToArray();
                        foreach (var tgt in targets)
                        {
                            try { _sessionSock.Send(pkt2, pkt2.Length, tgt, sessionPort); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[会话ANNO] type2失败: {ex.Message}");
                }

                for (int i = 0; i < 14 && _running; i++) Thread.Sleep(50);
            }
            Log("[会话ANNO] 停止");
        }

        private void RecvLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _mainSock.Receive(ref remote);

                    if (data.Length < 8) continue;

                    uint magic = BitConverter.ToUInt32(data, 0);
                    string magicName = GetMagicName(magic);
                    string sip = remote.Address.ToString();

                    // 不记录自己发送的包（源端口4705且源IP是本机）
                    if (sip == _localIP && remote.Port == MAIN_PORT) continue;

                    Log($"[接收] {magicName} from {sip}:{remote.Port}, len={data.Length}");

                    // KACA: 学生端发现回应，回复WACA
                    if (magic == 0x4143414B)
                    {
                        Log($"[握手] 收到 {sip} 的KACA，回复WACA");
                        byte[] waca = BuildWACA();
                        _mainSock.Send(waca, waca.Length, sip, MAIN_PORT);
                        // 自动更新目标IP为实际响应的学生端
                        if (sip != _targetIP)
                        {
                            Log($"[握手] 自动更新目标IP: {_targetIP} -> {sip}");
                            _targetIP = sip;
                        }
                    }
                    // TRMC: 学生端准备好，回复LPNT+DMOC
                    else if (magic == 0x434D5254)
                    {
                        Log($"[握手] 收到 {sip} 的TRMC，回复LPNT+DMOC");
                        byte[] lpnt = BuildLPNT(3, true);
                        _mainSock.Send(lpnt, lpnt.Length, sip, MAIN_PORT);
                        Thread.Sleep(50);
                        byte[] dmoc = BuildDMOC();
                        _mainSock.Send(dmoc, dmoc.Length, sip, MAIN_PORT);
                    }
                    // LOGI: 学生端登录请求
                    else if (magic == 0x49474F4C)
                    {
                        Log($"[登录] 收到 {sip} 的LOGI包！");
                        if (sip == _targetIP || _targetIP == "0.0.0.0")
                        {
                            HandleLogin(sip);
                        }
                        else
                        {
                            Log($"[登录] 非目标学生 {sip}（目标={_targetIP}），忽略");
                        }
                    }
                }
                catch (SocketException) { }
                catch (Exception ex)
                {
                    Log($"[接收] 异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 会话端口接收循环（5512端口），处理LOGI等包
        /// </summary>
        private void SessionRecvLoop()
        {
            int sessionPort = SESSION_PORT_BASE + _channel * 512;
            Log($"[会话接收] 启动，监听端口 {sessionPort}");

            while (_running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _sessionSock.Receive(ref remote);

                    if (data.Length < 4) continue;

                    uint magic = BitConverter.ToUInt32(data, 0);
                    string magicName = GetMagicName(magic);
                    string sip = remote.Address.ToString();

                    // 忽略自己的包
                    if (sip == _localIP) continue;

                    Log($"[会话接收] {magicName} from {sip}:{remote.Port}, len={data.Length}");

                    // LOGI: 学生端登录请求（在会话端口接收），只处理目标学生
                    if (magic == 0x49474F4C && (sip == _targetIP || _targetIP == "0.0.0.0"))
                    {
                        if (_connected)
                        {
                            Log($"[登录] {sip} 周期重发LOGI（心跳），回复MESS保持连接");
                            // 已登录学生周期重发LOGI，只需回复MESS保持连接
                            byte[] mess1 = BuildSessionReply(sip, 0x1000);
                            _sessionSock.Send(mess1, mess1.Length, sip, sessionPort);
                            Thread.Sleep(50);
                            byte[] mess2 = BuildSessionReply(sip, 0x8000);
                            _sessionSock.Send(mess2, mess2.Length, sip, sessionPort);
                        }
                        else
                        {
                            Log($"[登录] 收到 {sip} 的LOGI包，开始握手！");
                            HandleLogin(sip);
                        }
                    }
                }
                catch (SocketException) { }
                catch (Exception ex)
                {
                    Log($"[会话接收] 异常: {ex.Message}");
                }
            }
            Log("[会话接收] 停止");
        }

        /// <summary>
        /// 构造WACA包（回应学生端KACA）
        /// </summary>
        private byte[] BuildWACA()
        {
            byte[] guid = HexToBytes(TEACHER_GUID);
            byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x41434157); // WACA
                bw.Write((uint)0x10000);
                bw.Write((uint)8); // length
                bw.Write(guid);
                bw.Write(ip);
                bw.Write((uint)_channel);
                return ms.ToArray();
            }
        }

        private void HandleLogin(string sip)
        {
            if (_connected) return;

            Log($"[握手] 开始与 {sip} 握手...");

            try
            {
                int sessionPort = SESSION_PORT_BASE + _channel * 512;

                byte[] mess1 = BuildSessionReply(sip, 0x1000);
                _sessionSock.Send(mess1, mess1.Length, sip, sessionPort);
                Log($"[握手] MESS 0x1000 -> {sip}");
                Thread.Sleep(50);

                byte[] mess2 = BuildSessionReply(sip, 0x8000);
                _sessionSock.Send(mess2, mess2.Length, sip, sessionPort);
                Log($"[握手] MESS 0x8000 -> {sip}");
                Thread.Sleep(50);

                byte[] lpnt2 = BuildLPNT(2, true);
                _mainSock.Send(lpnt2, lpnt2.Length, sip, MAIN_PORT);
                Log($"[握手] LPNT subtype=2 -> {sip}");
                Thread.Sleep(50);

                byte[] lpnt3 = BuildLPNT(3, true);
                _mainSock.Send(lpnt3, lpnt3.Length, sip, MAIN_PORT);
                Log($"[握手] LPNT subtype=3 -> {sip}");
                Thread.Sleep(50);

                byte[] dmoc = BuildDMOC();
                _mainSock.Send(dmoc, dmoc.Length, sip, MAIN_PORT);
                Log($"[握手] DMOC -> {sip}");

                _connected = true;
                Log($"[成功] {sip} 已连接！");
                OnConnected?.Invoke(sip);
            }
            catch (Exception ex)
            {
                Log($"[握手] 失败: {ex.Message}");
            }
        }

        #region 包构造

        private byte[] BuildOONC()
        {
            byte[] guid = HexToBytes(TEACHER_GUID);
            byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x434E4F4F);
                bw.Write((uint)0x10000);
                bw.Write((uint)16);
                bw.Write(guid);
                bw.Write(ip);
                bw.Write((uint)1);
                bw.Write((uint)1);
                bw.Write((uint)_ooncSeq);
                _ooncSeq++;
                return ms.ToArray();
            }
        }

        private byte[] BuildNANC()
        {
            byte[] guid = HexToBytes(TEACHER_GUID);
            byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
            byte[] nameBytes = Encoding.Unicode.GetBytes(_teacherName + "\0");
            int nameChars = nameBytes.Length / 2 - 1;
            uint af = (uint)(nameChars << 17) | (uint)_channel;

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x434E414E);
                bw.Write((uint)0x10000);
                bw.Write((uint)0);
                long lenPos = ms.Position - 4;
                bw.Write(guid);
                bw.Write(af);
                bw.Write(ip);
                bw.Write(nameBytes);
                int declaredLen = 11 + nameChars * 2;
                // teacher_sim的length字段是declared_len，不是实际body长度
                // 同时body需要填充到至少declared_len
                int bodyLen = (int)(ms.Position - lenPos - 4);
                while (bodyLen < declaredLen) { bw.Write((byte)0); bodyLen++; }
                // 额外填充1字节使总长度为41（匹配teacher_sim）
                bw.Write((byte)0);
                long cur = ms.Position;
                ms.Position = lenPos;
                bw.Write((uint)declaredLen);
                ms.Position = cur;
                return ms.ToArray();
            }
        }

        private byte[] BuildCANC()
        {
            byte[] guid = HexToBytes(TEACHER_GUID);
            byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
            byte[] nameBytes = Encoding.Unicode.GetBytes(_teacherName + "\0");
            int nameChars = nameBytes.Length / 2 - 1;
            uint af = (uint)(nameChars << 17) | (uint)_channel;
            uint channelMask = (uint)(1 << (_channel - 1));

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x434E4143);
                bw.Write((uint)0x10000);
                bw.Write((uint)84); // length固定84
                bw.Write(guid);
                bw.Write(af);
                bw.Write(ip);
                bw.Write(channelMask);
                bw.Write((uint)1);
                bw.Write(nameBytes);
                // body从header(12)+guid(16)之后开始，填充到84字节
                int bodyLen = (int)ms.Position - 28;
                while (bodyLen < 84) { bw.Write((byte)0); bodyLen++; }
                return ms.ToArray();
            }
        }

        private byte[] BuildSessionReply(string sip, uint msgType)
        {
            byte[] ip = IPAddress.Parse(sip).GetAddressBytes();
            int sessionPort = SESSION_PORT_BASE + _channel * 512;

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x5353454D);
                bw.Write((uint)1);
                bw.Write((uint)1);
                bw.Write(ip);

                if (msgType == 0x1000)
                {
                    bw.Write((uint)0x0D);
                    bw.Write((uint)0x1000);
                    bw.Write((uint)0);
                    bw.Write((byte)0);
                }
                else
                {
                    bw.Write((uint)0x1B);
                    bw.Write((uint)0x8000);
                    bw.Write((uint)0);
                    bw.Write((uint)1);
                    bw.Write((ushort)sessionPort);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((uint)0);
                    bw.Write((uint)0);
                }
                return ms.ToArray();
            }
        }

        private byte[] BuildLPNT(int subtype, bool enabled)
        {
            byte[] guid = HexToBytes(LPNT_GUID);
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x544E504C);
                bw.Write((uint)0x10000);
                bw.Write((uint)20);
                bw.Write(guid);
                bw.Write((uint)subtype);
                bw.Write((uint)(enabled ? 1 : 0));
                bw.Write((uint)80);
                bw.Write((uint)60);
                bw.Write((uint)5);
                return ms.ToArray();
            }
        }

        private byte[] BuildDMOC()
        {
            byte[] guid = HexToBytes(DMOC_GUID);
            byte[] ip = IPAddress.Parse(_localIP).GetAddressBytes();
            byte[] dd;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)0x20); bw.Write((byte)0x4e); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write(ip);
                bw.Write((byte)0x35); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write((byte)0x35); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x01);
                bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x80);
                bw.Write(HexToBytes("e10202331e16e102023421160000a046000020419a99993fa0052000"));
                bw.Write((byte)0x01); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);
                bw.Write((byte)0x3d); bw.Write((byte)0x00);
                dd = ms.ToArray();
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)0x434F4D44);
                bw.Write((uint)0x10000);
                bw.Write((uint)dd.Length);
                bw.Write(guid);
                bw.Write(dd);
                return ms.ToArray();
            }
        }

        #endregion

        #region 单播操作（连接建立后可用）

        /// <summary>
        /// 发消息（单播到目标学生，DMOC协议）
        /// </summary>
        public bool SendMessage(string message)
        {
            if (!_connected || _mainSock == null)
            {
                Log("[操作] 未连接，无法发消息");
                return false;
            }
            try
            {
                byte[] data = new byte[1024];
                byte[] baseMsg = {
                    0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x9e,0x03,0x00,0x00,0x10,0x41,0xaf,0xfb,
                    0xa0,0xe7,0x52,0x40,0x91,0xdc,0x27,0xa3,0xb6,0xf9,0x29,0x2e,0x20,0x4e,0x00,0x00,
                    0xc0,0xa8,0x50,0x81,0x91,0x03,0x00,0x00,0x91,0x03,0x00,0x00,0x00,0x08,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x05,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
                };
                Array.Copy(baseMsg, data, 128);
                byte[] localIp = IPAddress.Parse(_localIP).GetAddressBytes();
                Array.Copy(localIp, 0, data, 32, 4);
                byte[] msgBytes = Encoding.Unicode.GetBytes(message);
                int copyLen = Math.Min(msgBytes.Length, 1024 - 56);
                Array.Copy(msgBytes, 0, data, 56, copyLen);
                _mainSock.Send(data, data.Length, _targetIP, MAIN_PORT);
                Log("[消息] -> " + _targetIP + ": " + message);
                return true;
            }
            catch (Exception ex)
            {
                Log("[消息] 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 黑屏（单播到目标学生，MESS协议，5秒后自动解除是极域机制）
        /// </summary>
        public bool SendBlackScreen(string text = null)
        {
            if (!_connected || _sessionSock == null)
            {
                Log("[操作] 未连接，无法黑屏");
                return false;
            }
            try
            {
                int sessionPort = SESSION_PORT_BASE + _channel * 512;
                int hasText = string.IsNullOrEmpty(text) ? 0 : 1;
                byte[] textUtf16 = hasText == 1 ? Encoding.Unicode.GetBytes(text + "\0") : new byte[0];
                int totalLen = 39 + textUtf16.Length;

                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write((uint)0x5353454D);
                    bw.Write((uint)1);
                    bw.Write((uint)1);
                    bw.Write(IPAddress.Parse(_targetIP).GetAddressBytes());
                    bw.Write((uint)totalLen);
                    bw.Write((uint)0x20);
                    bw.Write((uint)0x80000000);
                    bw.Write((uint)1);
                    bw.Write((uint)1);
                    bw.Write((uint)5);
                    bw.Write((uint)hasText);
                    bw.Write((uint)0x0000FFFF);
                    bw.Write((uint)0);
                    if (hasText == 1) bw.Write(textUtf16);
                    else bw.Write(new byte[] { 0xA0, 0x05, 0x20 });
                    byte[] mess = ms.ToArray();
                    _sessionSock.Send(mess, mess.Length, _targetIP, sessionPort);
                }
                Log("[黑屏] -> " + _targetIP + (text != null ? " [" + text + "]" : ""));
                return true;
            }
            catch (Exception ex)
            {
                Log("[黑屏] 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 解锁黑屏
        /// </summary>
        public bool SendUnlock()
        {
            if (!_connected || _sessionSock == null)
            {
                Log("[操作] 未连接，无法解锁");
                return false;
            }
            try
            {
                int sessionPort = SESSION_PORT_BASE + _channel * 512;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write((uint)0x5353454D);
                    bw.Write((uint)1);
                    bw.Write((uint)1);
                    bw.Write(IPAddress.Parse(_targetIP).GetAddressBytes());
                    bw.Write((uint)0x0D);
                    bw.Write((uint)0x20);
                    bw.Write((uint)0x90000000);
                    bw.Write((byte)0x01);
                    byte[] mess = ms.ToArray();
                    _sessionSock.Send(mess, mess.Length, _targetIP, sessionPort);
                }
                Log("[解锁] -> " + _targetIP);
                return true;
            }
            catch (Exception ex)
            {
                Log("[解锁] 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 关机（单播到目标学生，DMOC协议）
        /// </summary>
        public bool SendShutdown()
        {
            if (!_connected || _mainSock == null)
            {
                Log("[操作] 未连接，无法关机");
                return false;
            }
            try
            {
                byte[] data = new byte[1024];
                byte[] baseShutdown = {
                    0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x2a,0x02,0x00,0x00,0xc8,0xe3,0x97,0xfd,
                    0xc0,0xb5,0x9f,0x45,0x87,0x72,0x05,0xbd,0x4e,0x46,0xa8,0x96,0x20,0x4e,0x00,0x00,
                    0xc0,0xa8,0x50,0x81,0x1d,0x02,0x00,0x00,0x1d,0x02,0x00,0x00,0x00,0x02,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x14,0x00,0x00,0x10,0x0f,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5e,0x06,0x5c,0x73,0x51,0xed,0x95,0xa8,
                    0x60,0x84,0x76,0xa1,0x8b,0x97,0x7b,0x3a,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
                };
                Array.Copy(baseShutdown, data, 128);
                byte[] localIp = IPAddress.Parse(_localIP).GetAddressBytes();
                Array.Copy(localIp, 0, data, 32, 4);
                _mainSock.Send(data, data.Length, _targetIP, MAIN_PORT);
                Log("[关机] -> " + _targetIP);
                return true;
            }
            catch (Exception ex)
            {
                Log("[关机] 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 重启（单播到目标学生，DMOC协议）
        /// </summary>
        public bool SendReboot()
        {
            if (!_connected || _mainSock == null)
            {
                Log("[操作] 未连接，无法重启");
                return false;
            }
            try
            {
                byte[] data = new byte[1024];
                byte[] baseReboot = {
                    0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x2a,0x02,0x00,0x00,0xbf,0x40,0x22,0x4e,
                    0x57,0x2d,0x3e,0x4f,0x9b,0x6f,0xc1,0x8d,0xe1,0xeb,0x4f,0x62,0x20,0x4e,0x00,0x00,
                    0xc0,0xa8,0x50,0x81,0x1d,0x02,0x00,0x00,0x1d,0x02,0x00,0x00,0x00,0x02,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x13,0x00,0x00,0x10,0x0f,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5e,0x06,0x5c,0xcd,0x91,0x2f,0x54,0xa8,
                    0x60,0x84,0x76,0xa1,0x8b,0x97,0x7b,0x3a,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
                };
                Array.Copy(baseReboot, data, 128);
                byte[] localIp = IPAddress.Parse(_localIP).GetAddressBytes();
                Array.Copy(localIp, 0, data, 32, 4);
                _mainSock.Send(data, data.Length, _targetIP, MAIN_PORT);
                Log("[重启] -> " + _targetIP);
                return true;
            }
            catch (Exception ex)
            {
                Log("[重启] 失败: " + ex.Message);
                return false;
            }
        }

        #endregion

        public void Stop()
        {
            _running = false;
            _connected = false;
            try { _mainSock?.Close(); } catch { }
            try { _sessionSock?.Close(); } catch { }
            try { _sessionAnnoThread?.Join(1000); } catch { }
            try { _sessionRecvThread?.Join(1000); } catch { }
            Log("[停止] 单学生连接已停止");
            OnDisconnected?.Invoke(_targetIP);
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        private static string GetMagicName(uint magic)
        {
            byte[] bytes = BitConverter.GetBytes(magic);
            return Encoding.ASCII.GetString(bytes);
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}















