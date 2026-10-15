using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using DolbyKiller.Models;
using Microsoft.Win32;

namespace DolbyKiller.Services
{
    /// <summary>
    /// 扫描服务：整合服务/进程/端口/文件/注册表检测，汇总为 ScanReport
    /// 异步执行，不阻塞 UI
    /// </summary>
    public class Scanner
    {
        /// <summary>扫描进度回调（当前步骤描述）</summary>
        public event Action<string> OnProgress;

        /// <summary>扫描完成回调</summary>
        public event Action<ScanReport> OnComplete;

        private CancellationTokenSource _cts;

        /// <summary>
        /// 异步执行完整扫描
        /// </summary>
        public void StartAsync()
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                var report = new ScanReport();
                try
                {
                    // 1. 服务检测
                    OnProgress?.Invoke("正在检测 Windows 服务...");
                    ScanService(report);
                    token.ThrowIfCancellationRequested();

                    // 2. 进程检测
                    OnProgress?.Invoke("正在检测运行中进程...");
                    ScanProcesses(report);
                    token.ThrowIfCancellationRequested();

                    // 3. 端口检测
                    OnProgress?.Invoke("正在检测端口占用...");
                    ScanPorts(report);
                    token.ThrowIfCancellationRequested();

                    // 4. 文件残留检测
                    OnProgress?.Invoke("正在检测文件残留...");
                    ScanFiles(report);
                    token.ThrowIfCancellationRequested();

                    // 5. 注册表启动项检测
                    OnProgress?.Invoke("正在检测注册表启动项...");
                    ScanRegistry(report);
                }
                catch (OperationCanceledException)
                {
                    Logger.Instance.Info("扫描已取消");
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error($"扫描异常: {ex.Message}");
                }

                OnProgress?.Invoke($"扫描完成，发现 {report.TotalCount} 项");
                OnComplete?.Invoke(report);
            }, token);
        }

        /// <summary>
        /// 停止扫描
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #region 各项检测

        /// <summary>
        /// 检测 DolbyVision 服务
        /// </summary>
        private void ScanService(ScanReport report)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName, State, StartMode, PathName, ProcessId " +
                    "FROM Win32_Service WHERE Name='DolbyVision'"))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(5);
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string state = obj["State"]?.ToString() ?? "未知";
                        string startMode = obj["StartMode"]?.ToString() ?? "未知";
                        string pathName = obj["PathName"]?.ToString() ?? "";
                        uint pid = 0;
                        uint.TryParse(obj["ProcessId"]?.ToString(), out pid);

                        bool running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);

                        report.Add(new ThreatItem
                        {
                            Type = ThreatType.Service,
                            Severity = running ? Severity.Danger : Severity.Warning,
                            Name = "DolbyVision 服务",
                            Detail = $"状态: {state} | 启动类型: {startMode}" + (pid > 0 ? $" | PID={pid}" : ""),
                            Path = pathName,
                            Pid = pid > 0 ? (int?)pid : null
                        });
                        Logger.Instance.Info($"发现服务 DolbyVision: state={state}, startMode={startMode}, path={pathName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"服务检测失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测目标进程
        /// </summary>
        private void ScanProcesses(ScanReport report)
        {
            try
            {
                var procs = ProcessHelper.GetTargetProcesses();
                foreach (var p in procs)
                {
                    report.Add(new ThreatItem
                    {
                        Type = ThreatType.Process,
                        Severity = Severity.Danger,
                        Name = p.Name,
                        Detail = $"PID={p.Pid} | 会话={p.SessionId}" + (string.IsNullOrEmpty(p.ExecutablePath) ? "" : $" | {p.ExecutablePath}"),
                        Path = p.ExecutablePath,
                        Pid = p.Pid
                    });
                    Logger.Instance.Info($"发现进程 {p.Name} PID={p.Pid} Session={p.SessionId} Path={p.ExecutablePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"进程检测失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测固定端口占用
        /// </summary>
        private void ScanPorts(ScanReport report)
        {
            try
            {
                var ports = PortHelper.GetDolbyPorts();
                foreach (var (port, pid, proto) in ports)
                {
                    report.Add(new ThreatItem
                    {
                        Type = ThreatType.Port,
                        Severity = Severity.Warning,
                        Name = $"{port}/{proto}",
                        Detail = $"被 PID={pid} 占用",
                        Pid = pid
                    });
                    Logger.Instance.Info($"发现端口占用 {port}/{proto} PID={pid}");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"端口检测失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测文件残留
        /// </summary>
        private void ScanFiles(ScanReport report)
        {
            try
            {
                // %TEMP%\AudioSrv.exe（代理自复制落点）
                string tempAudioSrv = Path.Combine(Path.GetTempPath(), "AudioSrv.exe");
                if (File.Exists(tempAudioSrv))
                {
                    var fi = new FileInfo(tempAudioSrv);
                    report.Add(new ThreatItem
                    {
                        Type = ThreatType.File,
                        Severity = Severity.Info,
                        Name = "AudioSrv.exe（伪装文件）",
                        Detail = $"大小: {fi.Length / 1024}KB | 修改时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm}",
                        Path = tempAudioSrv
                    });
                    Logger.Instance.Info($"发现残留文件 {tempAudioSrv}");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"文件检测失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测注册表 Run 启动项
        /// </summary>
        private void ScanRegistry(ScanReport report)
        {
            string[] runKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
            };

            string[] keywords = { "dolbyvision", "dolbyaccess", "audiosrv" };

            foreach (string keyPath in runKeys)
            {
                CheckRegistryKey(report, Registry.LocalMachine, keyPath, "HKLM", keywords);
                CheckRegistryKey(report, Registry.CurrentUser, keyPath, "HKCU", keywords);
            }
        }

        private static void CheckRegistryKey(ScanReport report, RegistryKey root, string keyPath, string rootName, string[] keywords)
        {
            try
            {
                using (var key = root.OpenSubKey(keyPath))
                {
                    if (key == null) return;

                    foreach (string valueName in key.GetValueNames())
                    {
                        string valueData = key.GetValue(valueName)?.ToString() ?? "";
                        string combined = (valueName + " " + valueData).ToLowerInvariant();

                        foreach (string kw in keywords)
                        {
                            if (combined.Contains(kw))
                            {
                                report.Add(new ThreatItem
                                {
                                    Type = ThreatType.Registry,
                                    Severity = Severity.Warning,
                                    Name = $"{rootName}\\{keyPath}",
                                    Detail = $"{valueName} = {valueData}",
                                    Path = $"{rootName}\\{keyPath}!{valueName}"
                                });
                                Logger.Instance.Info($"发现注册表启动项 {rootName}\\{keyPath}: {valueName}={valueData}");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"注册表检测失败 ({rootName}\\{keyPath}): {ex.Message}");
            }
        }

        #endregion
    }
}
