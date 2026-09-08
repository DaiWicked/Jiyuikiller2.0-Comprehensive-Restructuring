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

                    // 验证文件
                    FileInfo fi = new FileInfo(ExePath);
                    Logger.Instance.Info($"[TeacherSim] 文件验证: {ExePath}, 大小: {fi.Length} bytes");
                    OnLogOutput?.Invoke($"[系统] 正在启动 teacher_sim.exe ({fi.Length / 1024 / 1024}MB)...");

                    // 清理桌面旧日志（teacher_sim.py硬编码日志到桌面）
                    string desktopLogPath = GetLogPath();
                    if (File.Exists(desktopLogPath))
                    {
                        try { File.Delete(desktopLogPath); } catch { }
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
                        // teacher_sim.exe 输出是GBK编码（中文Windows控制台默认编码）
                        StandardOutputEncoding = Encoding.GetEncoding("GB2312"),
                        StandardErrorEncoding = Encoding.GetEncoding("GB2312")
                    };

                    // 设置环境变量
                    psi.EnvironmentVariables["TEACHER_CHANNEL"] = Channel.ToString();

                    _process = new Process();
                    _process.StartInfo = psi;
                    _process.EnableRaisingEvents = true;

                    // 接收stdout输出（teacher_sim的print输出到stdout）
                    _process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Logger.Instance.Debug($"[TeacherSim] stdout: {e.Data}");
                            OnLogOutput?.Invoke(e.Data);
                        }
                    };
                    _process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Logger.Instance.Error($"[TeacherSim] stderr: {e.Data}");
                            OnLogOutput?.Invoke($"[错误] {e.Data}");
                        }
                    };

                    _process.Exited += (s, e) =>
                    {
                        _isRunning = false;
                        OnStateChanged?.Invoke(false);
                        OnLogOutput?.Invoke("[系统] teacher_sim 已退出");
                        Logger.Instance.Info($"[TeacherSim] 进程退出，退出码: {_process.ExitCode}");
                    };

                    Logger.Instance.Info($"[TeacherSim] 启动 teacher_sim.exe, 频道: {Channel}, 工作目录: {workDir}");
                    bool started = _process.Start();

                    // 开始异步读取stdout/stderr
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    // 等待2秒检查进程是否还在运行
                    Thread.Sleep(2000);
                    if (_process.HasExited)
                    {
                        Logger.Instance.Error($"[TeacherSim] 进程启动后立即退出，退出码: {_process.ExitCode}");
                        OnLogOutput?.Invoke($"[错误] teacher_sim.exe 启动失败，退出码: {_process.ExitCode}");
                        _isRunning = false;
                        OnStateChanged?.Invoke(false);
                        return false;
                    }

                    _isRunning = true;
                    OnStateChanged?.Invoke(true);
                    OnLogOutput?.Invoke($"[系统] 教师端模拟已启动，PID: {_process.Id}，频道: {Channel}");
                    OnLogOutput?.Invoke("[系统] 等待学生端登录... 输入 help 查看命令");

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
        /// 获取日志文件路径（teacher_sim.py硬编码为用户桌面）
        /// </summary>
        public string GetLogPath()
        {
            // teacher_sim.py中 LOG_DIR = os.path.join(os.path.expanduser('~'), 'Desktop')
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(desktop, "teacher_sim.log");
        }

        /// <summary>
        /// 监控日志文件线程
        /// </summary>
        private void MonitorLogFile()
        {
            string logPath = GetLogPath();
            Logger.Instance.Info($"[TeacherSim] 日志监控线程启动，监控: {logPath}");
            OnLogOutput?.Invoke($"[系统] 日志文件: {logPath}");

            // 等待日志文件创建
            int waitCount = 0;
            while (_isRunning && !File.Exists(logPath) && waitCount < 20)
            {
                Thread.Sleep(500);
                waitCount++;
            }

            if (!File.Exists(logPath))
            {
                Logger.Instance.Warn("[TeacherSim] 日志文件未创建，可能teacher_sim.exe启动失败");
                OnLogOutput?.Invoke("[警告] 日志文件未创建，请检查teacher_sim.exe是否正常启动");
            }

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
    }
}
