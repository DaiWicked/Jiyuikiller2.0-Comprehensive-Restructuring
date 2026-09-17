using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JiYuKiller.Services
{
    /// <summary>
    /// DLL 注入服务
    /// 对应原项目 TrainerWorker.cpp 的 InjectDll / UnInjectDll
    /// 使用 CreateRemoteThread + LoadLibraryW 标准注入方式
    /// </summary>
    public class DllInjectService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        // ==== Toolhelp32 模块枚举 ====
        // 用途: 判断目标进程里"某个 DLL 到底加载了没有"。
        // 这是"注入是否成功"的真实判据 —— 远程线程有没有返回并不等于 DLL 没加载成功:
        // 实测(VM 日志 02:20)出现过远程线程超 5 秒未返回、但 DLL 其实已经加载的情况,
        // 第二次注入因"模块已存在"而在同一毫秒内返回(LoadLibraryW 只把引用计数 +1)。
        private const uint TH32CS_SNAPMODULE = 0x00000008;
        private const uint TH32CS_SNAPMODULE32 = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MODULEENTRY32W
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        /// <summary>
        /// 查询目标进程是否已加载指定文件名的模块。
        /// </summary>
        /// <remarks>
        /// 标志必须用 TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32 (0x18)：
        /// 单独用 0x10 在 64 位目标上会以错误 18(ERROR_NO_MORE_FILES) 失败（已实测）。
        /// 本程序是 32 位、目标极域也是 32 位，0x18 组合在 32 位调用方 + 32 位目标下实测可用。
        /// </remarks>
        private static bool TryGetRemoteModule(int pid, string moduleFileName, out IntPtr baseAddress)
        {
            baseAddress = IntPtr.Zero;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                Logger.Instance.Debug("[Inject] 模块快照失败, 错误码: " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                MODULEENTRY32W entry = new MODULEENTRY32W();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));

                if (!Module32FirstW(snapshot, ref entry))
                {
                    Logger.Instance.Debug("[Inject] Module32First 失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                do
                {
                    if (string.Equals(entry.szModule, moduleFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        baseAddress = entry.modBaseAddr;
                        return true;
                    }
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
                }
                while (Module32NextW(snapshot, ref entry));

                return false;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private const uint PROCESS_CREATE_THREAD = 0x0002;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 4;
        private const uint INFINITE = 0xFFFFFFFF;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const uint STILL_ACTIVE = 259;

        /// <summary>
        /// 注入 DLL 到目标进程
        /// </summary>
        public bool InjectDll(int pid, string dllPath)
        {
            Logger.Instance.Info($"[Inject] 开始注入 DLL: PID={pid}, DLL={dllPath}");

            if (!System.IO.File.Exists(dllPath))
            {
                Logger.Instance.Error("[Inject] DLL 文件不存在: " + dllPath);
                return false;
            }

            // 注入前先确认目标进程是否已经加载过这个 DLL。
            // 已加载就直接按成功返回: 重复注入只会把引用计数 +1，并可能触发一次多余的 DLL 回调
            //（VM 实测日志里出现过这种"第二次注入在同一毫秒内返回成功"的冗余注入）。
            string moduleFileName = System.IO.Path.GetFileName(dllPath);
            IntPtr alreadyLoadedBase;
            if (TryGetRemoteModule(pid, moduleFileName, out alreadyLoadedBase))
            {
                Logger.Instance.Info(string.Format(
                    "[Inject] 目标进程已加载 {0} (基址=0x{1:X8}), 跳过重复注入: PID={2}",
                    moduleFileName, (long)alreadyLoadedBase, pid));
                return true;
            }

            IntPtr hProcess = OpenProcess(
                PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                false, pid);

            if (hProcess == IntPtr.Zero)
            {
                Logger.Instance.Error("[Inject] 打开进程失败, 错误码: " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                // 分配远程内存
                byte[] dllPathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
                IntPtr lpRemoteString = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllPathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

                if (lpRemoteString == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 分配远程内存失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                // 写入 DLL 路径
                UIntPtr bytesWritten;
                if (!WriteProcessMemory(hProcess, lpRemoteString, dllPathBytes, (uint)dllPathBytes.Length, out bytesWritten))
                {
                    Logger.Instance.Error("[Inject] 写入进程内存失败, 错误码: " + Marshal.GetLastWin32Error());
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                // 获取 LoadLibraryW 地址
                IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                IntPtr lpLoadLibrary = GetProcAddress(hKernel32, "LoadLibraryW");

                if (lpLoadLibrary == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 获取 LoadLibraryW 地址失败");
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                // 创建远程线程
                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, lpLoadLibrary, lpRemoteString, 0, out IntPtr threadId);

                if (hThread == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 创建远程线程失败, 错误码: " + Marshal.GetLastWin32Error());
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                // 等待线程结束。
                // 用 15 秒而不是原来的 5 秒: 实测正常注入耗时 0.20s / 0.53s / 1.85s / 2.66s / 3.45s，
                // 但 VM 上出现过一次 >5 秒(首次加载 580KB 的 DLL 时被极域自己的加载器锁拖慢)，
                // 固定 5 秒会把"慢但确实成功"的注入误判为失败。
                const uint InjectWaitMs = 15000;
                uint waitResult = WaitForSingleObject(hThread, InjectWaitMs);

                if (waitResult == WAIT_TIMEOUT)
                {
                    CloseHandle(hThread);
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);

                    // 超时不能直接判失败: "远程线程没返回"不等于"DLL 没加载"。
                    // 用模块枚举查真实结果 —— 这才是注入是否成功的判据。
                    IntPtr loadedBase;
                    if (TryGetRemoteModule(pid, moduleFileName, out loadedBase))
                    {
                        Logger.Instance.Info(string.Format(
                            "[Inject] 远程线程等待超时({0}ms), 但模块确已加载, 按成功处理: PID={1}, 模块基址=0x{2:X8}",
                            InjectWaitMs, pid, (long)loadedBase));
                        return true;
                    }

                    Logger.Instance.Error(string.Format(
                        "[Inject] 远程线程等待超时({0}ms)且模块未加载, 注入失败: PID={1}, DLL={2}",
                        InjectWaitMs, pid, dllPath));
                    return false;
                }

                // 读取远程线程退出码（从此只作为诊断信息，不再作为成功判据）
                uint exitCode;
                bool gotExitCode = GetExitCodeThread(hThread, out exitCode);
                CloseHandle(hThread);
                VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);

                // ==== 最终判据: 目标进程的模块列表里到底有没有这个 DLL ====
                //
                // 为什么不能只看 LoadLibraryW 的返回值:
                //   成功时它返回 HMODULE(基址)，失败返回 0；但如果线程内部发生异常，
                //   GetExitCodeThread 拿到的是"异常码"而不是 0。
                //   实测(本机用一个 32 位假靶子)就出现过返回 0xC0000005(STATUS_ACCESS_VIOLATION)，
                //   旧判据只拦 `== 0`，于是把它当成"模块基址 0xC0000005"并报"注入成功"，
                //   而事实上 DLL 根本没加载 —— 紧接着的日志就是"未找到病毒窗口"。
                // 只有"模块已出现在目标进程的模块表里"才是可靠的成功证据。
                IntPtr finalBase;
                bool moduleLoaded = TryGetRemoteModule(pid, moduleFileName, out finalBase);

                if (moduleLoaded)
                {
                    Logger.Instance.Info(string.Format(
                        "[Inject] DLL 注入成功, TID={0}, 模块基址=0x{1:X8}, LoadLibraryW 返回=0x{2:X8}",
                        threadId, (long)finalBase, exitCode));
                    return true;
                }

                if (!gotExitCode)
                {
                    Logger.Instance.Error("[Inject] 读取远程线程退出码失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (exitCode == 0)
                {
                    Logger.Instance.Error(string.Format(
                        "[Inject] LoadLibraryW 返回 0 且模块未加载 ⇒ 注入失败 (PID={0}, DLL={1})。常见原因: 目标进程位数不匹配 / 路径不可访问 / 被安全软件拦截",
                        pid, dllPath));
                }
                else if (exitCode == STILL_ACTIVE)
                {
                    Logger.Instance.Error(string.Format(
                        "[Inject] 远程线程仍在活动且模块未加载 ⇒ 注入失败: PID={0}", pid));
                }
                else
                {
                    Logger.Instance.Error(string.Format(
                        "[Inject] 远程线程以 0x{0:X8} 结束(疑似线程内异常, 如 0xC0000005=访问冲突) 且模块未加载 ⇒ 注入失败 (PID={1}, DLL={2})",
                        exitCode, pid, dllPath));
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Inject] DLL注入异常", ex);
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// 从目标进程卸载 DLL
        /// </summary>
        public bool UnInjectDll(int pid, string moduleName)
        {
            Logger.Instance.Info($"[Inject] 开始卸载 DLL: PID={pid}, Module={moduleName}");

            IntPtr hProcess = OpenProcess(
                PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                false, pid);

            if (hProcess == IntPtr.Zero)
            {
                Logger.Instance.Error("[Inject] 打开进程失败, 错误码: " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                // 写入模块名
                byte[] moduleNameBytes = Encoding.Unicode.GetBytes(moduleName + "\0");
                IntPtr lpRemoteString = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)moduleNameBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (lpRemoteString == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 分配远程内存失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                UIntPtr bytesWritten;
                if (!WriteProcessMemory(hProcess, lpRemoteString, moduleNameBytes, (uint)moduleNameBytes.Length, out bytesWritten))
                {
                    Logger.Instance.Error("[Inject] 写入模块名失败, 错误码: " + Marshal.GetLastWin32Error());
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                // GetModuleHandleW
                IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                IntPtr lpGetModuleHandle = GetProcAddress(hKernel32, "GetModuleHandleW");
                if (lpGetModuleHandle == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 获取 GetModuleHandleW 地址失败");
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, lpGetModuleHandle, lpRemoteString, 0, out IntPtr threadId);
                if (hThread == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 创建 GetModuleHandleW 远程线程失败, 错误码: " + Marshal.GetLastWin32Error());
                    VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);
                    return false;
                }

                // 必须判等待结果：超时（线程仍在运行）时退出码是 STILL_ACTIVE(259)，
                // 直接把它当模块基址传给 FreeLibrary，目标进程会用非法地址调用 FreeLibrary 而崩溃。
                uint wrModule = WaitForSingleObject(hThread, 5000);
                uint hModuleExit;
                bool gotModule = GetExitCodeThread(hThread, out hModuleExit);
                CloseHandle(hThread);

                if (wrModule != 0 || !gotModule || hModuleExit == 0 || hModuleExit == 259)
                {
                    // 这条路径**不释放**远程缓冲：远程线程可能仍在读它，提前释放会让目标进程 use-after-free
                    Logger.Instance.Error(string.Format(
                        "[Inject] 取模块基址失败（等待结果=0x{0:X}，退出码=0x{1:X}），放弃卸载: {2}", wrModule, hModuleExit, moduleName));
                    return false;
                }

                VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);

                if (!gotModule)
                {
                    Logger.Instance.Error("[Inject] 读取远程线程退出码失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                uint hModule = hModuleExit;
                if (hModule == 0)
                {
                    Logger.Instance.Warn("[Inject] 未找到模块: " + moduleName);
                    return false;
                }

                // FreeLibrary
                IntPtr lpFreeLibrary = GetProcAddress(hKernel32, "FreeLibrary");
                if (lpFreeLibrary == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 获取 FreeLibrary 地址失败");
                    return false;
                }

                hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, lpFreeLibrary, (IntPtr)hModule, 0, out threadId);
                if (hThread == IntPtr.Zero)
                {
                    Logger.Instance.Error("[Inject] 创建 FreeLibrary 远程线程失败, 错误码: " + Marshal.GetLastWin32Error());
                    return false;
                }

                uint wrFree = WaitForSingleObject(hThread, 5000);
                uint freeResult;
                bool gotFreeResult = GetExitCodeThread(hThread, out freeResult);
                CloseHandle(hThread);

                // 同样必须判等待结果：超时得到 STILL_ACTIVE(259)，它 != 0 会恰好绕过下面那条判据，
                // 把"模块仍驻留在目标进程"谎报成"卸载成功"。
                if (wrFree != 0 || !gotFreeResult || freeResult == 0 || freeResult == 259)
                {
                    Logger.Instance.Warn(string.Format(
                        "[Inject] FreeLibrary 返回 {0}, 模块可能仍驻留在目标进程: {1}",
                        gotFreeResult ? freeResult.ToString() : "(读取失败)", moduleName));
                    return false;
                }

                Logger.Instance.Info("[Inject] DLL 卸载成功");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Inject] DLL卸载异常", ex);
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// 向注入的 DLL 发送消息（WM_COPYDATA）
        /// 对应原项目 MsgCenterSendToVirus
        /// </summary>
        public bool SendMessageToVirus(string message, IntPtr fromWnd)
        {
            IntPtr receiveWnd = FindWindow(null, "JiYu Trainer Virus Window");
            if (receiveWnd == IntPtr.Zero)
            {
                Logger.Instance.Warn("[Inject] 未找到病毒窗口: JiYu Trainer Virus Window");
                return false;
            }

            try
            {
                byte[] data = Encoding.Unicode.GetBytes(message + "\0");
                COPYDATASTRUCT cds = new COPYDATASTRUCT();
                cds.dwData = IntPtr.Zero;
                cds.cbData = data.Length;
                cds.lpData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, cds.lpData, data.Length);

                IntPtr result = SendMessageTimeout(receiveWnd, WM_COPYDATA, fromWnd, ref cds, SMTO_ABORTIFHUNG | SMTO_NORMAL, 500, IntPtr.Zero);
                Marshal.FreeHGlobal(cds.lpData);

                return result != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[Inject] 发送消息异常", ex);
                return false;
            }
        }

        private const uint WM_COPYDATA = 0x004A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SMTO_NORMAL = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint fuFlags, uint uTimeout, IntPtr lpdwResult);
    }
}
