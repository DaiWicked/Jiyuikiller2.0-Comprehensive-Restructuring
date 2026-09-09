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

                // 等待线程结束
                WaitForSingleObject(hThread, 5000);
                CloseHandle(hThread);
                VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);

                Logger.Instance.Info("[Inject] DLL 注入成功, TID=" + threadId);
                return true;
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
                UIntPtr bytesWritten;
                WriteProcessMemory(hProcess, lpRemoteString, moduleNameBytes, (uint)moduleNameBytes.Length, out bytesWritten);

                // GetModuleHandleW
                IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                IntPtr lpGetModuleHandle = GetProcAddress(hKernel32, "GetModuleHandleW");
                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, lpGetModuleHandle, lpRemoteString, 0, out IntPtr threadId);
                WaitForSingleObject(hThread, 5000);
                GetExitCodeThread(hThread, out uint hModule);
                CloseHandle(hThread);
                VirtualFreeEx(hProcess, lpRemoteString, 0, MEM_RELEASE);

                if (hModule == 0)
                {
                    Logger.Instance.Warn("[Inject] 未找到模块: " + moduleName);
                    return false;
                }

                // FreeLibrary
                IntPtr lpFreeLibrary = GetProcAddress(hKernel32, "FreeLibrary");
                hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, lpFreeLibrary, (IntPtr)hModule, 0, out threadId);
                WaitForSingleObject(hThread, 5000);
                CloseHandle(hThread);

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
