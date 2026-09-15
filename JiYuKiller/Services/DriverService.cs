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
        private const string DRIVER_DEVICE = @"\\.\JKRK";

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
        // 撤销自我保护(断开驱动对当前进程的保护回调)，对应参考实现 KFUnInstallSelfProtect()
        private static readonly uint CTL_CLIENT_QUIT = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x90C, METHOD_BUFFERED, FILE_ANY_ACCESS);
        // 卸载前的驱动反初始化，对应参考实现 KFBeforeUnInitDriver()
        private static readonly uint CTL_UNINIT = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x90D, METHOD_BUFFERED, FILE_ANY_ACCESS);

        // 参考实现 Driver.c 中所有以 pid/tid 为入参的 IOCTL 都按 ULONG_PTR 解引用
        // (32 位进程 4 字节, 64 位进程 8 字节)，入参长度必须与之严格一致。
        private static readonly int PidArgumentSize = IntPtr.Size;

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

        // RtlGetVersion 不受 AppCompat 兼容性垫片影响, 能拿到真实系统版本。
        // Environment.OSVersion / GetVersionEx 在缺少 supportedOS 清单时会把 Win8.1/Win10 报成 6.2.9200,
        // 会导致 isWin7/isWinXP 判断错误并给驱动灌入错误的偏移选择。
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref OsVersionInfoEx lpVersionInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

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
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int STATUS_SUCCESS = 0;

        private SafeFileHandle _driverHandle;
        private bool _disposed;

        public bool IsDriverLoaded { get; private set; }
        public bool IsDriverOpened { get; private set; }

        /// <summary>
        /// 获取真实系统版本(主版本/次版本/内部版本号)。
        /// 优先 RtlGetVersion, 失败时回退注册表, 再失败回退 Environment.OSVersion。
        /// </summary>
        public static bool TryGetRealWindowsVersion(out int major, out int minor, out int build)
        {
            major = 0;
            minor = 0;
            build = 0;

            try
            {
                var vi = new OsVersionInfoEx();
                vi.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(OsVersionInfoEx));
                if (RtlGetVersion(ref vi) == STATUS_SUCCESS && vi.dwBuildNumber > 0)
                {
                    major = (int)vi.dwMajorVersion;
                    minor = (int)vi.dwMinorVersion;
                    build = (int)vi.dwBuildNumber;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("[Driver] RtlGetVersion 调用失败, 回退注册表: " + ex.Message);
            }

            // 回退 1: 注册表 (对应参考实现 SysHlp::GetWindowsBulidVersion)
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object buildObj = key.GetValue("CurrentBuildNumber");
                        int parsedBuild;
                        if (buildObj != null && int.TryParse(Convert.ToString(buildObj), out parsedBuild) && parsedBuild > 0)
                        {
                            build = parsedBuild;
                            // Win10 起 CurrentMajorVersionNumber/CurrentMinorVersionNumber 才是真值,
                            // 老系统没有这两个值, 只能退回 CurrentVersion 字符串(如 "6.1")。
                            object majObj = key.GetValue("CurrentMajorVersionNumber");
                            object minObj = key.GetValue("CurrentMinorVersionNumber");
                            if (majObj != null && minObj != null)
                            {
                                major = Convert.ToInt32(majObj);
                                minor = Convert.ToInt32(minObj);
                            }
                            else
                            {
                                string cur = Convert.ToString(key.GetValue("CurrentVersion"));
                                string[] parts = (cur ?? "").Split('.');
                                if (parts.Length >= 1) int.TryParse(parts[0], out major);
                                if (parts.Length >= 2) int.TryParse(parts[1], out minor);
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("[Driver] 读取注册表系统版本失败, 回退 Environment.OSVersion: " + ex.Message);
            }

            // 回退 2: 可能被兼容性垫片影响
            Version v = Environment.OSVersion.Version;
            major = v.Major;
            minor = v.Minor;
            build = v.Build;
            return build > 0;
        }

        /// <summary>
        /// 获取真实内部版本号(build)。对应参考实现 SysHlp::GetWindowsBulidVersion()。
        /// </summary>
        public static uint GetRealWindowsBuild()
        {
            int major, minor, build;
            TryGetRealWindowsVersion(out major, out minor, out build);
            return build > 0 ? (uint)build : 0u;
        }

        /// <summary>
        /// 计算 JDRV_INITPARAM 需要的两个布尔标志。
        /// 参考实现 DriverLoader.cpp:XLoadDriver() 中:
        ///   isWin7 = (SysHlp::GetSystemVersion() == SystemVersionWindows7OrLater)
        ///   isXp   = (SysHlp::GetSystemVersion() == SystemVersionWindowsXP)
        /// 而 SysHlp::GetSystemVersion() 把 Vista 也归入 Windows7OrLater,
        /// 所以 isWin7 的真实含义是"Vista 及以上", 不是"内部版本号等于 7601"。
        /// </summary>
        public static void GetDriverInitFlags(out bool isXp, out bool isWin7)
        {
            int major, minor, build;
            TryGetRealWindowsVersion(out major, out minor, out build);

            isWin7 = major >= 6;
            isXp = !isWin7 && (major > 5 || (major == 5 && minor >= 1));
        }

        /// <summary>
        /// 日志用的系统版本描述
        /// </summary>
        public static string DescribeRealWindowsVersion()
        {
            int major, minor, build;
            TryGetRealWindowsVersion(out major, out minor, out build);
            return string.Format("{0}.{1}.{2}", major, minor, build);
        }

        /// <summary>
        /// 加载驱动
        /// </summary>
        public bool LoadDriver(string driverPath)
        {
            Logger.Instance.Info("[Driver] 开始加载驱动: " + driverPath);

            // 检查1: 64位系统不支持 (对应原项目XTestDriverCanUse)
            if (Environment.Is64BitOperatingSystem)
            {
                Logger.Instance.Warn("[Driver] 驱动不支持64位系统, 跳过加载");
                return false;
            }

            // 检查2: 非管理员且非XP系统不支持
            bool isAdmin = false;
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }
            // 用真实系统版本判断是否 XP: Environment.OSVersion 会被兼容性垫片影响
            bool isXpOs, isWin7Os;
            GetDriverInitFlags(out isXpOs, out isWin7Os);
            if (!isAdmin && !isXpOs)
            {
                Logger.Instance.Warn(string.Format(
                    "[Driver] 要加载驱动, 请以管理员身份运行本程序 (真实系统版本 {0}, isWin7={1}, isWinXP={2})",
                    DescribeRealWindowsVersion(), isWin7Os, isXpOs));
                return false;
            }

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
                Logger.Instance.Info("[Driver] 服务创建/启动成功, 等待打开设备");
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

            // 重复打开时先释放旧句柄, 避免句柄泄漏
            if (_driverHandle != null)
            {
                CloseDriver();
            }

            // 共享模式与参考实现 DriverLoader.cpp:XOpenDriver() 保持一致(FILE_SHARE_READ | FILE_SHARE_WRITE)。
            // 传 0 表示独占打开, 一旦本进程或其它组件已持有句柄就会 ERROR_SHARING_VIOLATION。
            _driverHandle = CreateFile(
                DRIVER_DEVICE,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (_driverHandle.IsInvalid)
            {
                Logger.Instance.Error("[Driver] 打开设备失败, 错误码: " + Marshal.GetLastWin32Error());
                IsDriverOpened = false;
                IsDriverLoaded = false;
                return false;
            }

            IsDriverOpened = true;
            IsDriverLoaded = true;
            Logger.Instance.Info("[Driver] 设备打开成功, 驱动已就绪");
            return true;
        }

        /// <summary>
        /// 发送初始化参数
        /// </summary>
        /// <remarks>
        /// JDRV_INITPARAM 结构体定义见参考实现 JiYuTrainerDriver\IoStructs.h:
        ///   typedef struct tag_JDRV_INITPARAM {
        ///       BOOLEAN IsWin7;        // 偏移 0, 1 字节
        ///       BOOLEAN IsWinXP;       // 偏移 1, 1 字节
        ///       ULONG   systemVersion; // 偏移 4, 4 字节 (2 字节对齐填充)
        ///   } JDRV_INITPARAM;          // sizeof == 8
        /// 驱动端 Driver.c:149 直接按 JDRV_INITPARAM* 解引用输入缓冲区, 因此必须是 8 字节且偏移一致。
        /// </remarks>
        public bool SendInitParam(bool isXp, bool isWin7, uint sysBuildVer)
        {
            if (!IsDriverOpened)
            {
                Logger.Instance.Warn("[Driver] 驱动未打开，无法发送初始化参数");
                return false;
            }

            byte[] param = new byte[8];
            param[0] = (byte)(isWin7 ? 1 : 0);
            param[1] = (byte)(isXp ? 1 : 0);
            // param[2..3] 为结构体对齐填充, 保持 0
            BitConverter.GetBytes(sysBuildVer).CopyTo(param, 4);

            bool ok = SendIoControl(CTL_INITPARAM, param, null);
            Logger.Instance.Info(string.Format(
                "[Driver] 已发送 JDRV_INITPARAM(8字节): IsWin7={0}, IsWinXP={1}, systemVersion={2}, 结果={3}",
                isWin7, isXp, sysBuildVer, ok));
            return ok;
        }

        /// <summary>
        /// 内核级杀进程
        /// </summary>
        /// <remarks>
        /// 驱动 Driver.c:214-233 的返回: 入参为 ULONG_PTR pid, 出参为 4 字节 NTSTATUS。
        /// 参考实现 KernelUtils.cpp:KForceKill() 正是用一个 NTSTATUS 变量接出参并判断 STATUS_SUCCESS。
        /// </remarks>
        public bool KillProcess(int pid)
        {
            int ntStatus;
            return KillProcess(pid, out ntStatus);
        }

        /// <summary>
        /// 内核级杀进程(带出参 NTSTATUS)
        /// </summary>
        public bool KillProcess(int pid, out int ntStatus)
        {
            Logger.Instance.Info("[Driver] 内核级杀进程 PID=" + pid);

            ntStatus = unchecked((int)0xC0000001); // STATUS_UNSUCCESSFUL
            byte[] inBuf = GetPidArgument(pid);
            byte[] outBuf = new byte[4];

            if (!SendIoControl(CTL_KILL_PROCESS, inBuf, outBuf))
            {
                return false;
            }

            ntStatus = BitConverter.ToInt32(outBuf, 0);
            if (ntStatus != STATUS_SUCCESS)
            {
                Logger.Instance.Error(string.Format(
                    "[Driver] CTL_KILL_PROCESS 返回错误状态: 0x{0:X8}", ntStatus));
                return false;
            }

            return true;
        }

        /// <summary>
        /// 按驱动期望的 ULONG_PTR 宽度构造 pid/tid 入参
        /// </summary>
        private static byte[] GetPidArgument(int pid)
        {
            return PidArgumentSize == 8
                ? BitConverter.GetBytes((long)pid)
                : BitConverter.GetBytes(pid);
        }

        /// <summary>
        /// 终止进程
        /// </summary>
        /// <remarks>
        /// 注意: 参考实现的驱动(JiYuTrainerDriver\Driver.c)的 IOCTL 分派 switch 里
        /// **没有** CTL_TREMINATE_PROCESS(0x906) 分支 —— IoCtl.h 只定义了宏, 驱动未实现。
        /// 因此调用它一定会失败(DeviceIoControl 返回 FALSE), 保留此方法仅为接口完整性。
        /// 需要杀进程请用 KillProcess(CTL_KILL_PROCESS, 0x900), 该分支才是驱动真正实现的。
        /// </remarks>
        public bool TerminateProcess(int pid)
        {
            Logger.Instance.Warn("[Driver] CTL_TERMINATE_PROCESS(0x906) 在驱动端未实现, 该调用会失败; 请改用 KillProcess");
            return SendProcessControl(CTL_TERMINATE_PROCESS, pid);
        }

        /// <summary>
        /// 挂起进程
        /// </summary>
        public bool SuspendProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 挂起进程 PID=" + pid);
            return SendProcessControl(CTL_SUSPEND_PROCESS, pid);
        }

        /// <summary>
        /// 恢复进程
        /// </summary>
        public bool ResumeProcess(int pid)
        {
            Logger.Instance.Info("[Driver] 恢复进程 PID=" + pid);
            return SendProcessControl(CTL_RESUME_PROCESS, pid);
        }

        /// <summary>
        /// 以 pid 为入参、NTSTATUS 为出参下发进程控制 IOCTL
        /// </summary>
        private bool SendProcessControl(uint ioControlCode, int pid)
        {
            byte[] inBuf = GetPidArgument(pid);
            byte[] outBuf = new byte[4];

            if (!SendIoControl(ioControlCode, inBuf, outBuf))
            {
                return false;
            }

            int ntStatus = BitConverter.ToInt32(outBuf, 0);
            if (ntStatus != STATUS_SUCCESS)
            {
                Logger.Instance.Error(string.Format(
                    "[Driver] IOCTL 0x{0:X} 返回错误状态: 0x{1:X8}", ioControlCode, ntStatus));
                return false;
            }

            return true;
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
        /// <remarks>
        /// 驱动 Driver.c:168-170:
        ///   case CTL_INITSELFPROTECT: {
        ///       ULONG_PTR pid = *(ULONG_PTR*)InputData;
        /// 该分支没有任何 InputData 非空判断, 直接解引用输入缓冲区。
        /// 因此这里必须传入"当前进程 PID"(参考实现 KernelUtils.cpp:KFInstallSelfProtect 传 GetCurrentProcessId()),
        /// 传 NULL 会导致内核读取空指针。
        /// </remarks>
        public bool InstallSelfProtect()
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Logger.Instance.Info("[Driver] 安装自我保护 (PID=" + pid + ")");

            bool ok = SendIoControl(CTL_INITSELFPROTECT, GetPidArgument(pid), null);
            if (!ok)
            {
                Logger.Instance.Error("[Driver] CTL_INITSELFPROTECT 失败, 错误码: " + Marshal.GetLastWin32Error());
            }
            return ok;
        }

        /// <summary>
        /// 撤销自我保护并关闭句柄。
        /// 对应参考实现 DriverLoader.cpp:XCloseDriverHandle()
        /// (先 KFUnInstallSelfProtect() 即 CTL_CLIENT_QUIT, 再 CloseHandle)。
        /// </summary>
        public void CloseDriver()
        {
            if (_driverHandle == null || _driverHandle.IsInvalid)
            {
                return;
            }

            // 必须先撤销自我保护, 否则驱动的进程保护回调还挂在当前进程上,
            // 后续 ControlService(STOP)/DeleteService 会被驱动自身拦截。
            if (IsDriverOpened)
            {
                SendIoControl(CTL_CLIENT_QUIT, null, null);
            }

            _driverHandle.Close();
            _driverHandle = null;
            IsDriverOpened = false;
            Logger.Instance.Info("[Driver] 驱动句柄已关闭");
        }

        /// <summary>
        /// 卸载驱动
        /// </summary>
        /// <remarks>
        /// 对应参考实现 DriverLoader.cpp:XUnLoadDriver():
        ///   1) KFBeforeUnInitDriver()  -> CTL_UNINIT
        ///   2) XCloseDriverHandle()    -> CTL_CLIENT_QUIT + CloseHandle
        ///   3) MUnLoadKernelDriver()   -> ControlService(STOP) + DeleteService + MRegForceDeleteServiceRegkey()
        /// </remarks>
        public bool UnloadDriver()
        {
            Logger.Instance.Info("[Driver] 卸载驱动");

            // 1) 驱动反初始化
            if (IsDriverOpened)
            {
                SendIoControl(CTL_UNINIT, null, null);
            }

            // 2) 撤销自我保护 + 关闭句柄
            CloseDriver();

            // 3) 停止并删除服务
            IntPtr hSCM = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (hSCM == IntPtr.Zero)
            {
                Logger.Instance.Error("[Driver] 卸载驱动时打开SCM失败, 错误码: " + Marshal.GetLastWin32Error());
                return false;
            }

            bool deleted = false;
            try
            {
                IntPtr hService = OpenService(hSCM, DRIVER_NAME, SERVICE_ALL_ACCESS);
                if (hService == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                    {
                        Logger.Instance.Warn("[Driver] 驱动服务不存在，无需卸载");
                    }
                    else
                    {
                        Logger.Instance.Error("[Driver] 打开驱动服务失败, 错误码: " + err);
                    }
                }
                else
                {
                    try
                    {
                        ServiceStatus status;
                        if (!ControlService(hService, SERVICE_CONTROL_STOP, out status))
                        {
                            // 服务未运行时停止会失败, 不阻断删除流程
                            Logger.Instance.Warn("[Driver] 停止驱动服务失败, 错误码: " + Marshal.GetLastWin32Error());
                        }

                        if (!DeleteService(hService))
                        {
                            Logger.Instance.Error("[Driver] 删除驱动服务失败, 错误码: " + Marshal.GetLastWin32Error());
                        }
                        else
                        {
                            deleted = true;
                            Logger.Instance.Info("[Driver] 驱动服务已删除");
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(hService);
                    }
                }
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

            // 删除服务后必须清掉注册表残留, 否则下次 CreateService 会返回
            // ERROR_SERVICE_MARKED_FOR_DELETE 而无法重新加载驱动。
            if (deleted)
            {
                DeleteServiceRegKeys();
            }

            IsDriverLoaded = false;
            return true;
        }

        /// <summary>
        /// 删除服务注册表残留。对应参考实现 RegHlp.cpp:MRegForceDeleteServiceRegkey()
        ///   HKLM\SYSTEM\CurrentControlSet\services\&lt;服务名&gt;
        ///   HKLM\SYSTEM\CurrentControlSet\Enum\Root\LEGACY_&lt;大写服务名&gt;
        /// </summary>
        private static void DeleteServiceRegKeys()
        {
            const string servicesPath = @"SYSTEM\CurrentControlSet\services";
            string legacyPath = @"SYSTEM\CurrentControlSet\Enum\Root\LEGACY_" + DRIVER_NAME.ToUpperInvariant();

            try
            {
                using (var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(servicesPath, true))
                {
                    if (services != null)
                    {
                        services.DeleteSubKeyTree(DRIVER_NAME, false);
                        Logger.Instance.Info("[Driver] 已删除注册表服务键: HKLM\\" + servicesPath + "\\" + DRIVER_NAME);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("[Driver] 删除注册表服务键失败: " + ex.Message);
            }

            // 枚举键(HKLM\SYSTEM\CurrentControlSet\Enum\Root\LEGACY_xxx)：
            // 这个键由系统在服务首次加载时创建，其 ACL 把写/删权限留给 SYSTEM，
            // 即使以管理员运行也常常拿不到 —— Win7 上实测必然失败(错误: 不允许所请求的注册表访问权)，
            // Win10 上则通常能删掉。参考实现(RegHlp.cpp)对这一项的失败同样当作无害处理。
            // 它只是残留记录，不影响下次加载驱动(关键的是上面的 services\ 键)，
            // 所以这里只记 Debug，不再用 WARN 以免每次卸载都刷一条看起来像出错的消息。
            try
            {
                using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\Root", true))
                {
                    if (root != null)
                    {
                        root.DeleteSubKeyTree("LEGACY_" + DRIVER_NAME.ToUpperInvariant(), false);
                        Logger.Instance.Info("[Driver] 已删除注册表枚举键: HKLM\\" + legacyPath);
                    }
                }
            }
            catch (Exception ex)
            {
                // 已确认无害: services\ 键已删除，驱动下次仍可正常加载。
                Logger.Instance.Debug("[Driver] 注册表枚举键未能删除(受 ACL 保护, 无害): " + ex.Message);
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
