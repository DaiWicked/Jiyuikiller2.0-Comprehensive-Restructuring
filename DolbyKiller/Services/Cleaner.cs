using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using DolbyKiller.Models;
using Microsoft.Win32;

namespace DolbyKiller.Services
{
    /// <summary>
    /// 清除服务：按顺序停止服务→杀进程→删服务→删文件→清注册表→验证
    /// 每步记日志，通过事件回调进度
    /// </summary>
    public class Cleaner
    {
        /// <summary>清除进度回调（步骤描述）</summary>
        public event Action<string> OnProgress;

        /// <summary>清除完成回调（是否全部成功，残留项数）</summary>
        public event Action<bool, int> OnComplete;

        private const string ServiceName = "DolbyVision";

        /// <summary>
        /// 执行完整清除流程
        /// </summary>
        public void Start()
        {
            int failures = 0;

            try
            {
                // 步骤1：停止服务
                OnProgress?.Invoke("[1/6] 正在停止 DolbyVision 服务...");
                if (!StopService())
                {
                    failures++;
                    OnProgress?.Invoke("  ⚠ 服务停止失败或服务不存在，继续下一步");
                }
                else
                {
                    OnProgress?.Invoke("  ✅ 服务已停止");
                }
                Thread.Sleep(300); // 等服务进程退出

                // 步骤2：杀进程
                OnProgress?.Invoke("[2/6] 正在终止代理进程...");
                var (killed, procFailures) = ProcessHelper.KillAllTargetProcesses();
                if (procFailures.Count > 0)
                {
                    failures += procFailures.Count;
                    foreach (var f in procFailures)
                        OnProgress?.Invoke($"  ⚠ {f}");
                }
                OnProgress?.Invoke($"  ✅ 已终止 {killed} 个进程");
                Thread.Sleep(300);

                // 步骤3：删除服务
                OnProgress?.Invoke("[3/6] 正在删除 DolbyVision 服务...");
                if (!DeleteService())
                {
                    failures++;
                    OnProgress?.Invoke("  ⚠ 服务删除失败或服务不存在");
                }
                else
                {
                    OnProgress?.Invoke("  ✅ 服务已删除");
                }

                // 步骤4：删除伪装文件
                OnProgress?.Invoke("[4/6] 正在删除残留文件...");
                int filesDeleted = DeleteFiles();
                OnProgress?.Invoke($"  ✅ 已删除 {filesDeleted} 个文件");

                // 步骤5：清理注册表
                OnProgress?.Invoke("[5/6] 正在清理注册表启动项...");
                int regCleaned = CleanRegistry();
                OnProgress?.Invoke($"  ✅ 已清理 {regCleaned} 个注册表项");

                // 步骤6：验证
                OnProgress?.Invoke("[6/6] 正在验证清除结果...");
                Thread.Sleep(500);
                int remaining = VerifyClean();
                if (remaining > 0)
                {
                    failures += remaining;
                    OnProgress?.Invoke($"  ⚠ 仍有 {remaining} 项残留，建议重启电脑后再次扫描");
                }
                else
                {
                    OnProgress?.Invoke("  ✅ 验证通过，已全部清除");
                }

                OnComplete?.Invoke(failures == 0, failures);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"清除流程异常: {ex.Message}");
                OnProgress?.Invoke($"  ❌ 清除异常: {ex.Message}");
                OnComplete?.Invoke(false, -1);
            }
        }

        #region 各步骤实现

        /// <summary>
        /// 停止 DolbyVision 服务
        /// </summary>
        private bool StopService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        return true; // 已经停了
                    }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
            }
            catch (InvalidOperationException)
            {
                // 服务不存在
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"停止服务失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除 DolbyVision 服务（WMI Delete）
        /// </summary>
        private bool DeleteService()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Service WHERE Name='{ServiceName}'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        obj.Delete();
                        obj.Dispose();
                        return true;
                    }
                }
                return true; // 不存在也算成功
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"删除服务失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除残留文件
        /// 返回删除成功的文件数
        /// </summary>
        private int DeleteFiles()
        {
            int deleted = 0;
            var paths = new List<string>();

            // %TEMP%\AudioSrv.exe
            string tempAudioSrv = Path.Combine(Path.GetTempPath(), "AudioSrv.exe");
            paths.Add(tempAudioSrv);

            // 服务 binPath 指向的原始 exe（从 WMI 读）
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT PathName FROM Win32_Service WHERE Name='{ServiceName}'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string pathName = obj["PathName"]?.ToString() ?? "";
                        // binPath 格式: "C:\path\DolbyVision.exe" /service
                        // 提取引号内的路径
                        int firstQuote = pathName.IndexOf('"');
                        int secondQuote = pathName.IndexOf('"', firstQuote + 1);
                        if (firstQuote >= 0 && secondQuote > firstQuote)
                        {
                            string exePath = pathName.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                            paths.Add(exePath);
                        }
                        else if (!string.IsNullOrEmpty(pathName))
                        {
                            paths.Add(pathName.Split(' ')[0]);
                        }
                    }
                }
            }
            catch { }

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                // 重试3次（等进程释放文件句柄）
                bool ok = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Delete(path);
                        ok = true;
                        Logger.Instance.Info($"已删除文件 {path}");
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(500);
                    }
                }
                if (ok) deleted++;
                else Logger.Instance.Warn($"文件删除失败（可能被占用）: {path}");
            }

            return deleted;
        }

        /// <summary>
        /// 清理注册表 Run 键中的相关启动项
        /// 返回清理的项数
        /// </summary>
        private int CleanRegistry()
        {
            int cleaned = 0;
            string[] runKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
            };
            string[] keywords = { "dolbyvision", "dolbyaccess", "audiosrv" };

            foreach (string keyPath in runKeys)
            {
                cleaned += CleanRegistryKey(Registry.LocalMachine, keyPath, keywords);
                cleaned += CleanRegistryKey(Registry.CurrentUser, keyPath, keywords);
            }
            return cleaned;
        }

        private static int CleanRegistryKey(RegistryKey root, string keyPath, string[] keywords)
        {
            int cleaned = 0;
            try
            {
                using (var key = root.OpenSubKey(keyPath, writable: true))
                {
                    if (key == null) return 0;

                    foreach (string valueName in key.GetValueNames())
                    {
                        string valueData = key.GetValue(valueName)?.ToString() ?? "";
                        string combined = (valueName + " " + valueData).ToLowerInvariant();

                        foreach (string kw in keywords)
                        {
                            if (combined.Contains(kw))
                            {
                                key.DeleteValue(valueName, throwOnMissingValue: false);
                                cleaned++;
                                Logger.Instance.Info($"已删除注册表项 {keyPath}\\{valueName}");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"注册表清理失败 ({keyPath}): {ex.Message}");
            }
            return cleaned;
        }

        /// <summary>
        /// 验证清除结果：重新扫描，返回残留项数
        /// </summary>
        private int VerifyClean()
        {
            int remaining = 0;

            // 服务
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped) remaining++;
                }
            }
            catch (InvalidOperationException) { /* 服务不存在，正常 */ }
            catch { }

            // 进程
            if (ProcessHelper.AnyTargetProcessRunning())
            {
                remaining += ProcessHelper.GetTargetProcesses().Count;
            }

            // 端口
            remaining += PortHelper.GetDolbyPorts().Count;

            // 文件
            string tempAudioSrv = Path.Combine(Path.GetTempPath(), "AudioSrv.exe");
            if (File.Exists(tempAudioSrv)) remaining++;

            return remaining;
        }

        #endregion
    }
}
