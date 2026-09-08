using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 错误报告服务
    /// 程序异常时生成错误报告文件到exe目录
    /// 报告包含: 异常信息、堆栈、模块信息、系统信息
    /// </summary>
    public static class CrashReportService
    {
        private static readonly object _lock = new object();

        /// <summary>
        /// 生成错误报告
        /// </summary>
        /// <param name="ex">异常对象</param>
        /// <param name="moduleName">发生异常的模块名</param>
        /// <param name="functionName">发生异常的功能名</param>
        /// <param name="isFatal">是否是致命错误(导致程序终止)</param>
        /// <returns>报告文件路径</returns>
        public static string GenerateReport(Exception ex, string moduleName = "未知模块", string functionName = "未知功能", bool isFatal = false)
        {
            lock (_lock)
            {
                try
                {
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string reportType = isFatal ? "Crash" : "Error";
                    string reportPath = Path.Combine(exeDir, $"i.chaoxing_{reportType}_{timestamp}.txt");

                    StringBuilder sb = new StringBuilder();

                    sb.AppendLine("========================================");
                    sb.AppendLine($"  学习不通2.0 错误报告");
                    sb.AppendLine($"  报告类型: {(isFatal ? "致命错误(程序终止)" : "功能异常(功能终止)")}");
                    sb.AppendLine($"  报告时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine("========================================");
                    sb.AppendLine();

                    // 异常模块和功能
                    sb.AppendLine("【异常位置】");
                    sb.AppendLine($"  模块: {moduleName}");
                    sb.AppendLine($"  功能: {functionName}");
                    sb.AppendLine();

                    // 异常信息
                    sb.AppendLine("【异常信息】");
                    sb.AppendLine($"  异常类型: {ex.GetType().FullName}");
                    sb.AppendLine($"  异常消息: {ex.Message}");
                    sb.AppendLine($"  HResult: 0x{ex.HResult:X8}");
                    sb.AppendLine();

                    // 堆栈跟踪
                    sb.AppendLine("【堆栈跟踪】");
                    sb.AppendLine(ex.StackTrace ?? "无堆栈信息");
                    sb.AppendLine();

                    // 内部异常
                    if (ex.InnerException != null)
                    {
                        sb.AppendLine("【内部异常】");
                        Exception inner = ex.InnerException;
                        int depth = 1;
                        while (inner != null)
                        {
                            sb.AppendLine($"  [{depth}] {inner.GetType().FullName}: {inner.Message}");
                            sb.AppendLine($"      堆栈: {inner.StackTrace}");
                            inner = inner.InnerException;
                            depth++;
                        }
                        sb.AppendLine();
                    }

                    // 系统信息
                    sb.AppendLine("【系统信息】");
                    sb.AppendLine($"  操作系统: {Environment.OSVersion}");
                    sb.AppendLine($"  .NET版本: {Environment.Version}");
                    sb.AppendLine($"  64位系统: {Environment.Is64BitOperatingSystem}");
                    sb.AppendLine($"  64位进程: {Environment.Is64BitProcess}");
                    sb.AppendLine($"  机器名: {Environment.MachineName}");
                    sb.AppendLine($"  用户名: {Environment.UserName}");
                    sb.AppendLine($"  工作目录: {Environment.CurrentDirectory}");
                    sb.AppendLine();

                    // 进程信息
                    sb.AppendLine("【进程信息】");
                    Process proc = Process.GetCurrentProcess();
                    sb.AppendLine($"  进程ID: {proc.Id}");
                    sb.AppendLine($"  进程名: {proc.ProcessName}");
                    sb.AppendLine($"  内存使用: {proc.WorkingSet64 / 1024 / 1024} MB");
                    sb.AppendLine($"  线程数: {proc.Threads.Count}");
                    sb.AppendLine($"  启动时间: {proc.StartTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"  运行时长: {(DateTime.Now - proc.StartTime).TotalSeconds:F1} 秒");
                    sb.AppendLine();

                    // 加载的程序集
                    sb.AppendLine("【加载的程序集】");
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            sb.AppendLine($"  {asm.GetName().Name} - v{asm.GetName().Version}");
                        }
                        catch { }
                    }
                    sb.AppendLine();

                    sb.AppendLine("========================================");
                    sb.AppendLine("  报告结束");
                    sb.AppendLine("========================================");

                    File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));

                    // 同时记录到日志
                    if (Logger.Instance.Enabled)
                    {
                        Logger.Instance.Error($"错误报告已生成: {reportPath}");
                        Logger.Instance.Error($"异常模块: {moduleName}, 功能: {functionName}, 类型: {ex.GetType().Name}, 消息: {ex.Message}");
                    }

                    return reportPath;
                }
                catch (Exception reportEx)
                {
                    // 报告生成失败时尝试写临时文件
                    try
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), $"i.chaoxing_error_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                        File.WriteAllText(tempPath, $"错误报告生成失败: {reportEx.Message}\n原始异常: {ex.Message}\n{ex.StackTrace}", Encoding.UTF8);
                        return tempPath;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>
        /// 显示错误弹窗
        /// </summary>
        /// <param name="ex">异常对象</param>
        /// <param name="moduleName">模块名</param>
        /// <param name="functionName">功能名</param>
        /// <param name="reportPath">报告文件路径</param>
        /// <param name="isFatal">是否致命错误</param>
        public static void ShowErrorDialog(Exception ex, string moduleName, string functionName, string reportPath, bool isFatal = false)
        {
            string title = isFatal ? "程序致命错误" : "功能异常";
            string message = $"模块: {moduleName}\n功能: {functionName}\n\n异常类型: {ex.GetType().Name}\n异常消息: {ex.Message}\n\n";

            if (!string.IsNullOrEmpty(reportPath))
            {
                message += $"错误报告已保存至:\n{reportPath}";
            }
            else
            {
                message += "错误报告生成失败，请查看日志文件。";
            }

            if (isFatal)
            {
                message += "\n\n程序将退出。";
            }

            System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.OK,
                isFatal ? System.Windows.MessageBoxImage.Stop : System.Windows.MessageBoxImage.Error);
        }
    }
}
