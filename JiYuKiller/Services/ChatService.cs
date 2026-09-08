using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 小小私聊服务
    /// 原理：极域学生端不对UDP包做身份验证，可构造数据包发送消息
    /// 座位号换算算法移植自jiyu_chat，按6人一排布局推算
    /// </summary>
    public class ChatService
    {
        private const int DefaultPort = 4705;
        private const int MaxMessageLength = 80;

        // 事件
        public event Action<string> OnLog;
        public event Action<string> OnChatRecord;
        public event Action<bool, string> OnSendResult;

        private UdpClient _udpClient;
        private int _mySeatID = 0;
        private string _localIP = "127.0.0.1";

        public int MySeatID => _mySeatID;
        public string LocalIP => _localIP;

        public ChatService()
        {
            Logger.Instance.Info("[ChatService] 小小私聊服务初始化");
        }

        /// <summary>
        /// 初始化本机信息（IP和座位号）
        /// </summary>
        public void InitLocalInfo()
        {
            try
            {
                _localIP = GetLocalIP();
                int ipLast = GetIPLastOctet(_localIP);
                _mySeatID = IPLast2StuID(ipLast);
                Logger.Instance.Info($"[ChatService] 本机IP: {_localIP}，推算座位号: {_mySeatID}");
                OnLog?.Invoke($"本机IP: {_localIP}   座位号: {_mySeatID}");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ChatService] 初始化本机信息失败: " + ex.Message);
                OnLog?.Invoke("获取本机信息失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 座位号转IP末位（6人一排布局）
        /// </summary>
        public static int StuID2IPLast(int stuID)
        {
            if (stuID <= 0) return 0;
            if (stuID % 6 == 0)
            {
                int op = stuID / 6;
                return 60 + op;
            }
            else
            {
                int r = stuID % 6;
                int tp = r * 10;
                int op = stuID / 6 + 1;
                return tp + op;
            }
        }

        /// <summary>
        /// IP末位转座位号
        /// </summary>
        public static int IPLast2StuID(int ipLastOctet)
        {
            int m = ipLastOctet;
            int o = m - 1;
            int tens = o / 10;
            return (m - tens * 10) * 6 - (6 - tens);
        }

        /// <summary>
        /// 查找同学（座位号转IP + ping检测）
        /// </summary>
        public async Task<Tuple<bool, string, string>> FindClassmate(int seatID)
        {
            if (seatID <= 0)
            {
                OnChatRecord?.Invoke("请输入有效的座位号！");
                return Tuple.Create(false, "", "无效座位号");
            }

            int ipLast = StuID2IPLast(seatID);
            string ipPrefix = GetLocalIPPrefix();
            string targetIP = ipPrefix + ipLast;

            Logger.Instance.Info($"[ChatService] 查找: 座位号 {seatID} -> IP {targetIP}");
            OnChatRecord?.Invoke($"查找 {seatID} 号同学 -> {targetIP}");

            // ping检测
            bool online = await IsOnline(targetIP);
            if (online)
            {
                OnChatRecord?.Invoke($"{seatID} 号同学在线");
                return Tuple.Create(true, targetIP, $"{seatID}号同学在线");
            }
            else
            {
                OnChatRecord?.Invoke($"{seatID} 号同学不在线或未开机");
                return Tuple.Create(false, targetIP, $"{seatID}号同学不在线或未开机");
            }
        }

        /// <summary>
        /// 发送私聊消息
        /// </summary>
        public async Task SendMessage(string targetIP, string message, int seatID, int port = DefaultPort)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                OnChatRecord?.Invoke("不可发送空白消息！");
                OnSendResult?.Invoke(false, "消息为空");
                return;
            }
            if (message.Length > MaxMessageLength)
            {
                OnChatRecord?.Invoke($"消息太长了，请控制在{MaxMessageLength}字以内。");
                OnSendResult?.Invoke(false, "消息过长");
                return;
            }
            if (string.IsNullOrEmpty(targetIP))
            {
                OnChatRecord?.Invoke("请先点击「查找同学」确定目标！");
                OnSendResult?.Invoke(false, "未指定目标");
                return;
            }

            // 消息前缀：「X号同学:」，与jiyu_chat的prefix逻辑一致
            string fullMsg = $"{_mySeatID}号同学:{message}";

            Logger.Instance.Info($"[ChatService] 发送 -> {targetIP}:{port} : {fullMsg}");

            try
            {
                if (_udpClient == null)
                {
                    _udpClient = new UdpClient();
                }

                byte[] data = Encoding.Unicode.GetBytes(fullMsg);
                await _udpClient.SendAsync(data, data.Length, targetIP, port);

                OnChatRecord?.Invoke($"我 -> {seatID}号: {message}");
                OnSendResult?.Invoke(true, "发送成功");
                Logger.Instance.Info($"[ChatService] 发送成功，{data.Length}字节");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ChatService] 发送失败: " + ex.Message);
                OnChatRecord?.Invoke("发送失败: " + ex.Message);
                OnSendResult?.Invoke(false, ex.Message);
            }
        }

        /// <summary>
        /// 获取本机IP
        /// </summary>
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
            catch
            {
                // 回退：遍历网络接口
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                return ip.Address.ToString();
                            }
                        }
                    }
                }
                return "127.0.0.1";
            }
        }

        private string GetLocalIPPrefix()
        {
            string ip = GetLocalIP();
            int pos = ip.LastIndexOf('.');
            if (pos < 0) return "192.168.1.";
            return ip.Substring(0, pos + 1);
        }

        private int GetIPLastOctet(string ip)
        {
            try
            {
                string[] parts = ip.Split('.');
                if (parts.Length == 4) return int.Parse(parts[3]);
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Ping检测对方是否在线
        /// </summary>
        private async Task<bool> IsOnline(string ip)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(ip, 1000);
                    return reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _udpClient?.Close();
            _udpClient = null;
        }
    }
}
