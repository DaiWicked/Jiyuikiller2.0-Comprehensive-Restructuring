using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 内核驱动交互服务
    /// 对应原项目 DriverLoader.cpp + KernelUtils.cpp
    /// 驱动文件: JiYuTrainerDriver.sys
    /// </summary>
    public class DriverService : IDisposable
    {
        private const string DRIVER_NAME = "JiYuTrainerDriver";
        private const string DRIVER_DEVICE = @"\\.\JiYuTrainerDriver";

        // IOCTL 控制码（对应原项目 IoCtl.h）
        private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_ANY_ACCESS = 0;

        private static uint CTL_CODE(uint DeviceType, uint Function, uint Method, uint Access)
        {
            return ((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method);
        }

        private static readonly uint CTL_INITPARAM = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x989D, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_INITSELFPROTECT = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x989E, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_KILL_PROCESS = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_TERMINATE_PROCESS = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x906, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_SUSPEND_PROCESS = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x908, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_RESUME_PROCESS = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x909, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_SHUTDOWN = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x90A, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_REBOOT = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x90B, METHOD_BUFFERED, FILE_ANY_ACCESS);
        private static readonly uint CTL_UNINIT = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x90D, METHOD_BUFFERED, FILE_ANY_ACCESS);

        // Win32 API
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, out ServiceStatus lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_KERNEL_DRIVER = 0x00000001;
        private const uint SERVICE_DEMAND_START = 0x00000003;
        private const uint SERVICE_ERROR_NORMAL = 0x00000001;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private SafeFileHandle _driverHandle;
        private bool _disposed;

        public bool IsDriverLoaded { get; private set; }
        public bool IsDriverOpened { get; private set; }

        /// <summary>
        /// 加载驱动
        /// </summary>
        public bool LoadDriver(string driverPath)
        {
            Logger.Instance.Info("[Driver] 开始加载驱动: " + driverPath);

            if (!File.Exists(driverPath))
            {
                Logger.Instance.Error("[Driver] 驱动文件不存在: " + driverPath);
                return false;
            }

            IntPtr hSCM = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (hSCM == IntPtr.Zero)
            {
                Logger.Instance.Error("[Driver] 打开SCM失败, 错误码: " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                // 尝试打开已有服务
                IntPtr hService = OpenService(hSCM, DRIVER_NAME, SERVICE_ALL_ACCESS);
                if (hService == IntPtr.Zero)
                {
                    // 创建服务
                    hService = CreateService(
                        hSCM,
                        DRIVER_NAME,
                        DRIVER_NAME,
                        SERVICE_ALL_ACCESS,
                        SERVICE_KERNEL_DRIVER,
                        SERVICE_DEMAND_START,
                        SERVICE_ERROR_NORMAL,
                        driverPath,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null);

                    if (hService == IntPtr.Zero)
                    {
                        Logger.Instance.Error("[Driver] 创建服务失败, 错误码: " + Marshal.GetLastWin32Error());
                        return false;
                    }
                    Logger.Instance.Info("[Driver] 服务创建成功");
                }
                else
                {
                    Logger.Instance.Info("[Driver] 服务已存在");
                }

                // 启动服务
                if (!StartService(hService, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                    {
                        Logger.Instance.Error("[Driver] 启动服务失败, 错误码: " + err);
                        CloseServiceHandle(hService);
                        return false;
                    }
                    Logger.Instance.Info("[Driver] 服务已在运行");
                }
                else
                {
                    Logger.Instance.Info("[Driver] 服务启动成功");
                }

                CloseServiceHandle(hService);
                IsDriverLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Driver] 加载驱动异常", ex);
                return false;
            }
            finally
            {
                CloseServiceHandle(hSCM);
            }
        }

        /// <summary>
        /// 打开驱动设备
        /// </summary>
        public bool OpenDriver()
        {
            Logger.Instance.Info("[Driver] 打开驱动设备: " + DRIVER_DEVICE);

            _driverHandle = CreateFile(
                DRIVER_DEVICE,
                GENERIC_READ | GENERIC_WRITE,
                0,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (_driverHandle.IsInvalid)
            {
                Logger.Instance.Error("[Driver] 打开设备失败, 错误码: " + Marshal.GetLastWin32Error());
                IsDriverOpened = false;
                return false;
            }

            IsDriverOpened = true;
            Logger.Instance.Info("[Driver] 设备打开成功");
            return true;
        }

        /// <summary>
        /// 发送初始化参数
        /// </summary>
        public bool SendInitParam(bool isXp, bool isWin7, uint sysBuildVer)
        {
            if (!IsDriverOpened)
            {
                Logger.Instance.Warn("[Driver] 驱动未打开，无法发送初始化参数");
                return false;
            }

            byte[] param = new byte[12];
            BitConverter.GetBytes(isXp ? 1 : 0).CopyTo(param, 0);
            BitConverter.GetBytes(isWin7 ? 1 : 0).CopyTo(param, 4);
            BitConverter.GetBytes(sysBuildVer).CopyTo(param, 8);

            return SendIoControl(CTL_INITPARAM, param, null);
        }

        /// <summary>
        /// 内核级杀进程
        /// </summary>
        public bool KillProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 内核级杀进程 PID=" + pid);
            byte[] inBuf = BitConverter.GetBytes(pid);
            return SendIoControl(CTL_KILL_PROCESS, inBuf, null);
        }

        /// <summary>
        /// 终止进程
        /// </summary>
        public bool TerminateProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 终止进程 PID=" + pid);
            byte[] inBuf = BitConverter.GetBytes(pid);
            return SendIoControl(CTL_TERMINATE_PROCESS, inBuf, null);
        }

        /// <summary>
        /// 挂起进程
        /// </summary>
        public bool SuspendProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 挂起进程 PID=" + pid);
            byte[] inBuf = BitConverter.GetBytes(pid);
            return SendIoControl(CTL_SUSPEND_PROCESS, inBuf, null);
        }

        /// <summary>
        /// 恢复进程
        /// </summary>
        public bool ResumeProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 恢复进程 PID=" + pid);
            byte[] inBuf = BitConverter.GetBytes(pid);
            return SendIoControl(CTL_RESUME_PROCESS, inBuf, null);
        }

        /// <summary>
        /// 内核级关机
        /// </summary>
        public bool Shutdown()
        {
            Logger.Instance.Info("[Driver] 内核级关机");
            return SendIoControl(CTL_SHUTDOWN, null, null);
        }

        /// <summary>
        /// 内核级重启
        /// </summary>
        public bool Reboot()
        {
            Logger.Instance.Info("[Driver] 内核级重启");
            return SendIoControl(CTL_REBOOT, null, null);
        }

        /// <summary>
        /// 安装自我保护
        /// </summary>
        public bool InstallSelfProtect()
        {
            Logger.Instance.Info("[Driver] 安装自我保护");
            return SendIoControl(CTL_INITSELFPROTECT, null, null);
        }

        /// <summary>
        /// 卸载驱动
        /// </summary>
        public bool UnloadDriver()
        {
            Logger.Instance.Info("[Driver] 卸载驱动");

            CloseDriver();

            IntPtr hSCM = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (hSCM == IntPtr.Zero) return false;

            try
            {
                IntPtr hService = OpenService(hSCM, DRIVER_NAME, SERVICE_ALL_ACCESS);
                if (hService != IntPtr.Zero)
                {
                    ServiceStatus status;
                    ControlService(hService, SERVICE_CONTROL_STOP, out status);
                    DeleteService(hService);
                    CloseServiceHandle(hService);
                    Logger.Instance.Info("[Driver] 驱动服务已删除");
                }
                IsDriverLoaded = false;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Driver] 卸载驱动异常", ex);
                return false;
            }
            finally
            {
                CloseServiceHandle(hSCM);
            }
        }

        /// <summary>
        /// 关闭驱动句柄
        /// </summary>
        public void CloseDriver()
        {
            if (_driverHandle != null && !_driverHandle.IsInvalid)
            {
                _driverHandle.Close();
                _driverHandle = null;
                IsDriverOpened = false;
                Logger.Instance.Info("[Driver] 驱动句柄已关闭");
            }
        }

        private bool SendIoControl(uint ioControlCode, byte[] inBuffer, byte[] outBuffer)
        {
            if (!IsDriverOpened || _driverHandle == null || _driverHandle.IsInvalid)
            {
                Logger.Instance.Warn("[Driver] 驱动未打开，IOCTL失败");
                return false;
            }

            IntPtr inPtr = IntPtr.Zero;
            IntPtr outPtr = IntPtr.Zero;
            uint bytesReturned;

            try
            {
                if (inBuffer != null)
                {
                    inPtr = Marshal.AllocHGlobal(inBuffer.Length);
                    Marshal.Copy(inBuffer, 0, inPtr, inBuffer.Length);
                }

                if (outBuffer != null)
                {
                    outPtr = Marshal.AllocHGlobal(outBuffer.Length);
                }

                bool result = DeviceIoControl(
                    _driverHandle,
                    ioControlCode,
                    inPtr,
                    inBuffer != null ? (uint)inBuffer.Length : 0,
                    outPtr,
                    outBuffer != null ? (uint)outBuffer.Length : 0,
                    out bytesReturned,
                    IntPtr.Zero);

                if (!result)
                {
                    Logger.Instance.Error("[Driver] IOCTL 0x" + ioControlCode.ToString("X") + " 失败, 错误码: " + Marshal.GetLastWin32Error());
                }

                if (outBuffer != null && outPtr != IntPtr.Zero)
                {
                    Marshal.Copy(outPtr, outBuffer, 0, outBuffer.Length);
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Driver] IOCTL 0x" + ioControlCode.ToString("X") + " 异常", ex);
                return false;
            }
            finally
            {
                if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
                if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    CloseDriver();
                }
                _disposed = true;
            }
        }

        ~DriverService()
        {
            Dispose(false);
        }
    }
}
