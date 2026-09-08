using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 极域教师端模拟服务
    /// 管理teacher_sim.exe进程，通过stdin发送命令，通过日志文件获取输出
    /// </summary>
    public class TeacherSimService
    {
        private Process _process;
        private Thread _logMonitorThread;
        private bool _isRunning = false;
        private long _lastLogPosition = 0;
        private readonly object _lock = new object();

        /// <summary>日志输出事件</summary>
        public event Action<string> OnLogOutput;

        /// <summary>状态变化事件</summary>
        public event Action<bool> OnStateChanged;

        /// <summary>teacher_sim.exe路径</summary>
        public string ExePath { get; set; }

        /// <summary>工作目录</summary>
        public string WorkDir { get; set; }

        /// <summary>频道号</summary>
        public int Channel { get; set; } = 1;

        /// <summary>是否运行中</summary>
        public bool IsRunning => _isRunning && _process != null && !_process.HasExited;

        /// <summary>
        /// 启动teacher_sim.exe
        /// </summary>
        public bool Start()
        {
            lock (_lock)
            {
                if (IsRunning)
                {
                    Logger.Instance.Warn("[TeacherSim] 已经在运行中");
                    return false;
                }

                try
                {
                    if (string.IsNullOrEmpty(ExePath) || !File.Exists(ExePath))
                    {
                        Logger.Instance.Error($"[TeacherSim] teacher_sim.exe 不存在: {ExePath}");
                        OnLogOutput?.Invoke($"[错误] teacher_sim.exe 不存在: {ExePath}");
                        return false;
                    }

                    string workDir = string.IsNullOrEmpty(WorkDir) ? Path.GetDirectoryName(ExePath) : WorkDir;
                    Directory.CreateDirectory(workDir);

                    // 清理旧日志
                    string logPath = Path.Combine(workDir, "teacher_sim.log");
                    if (File.Exists(logPath))
                    {
                        try { File.Delete(logPath); } catch { }
                    }
                    _lastLogPosition = 0;

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = ExePath,
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    // 设置环境变量
                    psi.EnvironmentVariables["TEACHER_CHANNEL"] = Channel.ToString();

                    _process = new Process();
                    _process.StartInfo = psi;
                    _process.EnableRaisingEvents = true;
                    _process.Exited += (s, e) =>
                    {
                        _isRunning = false;
                        OnStateChanged?.Invoke(false);
                        OnLogOutput?.Invoke("[系统] teacher_sim 已退出");
                        Logger.Instance.Info($"[TeacherSim] 进程退出，退出码: {_process.ExitCode}");
                    };

                    Logger.Instance.Info($"[TeacherSim] 启动 teacher_sim.exe, 频道: {Channel}, 工作目录: {workDir}");
                    _process.Start();

                    _isRunning = true;
                    OnStateChanged?.Invoke(true);
                    OnLogOutput?.Invoke($"[系统] 教师端模拟已启动，频道: {Channel}");

                    // 启动日志监控线程
                    _logMonitorThread = new Thread(MonitorLogFile)
                    {
                        IsBackground = true,
                        Name = "TeacherSimLogMonitor"
                    };
                    _logMonitorThread.Start();

                    Logger.Instance.Info($"[TeacherSim] 启动成功，PID: {_process.Id}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[TeacherSim] 启动失败", ex);
                    OnLogOutput?.Invoke($"[错误] 启动失败: {ex.Message}");
                    _isRunning = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// 停止teacher_sim.exe
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_process == null || _process.HasExited)
                {
                    _isRunning = false;
                    return;
                }

                try
                {
                    Logger.Instance.Info("[TeacherSim] 发送 exit 命令");
                    _process.StandardInput.WriteLine("exit");
                    _process.StandardInput.Flush();

                    // 等待2秒
                    if (!_process.WaitForExit(2000))
                    {
                        Logger.Instance.Warn("[TeacherSim] 进程未响应exit命令，强制结束");
                        _process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[TeacherSim] 停止时出错", ex);
                    try { _process.Kill(); } catch { }
                }

                _isRunning = false;
                OnStateChanged?.Invoke(false);
                OnLogOutput?.Invoke("[系统] 教师端模拟已停止");
            }
        }

        /// <summary>
        /// 发送命令到teacher_sim
        /// </summary>
        public bool SendCommand(string command)
        {
            if (!IsRunning)
            {
                OnLogOutput?.Invoke("[错误] 教师端模拟未启动");
                return false;
            }

            try
            {
                Logger.Instance.Info($"[TeacherSim] 发送命令: {command}");
                OnLogOutput?.Invoke($"teacher> {command}");
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[TeacherSim] 发送命令失败", ex);
                OnLogOutput?.Invoke($"[错误] 发送命令失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 监控日志文件线程
        /// </summary>
        private void MonitorLogFile()
        {
            string logPath = Path.Combine(
                string.IsNullOrEmpty(WorkDir) ? Path.GetDirectoryName(ExePath) : WorkDir,
                "teacher_sim.log");

            Logger.Instance.Info($"[TeacherSim] 日志监控线程启动，监控: {logPath}");

            while (_isRunning)
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (fs.Length > _lastLogPosition)
                            {
                                fs.Seek(_lastLogPosition, SeekOrigin.Begin);
                                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                                {
                                    string content = sr.ReadToEnd();
                                    _lastLogPosition = fs.Position;

                                    if (!string.IsNullOrEmpty(content))
                                    {
                                        // 逐行输出
                                        string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (string line in lines)
                                        {
                                            OnLogOutput?.Invoke(line);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Debug($"[TeacherSim] 日志读取异常: {ex.Message}");
                }

                Thread.Sleep(500);
            }

            Logger.Instance.Info("[TeacherSim] 日志监控线程结束");
        }

        /// <summary>
        /// 获取日志文件路径
        /// </summary>
        public string GetLogPath()
        {
            string workDir = string.IsNullOrEmpty(WorkDir) ? Path.GetDirectoryName(ExePath) : WorkDir;
            return Path.Combine(workDir, "teacher_sim.log");
        }
    }
}
