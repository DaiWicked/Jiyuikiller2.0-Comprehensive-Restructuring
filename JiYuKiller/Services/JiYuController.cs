using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 极域控制器 - 参考原项目 TrainerWorker 逻辑
    /// 第一阶段：实现进程监控、窗口控制基础功能
    /// 后续阶段：Hook DLL 注入、驱动加载、网络过滤
    /// </summary>
    public class JiYuController
    {
        private static readonly Lazy<JiYuController> _instance = new Lazy<JiYuController>(() => new JiYuController());
        public static JiYuController Instance => _instance.Value;

        private Thread _monitorThread;
        private volatile bool _isRunning;
        private Models.AppSettings _settings;

        // 极域进程名（常见）
        private static readonly string[] JiYuProcessNames = new[]
        {
            "StudentMain", "StudentMain.exe", "jiyu", "极域电子教室"
        };

        public bool IsRunning => _isRunning;
        public bool IsJiYuRunning { get; private set; }
        public int JiYuProcessId { get; private set; }
        public string JiYuProcessPath { get; private set; }

        // Windows API
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;

        private JiYuController()
        {
            _settings = Models.AppSettings.Load();
            Logger.Instance.Info("JiYuController 初始化完成");
        }

        public void UpdateSettings(Models.AppSettings settings)
        {
            _settings = settings;
            Logger.Instance.Info("JiYuController 设置已更新");
            LogSettings();
        }

        private void LogSettings()
        {
            Logger.Instance.Debug($"  监控进程: {_settings.MonitorJiYuProcess}");
            Logger.Instance.Debug($"  禁止运行: {_settings.BanJiYuRunOp}");
            Logger.Instance.Debug($"  允许置顶: {_settings.AllowGbTop}");
            Logger.Instance.Debug($"  禁止结束进程: {_settings.ProhibitKillProcess}");
            Logger.Instance.Debug($"  允许监视: {_settings.AllowMonitor}");
            Logger.Instance.Debug($"  禁止关闭窗口: {_settings.ProhibitCloseWindow}");
            Logger.Instance.Debug($"  允许控制: {_settings.AllowControl}");
        }

        /// <summary>
        /// 启动监控
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                Logger.Instance.Warn("JiYuController 已在运行，忽略启动请求");
                return;
            }

            _isRunning = true;
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "JiYuMonitorThread"
            };
            _monitorThread.Start();
            Logger.Instance.Info("JiYuController 监控已启动");
        }

        /// <summary>
        /// 停止监控
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _monitorThread?.Join(1000);
            Logger.Instance.Info("JiYuController 监控已停止");
        }

        private void MonitorLoop()
        {
            Logger.Instance.Info("监控线程开始运行");
            int checkInterval = 3000; // 3秒检查一次

            while (_isRunning)
            {
                try
                {
                    CheckJiYuProcess();

                    if (IsJiYuRunning)
                    {
                        ApplyWindowRules();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("监控循环异常", ex);
                }

                Thread.Sleep(checkInterval);
            }

            Logger.Instance.Info("监控线程已退出");
        }

        /// <summary>
        /// 检查极域进程状态
        /// </summary>
        private void CheckJiYuProcess()
        {
            Process jiYuProcess = null;

            foreach (string name in JiYuProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
                if (processes.Length > 0)
                {
                    jiYuProcess = processes[0];
                    break;
                }
            }

            if (jiYuProcess != null)
            {
                if (!IsJiYuRunning)
                {
                    IsJiYuRunning = true;
                    JiYuProcessId = jiYuProcess.Id;
                    try
                    {
                        JiYuProcessPath = jiYuProcess.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                        JiYuProcessPath = "(无法获取路径)";
                    }
                    Logger.Instance.Info($"检测到极域进程运行: PID={JiYuProcessId}, 路径={JiYuProcessPath}");
                }

                // 禁止极域运行进程 - 直接结束
                if (_settings.BanJiYuRunOp)
                {
                    try
                    {
                        jiYuProcess.Kill();
                        Logger.Instance.Warn($"已禁止极域进程运行，结束 PID={JiYuProcessId}");
                        IsJiYuRunning = false;
                        JiYuProcessId = 0;
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error($"结束极域进程失败", ex);
                    }
                }
            }
            else
            {
                if (IsJiYuRunning)
                {
                    IsJiYuRunning = false;
                    Logger.Instance.Info("极域进程已退出");
                }
            }
        }

        /// <summary>
        /// 应用窗口规则（置顶控制）
        /// </summary>
        private void ApplyWindowRules()
        {
            if (!_settings.AllowGbTop)
            {
                // 不允许置顶：移除极域窗口的 TOPMOST 属性
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == JiYuProcessId)
                    {
                        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                        if ((exStyle & WS_EX_TOPMOST) == WS_EX_TOPMOST)
                        {
                            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                            Logger.Instance.Debug($"已移除极域窗口置顶: HWND={hWnd}");
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }

        /// <summary>
        /// 解除网络控制（第一阶段：提示需要驱动支持）
        /// </summary>
        public void UnloadNetFilter()
        {
            Logger.Instance.FunctionCall("UnloadNetFilter");
            Logger.Instance.Warn("解除网络控制功能需要内核驱动支持，当前版本暂未实现");
            MessageBox.Show("解除网络控制功能需要内核驱动支持，将在后续版本中实现。\n\n当前阶段：基础框架搭建", "功能提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 定位极域主进程位置
        /// </summary>
        public string LocateJiYuPosition()
        {
            Logger.Instance.FunctionCall("LocateJiYuPosition");

            // 先检查当前运行的进程
            if (IsJiYuRunning && !string.IsNullOrEmpty(JiYuProcessPath) && JiYuProcessPath != "(无法获取路径)")
            {
                Logger.Instance.Info($"从运行进程定位到极域: {JiYuProcessPath}");
                return JiYuProcessPath;
            }

            // 常见安装路径
            string[] commonPaths = new[]
            {
                @"C:\Program Files\极域电子教室\StudentMain.exe",
                @"C:\Program Files (x86)\极域电子教室\StudentMain.exe",
                @"C:\Program Files\Mythware\极域电子教室\StudentMain.exe",
                @"C:\Program Files (x86)\Mythware\极域电子教室\StudentMain.exe",
            };

            foreach (string path in commonPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    Logger.Instance.Info($"从常见路径定位到极域: {path}");
                    return path;
                }
            }

            Logger.Instance.Warn("未能自动定位极域主进程位置");
            return "";
        }

        /// <summary>
        /// 获取状态信息
        /// </summary>
        public string GetStatusText()
        {
            if (IsJiYuRunning)
            {
                return $"极域正在运行 (PID: {JiYuProcessId})";
            }
            return "极域未运行";
        }
    }
}
