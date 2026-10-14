using System;
using System.IO;

namespace DolbyKiller.Services
{
    /// <summary>
    /// 轻量日志类（DolbyKiller 简化版，不写文件，只保留事件供 UI 订阅）
    /// </summary>
    public class Logger
    {
        private static readonly Lazy<Logger> _instance = new Lazy<Logger>(() => new Logger());
        public static Logger Instance => _instance.Value;

        public event Action<string> OnLog;

        public void Info(string msg) => OnLog?.Invoke($"[INFO] {msg}");
        public void Warn(string msg) => OnLog?.Invoke($"[WARN] {msg}");
        public void Error(string msg) => OnLog?.Invoke($"[ERROR] {msg}");
        public void Debug(string msg) => OnLog?.Invoke($"[DEBUG] {msg}");
    }
}
