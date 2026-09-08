using System;
using System.IO;
using System.Text;
using System.Threading;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 统一日志服务 - 单例模式，线程安全
    /// 日志文件：exe目录\i.chaoxing.log
    /// </summary>
    public class Logger
    {
        private static readonly Lazy<Logger> _instance = new Lazy<Logger>(() => new Logger());
        public static Logger Instance => _instance.Value;

        private readonly object _lock = new object();
        private readonly string _logPath;
        private StreamWriter _writer;

        public string LogPath => _logPath;

        private Logger()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _logPath = Path.Combine(exeDir, "i.chaoxing.log");

            try
            {
                // 追加模式，UTF-8 编码
                _writer = new StreamWriter(_logPath, true, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                WriteRaw("========================================");
                WriteRaw($"日志系统初始化，日志文件: {_logPath}");
                WriteRaw($"应用程序域: {AppDomain.CurrentDomain.FriendlyName}");
                WriteRaw($"工作目录: {Environment.CurrentDirectory}");
                WriteRaw($"用户名: {Environment.UserName}");
                WriteRaw($"机器名: {Environment.MachineName}");
                WriteRaw("========================================");
            }
            catch (Exception ex)
            {
                // 日志初始化失败时，尝试写入临时目录
                string fallback = Path.Combine(Path.GetTempPath(), "i.chaoxing.log");
                _logPath = fallback;
                _writer = new StreamWriter(fallback, true, new UTF8Encoding(false)) { AutoFlush = true };
                Error($"日志初始化失败，使用备用路径: {fallback}, 错误: {ex.Message}");
            }
        }

        public void Debug(string message, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Write("DEBUG", message, member, file, line);
        }

        public void Info(string message, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Write("INFO", message, member, file, line);
        }

        public void Warn(string message, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Write("WARN", message, member, file, line);
        }

        public void Error(string message, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Write("ERROR", message, member, file, line);
        }

        public void Error(string message, Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Write("ERROR", $"{message}\n异常: {ex.GetType().Name}: {ex.Message}\n堆栈: {ex.StackTrace}", member, file, line);
        }

        /// <summary>
        /// 记录按钮点击操作
        /// </summary>
        public void ButtonClick(string buttonName, string buttonId = "")
        {
            string detail = string.IsNullOrEmpty(buttonId) ? buttonName : $"{buttonName} (ID: {buttonId})";
            Write("BUTTON", $"用户点击: {detail}", "ButtonClick", "", 0);
        }

        /// <summary>
        /// 记录复选框状态变化
        /// </summary>
        public void CheckboxChanged(string checkboxName, bool newValue, string checkboxId = "")
        {
            string detail = string.IsNullOrEmpty(checkboxId) ? checkboxName : $"{checkboxName} (ID: {checkboxId})";
            Write("UI", $"复选框变化: {detail} = {newValue}", "CheckboxChanged", "", 0);
        }

        /// <summary>
        /// 记录窗口事件
        /// </summary>
        public void WindowEvent(string windowName, string eventName)
        {
            Write("WINDOW", $"窗口事件: {windowName} - {eventName}", "WindowEvent", "", 0);
        }

        /// <summary>
        /// 记录功能操作
        /// </summary>
        public void FunctionCall(string functionName, string parameters = "")
        {
            string detail = string.IsNullOrEmpty(parameters) ? functionName : $"{functionName}({parameters})";
            Write("FUNC", $"功能调用: {detail}", "FunctionCall", "", 0);
        }

        private void Write(string level, string message, string member, string file, int line)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string threadId = Thread.CurrentThread.ManagedThreadId.ToString();
            string fileName = string.IsNullOrEmpty(file) ? "" : Path.GetFileName(file);
            string location = string.IsNullOrEmpty(fileName) ? member : $"{fileName}:{line} {member}";

            string logLine = $"[{timestamp}] [{level,-6}] [TID:{threadId,-3}] [{location}] {message}";
            WriteRaw(logLine);
        }

        private void WriteRaw(string message)
        {
            lock (_lock)
            {
                try
                {
                    _writer?.WriteLine(message);
                }
                catch
                {
                    // 日志写入失败时静默处理，避免影响主程序
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _writer?.Close();
                _writer = null;
            }
        }
    }
}
