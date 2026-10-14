using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace DolbyKiller.Services
{
    /// <summary>
    /// 进程检测与查杀工具
    /// 目标：AudioSrv.exe（代理伪装名）、DolbyVision.exe、DolbyAccess.exe
    /// 注意：真正的 Windows Audio 服务运行在 svchost.exe 中（服务名 Audiosrv），
    ///       系统中不存在 AudioSrv.exe 这个进程，见到就是代理伪装。
    /// </summary>
    public static class ProcessHelper
    {
        /// <summary>目标进程名（小写比较）</summary>
        public static readonly string[] TargetProcessNames = { "audiosrv.exe", "dolbyvision.exe", "dolbyaccess.exe" };

        #region NtTerminateProcess 兜底

        [DllImport("ntdll.dll")]
        private static extern int NtTerminateProcess(IntPtr processHandle, int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        #endregion

        /// <summary>
        /// 进程信息
        /// </summary>
        public class ProcessInfo
        {
            public int Pid { get; set; }
            public string Name { get; set; }
            public string ExecutablePath { get; set; }
            public int SessionId { get; set; }
            public string CommandLine { get; set; }
        }

        /// <summary>
        /// 枚举所有目标进程（含 Session 0 服务进程）
        /// 使用 WMI 一次查询，比 Process.GetProcesses + MainModule 快且不会挂起
        /// </summary>
        public static List<ProcessInfo> GetTargetProcesses()
        {
            var result = new List<ProcessInfo>();
            try
            {
                // 构造 WHERE Name IN (...) 查询
                string nameFilter = string.Join(" OR ",
                    Array.ConvertAll(TargetProcessNames, n => $"Name='{n.Replace(".exe", "")}' OR Name='{n}'"));

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, ExecutablePath, SessionId, CommandLine " +
                    $"FROM Win32_Process WHERE {nameFilter}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var info = new ProcessInfo
                        {
                            Pid = Convert.ToInt32(obj["ProcessId"]),
                            Name = obj["Name"]?.ToString() ?? "",
                            ExecutablePath = obj["ExecutablePath"]?.ToString() ?? "",
                            SessionId = Convert.ToInt32(obj["SessionId"] ?? 0),
                            CommandLine = obj["CommandLine"]?.ToString() ?? ""
                        };

                        // 双保险：AudioSrv.exe 必须路径在 TEMP 或命令行含 /service，
                        // 防止极端情况下有同名合法进程
                        if (info.Name.Equals("AudioSrv.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            string path = info.ExecutablePath ?? "";
                            bool isTemp = path.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isService = info.CommandLine.IndexOf("/service", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!isTemp && !isService && !string.IsNullOrEmpty(path))
                            {
                                // 路径不在 TEMP 也不是服务模式，跳过（理论上不会发生）
                                continue;
                            }
                        }
                        result.Add(info);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"WMI 进程枚举失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 终止指定 PID 的进程
        /// 先 Process.Kill()，失败则 OpenProcess + TerminateProcess，再失败则 NtTerminateProcess
        /// </summary>
        public static bool KillProcess(int pid, out string error)
        {
            error = null;
            try
            {
                Process proc = Process.GetProcessById(pid);
                proc.Kill();
                if (!proc.WaitForExit(3000))
                {
                    error = $"进程 {pid} 未在3秒内退出";
                    return false;
                }
                return true;
            }
            catch (ArgumentException)
            {
                error = $"进程 {pid} 不存在";
                return true; // 已经不存在，算成功
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn($"Process.Kill({pid}) 失败: {ex.Message}，尝试 TerminateProcess 兜底");
            }

            // 兜底1：TerminateProcess
            IntPtr handle = OpenProcess(ProcessTerminate, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    if (TerminateProcess(handle, 1))
                    {
                        return true;
                    }
                    error = $"TerminateProcess 失败，错误码 {Marshal.GetLastWin32Error()}";
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            else
            {
                error = $"OpenProcess 失败，错误码 {Marshal.GetLastWin32Error()}";
            }

            // 兜底2：NtTerminateProcess
            handle = OpenProcess(ProcessTerminate, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    int ntStatus = NtTerminateProcess(handle, 1);
                    if (ntStatus == 0)
                    {
                        return true;
                    }
                    error = $"NtTerminateProcess 失败，NTSTATUS=0x{ntStatus:X8}";
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            return false;
        }

        /// <summary>
        /// 批量终止所有目标进程
        /// 返回 (成功数, 失败列表)
        /// </summary>
        public static (int Killed, List<string> Failures) KillAllTargetProcesses()
        {
            int killed = 0;
            var failures = new List<string>();
            var procs = GetTargetProcesses();

            foreach (var p in procs)
            {
                if (KillProcess(p.Pid, out string err))
                {
                    killed++;
                    Logger.Instance.Info($"已终止进程 {p.Name} (PID={p.Pid}, Session={p.SessionId})");
                }
                else
                {
                    failures.Add($"{p.Name} (PID={p.Pid}): {err}");
                    Logger.Instance.Error($"终止进程失败: {p.Name} PID={p.Pid} - {err}");
                }
            }
            return (killed, failures);
        }

        /// <summary>
        /// 检查目标进程是否还存在
        /// </summary>
        public static bool AnyTargetProcessRunning()
        {
            return GetTargetProcesses().Count > 0;
        }
    }
}
