using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DolbyKiller.Services
{
    /// <summary>
    /// 端口检测工具
    /// 使用 IPHelper API 获取端口→PID 映射，检测 DolbyVision/DolbyAccess 固定端口段
    /// </summary>
    public static class PortHelper
    {
        // 关注的端口列表（DolbyVision/DolbyAccess 固定端口）
        public static readonly int[] TcpPorts = { 9101, 9102, 9103, 9112, 9113, 9114 };
        public static readonly int[] UdpPorts = { 9100 };

        #region IPHelper P/Invoke

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, TcpTableClass tblClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, UdpTableClass tblClass, uint reserved);

        private enum TcpTableClass
        {
            TcpTableBasicListener = 0,
            TcpTableBasicConnections = 1,
            TcpTableBasicAll = 2,
            TcpTableOwnerPidListener = 3,
            TcpTableOwnerPidConnections = 4,
            TcpTableOwnerPidAll = 5,
            TcpTableOwnerModuleListener = 6,
            TcpTableOwnerModuleConnections = 7,
            TcpTableOwnerModuleAll = 8
        }

        private enum UdpTableClass
        {
            UdpTableBasic = 0,
            UdpTableOwnerPid = 1,
            UdpTableOwnerModule = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public byte remotePort1;
            public byte remotePort2;
            public byte remotePort3;
            public byte remotePort4;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpTableOwnerPid
        {
            public uint dwNumEntries;
            public MibTcpRowOwnerPid table;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibUdpRowOwnerPid
        {
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibUdpTableOwnerPid
        {
            public uint dwNumEntries;
            public MibUdpRowOwnerPid table;
        }

        private const int AfInet = 2; // IPv4
        private const int ErrorInsufficientBuffer = 122;

        #endregion

        /// <summary>
        /// 获取所有 TCP 监听端口 → PID 映射
        /// </summary>
        public static Dictionary<int, int> GetTcpListeners()
        {
            var result = new Dictionary<int, int>();
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AfInet,
                TcpTableClass.TcpTableOwnerPidListener, 0);

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                uint ret = GetExtendedTcpTable(buffer, ref bufferSize, false, AfInet,
                    TcpTableClass.TcpTableOwnerPidListener, 0);
                if (ret != 0) return result;

                MibTcpTableOwnerPid table = (MibTcpTableOwnerPid)Marshal.PtrToStructure(
                    buffer, typeof(MibTcpTableOwnerPid));

                IntPtr rowPtr = new IntPtr(buffer.ToInt64() + Marshal.SizeOf(typeof(uint)));
                int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));

                for (int i = 0; i < table.dwNumEntries; i++)
                {
                    MibTcpRowOwnerPid row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(
                        rowPtr, typeof(MibTcpRowOwnerPid));
                    int port = (row.localPort1 << 8) | row.localPort2;
                    if (port > 0 && !result.ContainsKey(port))
                    {
                        result[port] = (int)row.owningPid;
                    }
                    rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        /// <summary>
        /// 获取所有 UDP 监听端口 → PID 映射
        /// </summary>
        public static Dictionary<int, int> GetUdpListeners()
        {
            var result = new Dictionary<int, int>();
            int bufferSize = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AfInet,
                UdpTableClass.UdpTableOwnerPid, 0);

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                uint ret = GetExtendedUdpTable(buffer, ref bufferSize, false, AfInet,
                    UdpTableClass.UdpTableOwnerPid, 0);
                if (ret != 0) return result;

                MibUdpTableOwnerPid table = (MibUdpTableOwnerPid)Marshal.PtrToStructure(
                    buffer, typeof(MibUdpTableOwnerPid));

                IntPtr rowPtr = new IntPtr(buffer.ToInt64() + Marshal.SizeOf(typeof(uint)));
                int rowSize = Marshal.SizeOf(typeof(MibUdpRowOwnerPid));

                for (int i = 0; i < table.dwNumEntries; i++)
                {
                    MibUdpRowOwnerPid row = (MibUdpRowOwnerPid)Marshal.PtrToStructure(
                        rowPtr, typeof(MibUdpRowOwnerPid));
                    int port = (row.localPort1 << 8) | row.localPort2;
                    if (port > 0 && !result.ContainsKey(port))
                    {
                        result[port] = (int)row.owningPid;
                    }
                    rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        /// <summary>
        /// 检测 DolbyVision/DolbyAccess 固定端口是否被占用
        /// 返回被占用的端口列表（端口号, PID, 协议）
        /// </summary>
        public static List<(int Port, int Pid, string Protocol)> GetDolbyPorts()
        {
            var found = new List<(int, int, string)>();

            try
            {
                var tcp = GetTcpListeners();
                foreach (int port in TcpPorts)
                {
                    if (tcp.TryGetValue(port, out int pid))
                    {
                        found.Add((port, pid, "TCP"));
                    }
                }
            }
            catch { }

            try
            {
                var udp = GetUdpListeners();
                foreach (int port in UdpPorts)
                {
                    if (udp.TryGetValue(port, out int pid))
                    {
                        found.Add((port, pid, "UDP"));
                    }
                }
            }
            catch { }

            return found;
        }
    }
}
