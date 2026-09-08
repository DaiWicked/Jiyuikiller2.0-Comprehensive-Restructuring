using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 极域控制器 - 完整移植原项目 TrainerWorker 逻辑
    /// 整合: 进程监控 + DLL注入 + 窗口处理 + 驱动管理
    /// </summary>
    public class JiYuController
    {
        private static readonly Lazy<JiYuController> _instance = new Lazy<JiYuController>(() => new JiYuController());
        public static JiYuController Instance => _instance.Value;

        private Thread _monitorThread;
        private volatile bool _isRunning;
        private Models.AppSettings _settings;

        // 服务
        private readonly DllInjectService _dllInject = new DllInjectService();
        private readonly DriverService _driver = new DriverService();

        // 状态
        private bool _virusInstalled = false;
        private bool _studentControlled = false;
        private IntPtr _currentBroadcastWnd = IntPtr.Zero;
        private IntPtr _currentBlackScreenWnd = IntPtr.Zero;
        private int _screenWidth, _screenHeight;

        // 极域进程名
        private static readonly string[] JiYuProcessNames = new[] { "StudentMain" };

        public bool IsRunning => _isRunning;
        public bool IsJiYuRunning { get; private set; }
        public int JiYuProcessId { get; private set; }
        public string JiYuProcessPath { get; private set; }
        public bool IsVirusInstalled => _virusInstalled;
        public bool IsDriverLoaded => _driver.IsDriverLoaded;
        public bool IsControlled => _studentControlled;

        // 事件
        public event Action OnStatusChanged;

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const int GWL_STYLE = -16;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_BORDER = 0x00800000;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint WM_SIZE = 0x0005;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private JiYuController()
        {
            _settings = Models.AppSettings.Load();
            _screenWidth = GetSystemMetrics(SM_CXSCREEN);
            _screenHeight = GetSystemMetrics(SM_CYSCREEN);
            Logger.Instance.Info("[JiYuController] 初始化完成, 屏幕尺寸: " + _screenWidth + "x" + _screenHeight);
        }

        public void UpdateSettings(Models.AppSettings settings)
        {
            _settings = settings;
            Logger.Instance.Info("[JiYuController] 设置已更新");
            LogSettings();

            // 更新监控间隔（MonitorLoop中读取_settings.CKInterval，无需额外操作）
            Logger.Instance.Debug($"[JiYuController] 监控间隔将在下一轮生效: {_settings.CKInterval}ms");

            // 驱动设置变更
            if (_settings.DisableDriver && IsDriverLoaded)
            {
                Logger.Instance.Info("[JiYuController] 设置要求禁用驱动，正在卸载...");
                UnloadDriver();
            }

            // 自我保护
            if (_settings.SelfProtect && IsDriverLoaded)
            {
                Logger.Instance.Info("[JiYuController] 启用驱动层自我保护");
                _driver.InstallSelfProtect();
            }

            // 如果已注入DLL，发送设置更新
            if (_virusInstalled && _studentControlled)
            {
                Logger.Instance.Info("[JiYuController] 发送设置更新到DLL");
                SendVirusMessage("hk:reset");
                SendSettingsToVirus();
            }
        }

        private void LogSettings()
        {
            Logger.Instance.Debug("[JiYuController] 监控进程: " + _settings.MonitorJiYuProcess);
            Logger.Instance.Debug("[JiYuController] 禁止运行: " + _settings.BanJiYuRunOp);
            Logger.Instance.Debug("[JiYuController] 允许置顶: " + _settings.AllowGbTop);
            Logger.Instance.Debug("[JiYuController] 禁止结束进程: " + _settings.ProhibitKillProcess);
            Logger.Instance.Debug("[JiYuController] 允许监视: " + _settings.AllowMonitor);
            Logger.Instance.Debug("[JiYuController] 禁止关闭窗口: " + _settings.ProhibitCloseWindow);
            Logger.Instance.Debug("[JiYuController] 允许控制: " + _settings.AllowControl);
            Logger.Instance.Debug("[JiYuController] 检查间隔: " + _settings.CKInterval + "ms");
            Logger.Instance.Debug("[JiYuController] 结束进程模式: " + _settings.KillProcessMode);
            Logger.Instance.Debug("[JiYuController] 禁用驱动: " + _settings.DisableDriver);
            Logger.Instance.Debug("[JiYuController] 自我保护: " + _settings.SelfProtect);
            Logger.Instance.Debug("[JiYuController] 注入模式: " + _settings.InjectMode);
        }

        /// <summary>
        /// 启动监控
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                Logger.Instance.Warn("[JiYuController] 已在运行，忽略启动请求");
                return;
            }

            _isRunning = true;
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "JiYuMonitorThread"
            };
            _monitorThread.Start();
            Logger.Instance.Info("[JiYuController] 监控已启动");
            OnStatusChanged?.Invoke();
        }

        /// <summary>
        /// 停止监控
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _monitorThread?.Join(2000);

            // 卸载注入的DLL
            if (_virusInstalled && JiYuProcessId > 0)
            {
                try
                {
                    SendVirusMessage("hk:ckend");
                    Thread.Sleep(500);
                    _dllInject.UnInjectDll(JiYuProcessId, "JiYuTrainerHooks.dll");
                    Logger.Instance.Info("[JiYuController] 已卸载注入的DLL");
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[JiYuController] 卸载DLL异常", ex);
                }
                _virusInstalled = false;
                _studentControlled = false;
            }

            Logger.Instance.Info("[JiYuController] 监控已停止");
            OnStatusChanged?.Invoke();
        }

        private void MonitorLoop()
        {
            Logger.Instance.Info("[JiYuController] 监控线程开始运行");
            int checkInterval = Math.Max(1000, Math.Min(10000, _settings.CKInterval > 0 ? _settings.CKInterval : 3000));

            while (_isRunning)
            {
                try
                {
                    CheckJiYuProcess();

                    if (IsJiYuRunning && !_settings.BanJiYuRunOp)
                    {
                        // 确保DLL已注入
                        if (!_virusInstalled)
                        {
                            InstallVirus();
                        }

                        // 处理窗口
                        ResolveWindows();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[JiYuController] 监控循环异常", ex);
                }

                Thread.Sleep(checkInterval);
            }

            Logger.Instance.Info("[JiYuController] 监控线程已退出");
        }

        /// <summary>
        /// 检查极域进程
        /// </summary>
        private void CheckJiYuProcess()
        {
            Process jiYuProcess = null;

            foreach (string name in JiYuProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    jiYuProcess = processes[0];
                    break;
                }
            }

            if (jiYuProcess != null)
            {
                if (!IsJiYuRunning || JiYuProcessId != jiYuProcess.Id)
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
                    _virusInstalled = false;
                    _studentControlled = false;
                    Logger.Instance.Info("[JiYuController] 检测到极域进程: PID=" + JiYuProcessId + ", 路径=" + JiYuProcessPath);
                    OnStatusChanged?.Invoke();
                }

                // 禁止极域运行
                if (_settings.BanJiYuRunOp)
                {
                    try
                    {
                        jiYuProcess.Kill();
                        Logger.Instance.Warn("[JiYuController] 已禁止极域进程运行, PID=" + JiYuProcessId);
                        IsJiYuRunning = false;
                        JiYuProcessId = 0;
                        _virusInstalled = false;
                        _studentControlled = false;
                        OnStatusChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error("[JiYuController] 结束极域进程失败", ex);
                    }
                }
            }
            else
            {
                if (IsJiYuRunning)
                {
                    IsJiYuRunning = false;
                    JiYuProcessId = 0;
                    _virusInstalled = false;
                    _studentControlled = false;
                    Logger.Instance.Info("[JiYuController] 极域进程已退出");
                    OnStatusChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 注入DLL（病毒）
        /// </summary>
        private bool InstallVirus()
        {
            if (JiYuProcessId <= 0) return false;

            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "JiYuTrainerHooks.dll");

            if (!File.Exists(dllPath))
            {
                Logger.Instance.Error("[JiYuController] DLL文件不存在: " + dllPath);
                return false;
            }

            Logger.Instance.Info("[JiYuController] 开始注入DLL到 PID=" + JiYuProcessId);
            bool result = _dllInject.InjectDll(JiYuProcessId, dllPath);

            if (result)
            {
                _virusInstalled = true;
                _studentControlled = true;
                Logger.Instance.Info("[JiYuController] DLL注入成功，已控制极域");

                // 等待DLL初始化
                Thread.Sleep(1000);

                // 发送初始化消息
                SendVirusMessage("hk:path:" + AppDomain.CurrentDomain.BaseDirectory);
                SendVirusMessage("hs:" + Process.GetCurrentProcess().MainWindowHandle.ToInt64());

                // 发送设置
                SendSettingsToVirus();
                OnStatusChanged?.Invoke();
            }
            else
            {
                Logger.Instance.Error("[JiYuController] DLL注入失败");
            }

            return result;
        }

        /// <summary>
        /// 发送设置到注入的DLL
        /// </summary>
        private void SendSettingsToVirus()
        {
            // 对应原项目 hk: 开头的命令
            SendVirusMessage("hk:allowgbtop:" + (_settings.AllowGbTop ? "1" : "0"));
            SendVirusMessage("hk:prohibitkill:" + (_settings.ProhibitKillProcess ? "1" : "0"));
            SendVirusMessage("hk:allowmonitor:" + (_settings.AllowMonitor ? "1" : "0"));
            SendVirusMessage("hk:prohibitclose:" + (_settings.ProhibitCloseWindow ? "1" : "0"));
            SendVirusMessage("hk:allowcontrol:" + (_settings.AllowControl ? "1" : "0"));
            Logger.Instance.Info("[JiYuController] 已发送设置到DLL");
        }

        /// <summary>
        /// 发送消息到注入的DLL
        /// </summary>
        public void SendVirusMessage(string message)
        {
            if (!_virusInstalled) return;
            _dllInject.SendMessageToVirus(message, Process.GetCurrentProcess().MainWindowHandle);
        }

        /// <summary>
        /// 窗口处理（广播窗口化、黑屏窗口处理）
        /// </summary>
        private void ResolveWindows()
        {
            _currentBroadcastWnd = IntPtr.Zero;
            _currentBlackScreenWnd = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != JiYuProcessId) return true;
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, 256);
                string windowTitle = title.ToString();

                // 检测广播窗口
                if (windowTitle.Contains("屏幕广播") || windowTitle.Contains("广播") || windowTitle.Contains("Screen"))
                {
                    _currentBroadcastWnd = hWnd;
                    FixBroadcastWindow(hWnd);
                }

                // 检测黑屏窗口
                if (windowTitle.Contains("黑屏") || windowTitle.Contains("安静") || windowTitle.Contains("Black"))
                {
                    _currentBlackScreenWnd = hWnd;
                    FixBlackScreenWindow(hWnd);
                }

                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// 修复广播窗口（窗口化）
        /// </summary>
        private void FixBroadcastWindow(IntPtr hWnd)
        {
            if (_settings.AllowGbTop)
            {
                // 允许置顶
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                return;
            }

            // 不允许置顶：移除TOPMOST，窗口化
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOPMOST) == WS_EX_TOPMOST)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, exStyle & ~(int)WS_EX_TOPMOST);
            }

            // 移除全屏样式
            int style = GetWindowLong(hWnd, GWL_STYLE);
            if ((style & WS_BORDER) == 0 || (style & WS_OVERLAPPEDWINDOW) == 0)
            {
                SetWindowLong(hWnd, GWL_STYLE, style | (int)WS_BORDER | (int)WS_OVERLAPPEDWINDOW);
            }

            // 调整为窗口大小（屏幕的 3/4 x 4/5）
            int w = (int)(_screenWidth * 0.75);
            int h = (int)(_screenHeight * 0.8);
            int x = (_screenWidth - w) / 2;
            int y = (_screenHeight - h) / 2;
            SetWindowPos(hWnd, HWND_NOTOPMOST, x, y, w, h, SWP_SHOWWINDOW);
            SendMessage(hWnd, WM_SIZE, IntPtr.Zero, (IntPtr)((h << 16) | w));

            Logger.Instance.Debug("[JiYuController] 广播窗口已窗口化: HWND=" + hWnd);
        }

        /// <summary>
        /// 修复黑屏窗口（关闭黑屏）
        /// </summary>
        private void FixBlackScreenWindow(IntPtr hWnd)
        {
            // 黑屏安静模式：移除TOPMOST并隐藏
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOPMOST) == WS_EX_TOPMOST)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, exStyle & ~(int)WS_EX_TOPMOST);
            }
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            Logger.Instance.Debug("[JiYuController] 黑屏窗口已处理: HWND=" + hWnd);
        }

        /// <summary>
        /// 加载驱动
        /// </summary>
        public bool LoadDriver()
        {
            Logger.Instance.FunctionCall("LoadDriver");

            string driverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "JiYuTrainerDriver.sys");

            if (!File.Exists(driverPath))
            {
                Logger.Instance.Error("[JiYuController] 驱动文件不存在: " + driverPath);
                MessageBox.Show("驱动文件不存在:\n" + driverPath, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_driver.LoadDriver(driverPath))
            {
                if (_driver.OpenDriver())
                {
                    // 发送初始化参数
                    bool isXp = Environment.OSVersion.Version.Major < 6;
                    bool isWin7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
                    uint buildVer = (uint)Environment.OSVersion.Version.Build;
                    _driver.SendInitParam(isXp, isWin7, buildVer);

                    Logger.Instance.Info("[JiYuController] 驱动加载成功");
                    MessageBox.Show("内核驱动加载成功！\n\n现在可以使用内核级功能。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    OnStatusChanged?.Invoke();
                    return true;
                }
            }

            Logger.Instance.Error("[JiYuController] 驱动加载失败");
            MessageBox.Show("驱动加载失败，请以管理员身份运行程序。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        /// <summary>
        /// 卸载驱动
        /// </summary>
        public void UnloadDriver()
        {
            Logger.Instance.FunctionCall("UnloadDriver");
            _driver.UnloadDriver();
            Logger.Instance.Info("[JiYuController] 驱动已卸载");
            OnStatusChanged?.Invoke();
        }

        /// <summary>
        /// 内核级杀进程
        /// </summary>
        public bool KernelKillProcess(int pid)
        {
            if (!_driver.IsDriverOpened)
            {
                Logger.Instance.Warn("[JiYuController] 驱动未打开，无法内核级杀进程");
                MessageBox.Show("请先加载内核驱动。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return _driver.KillProcess(pid);
        }

        /// <summary>
        /// 杀死极域（根据 KillProcessMode 设置选择方式）
        /// </summary>
        public bool KillJiYu(bool useDriver = false)
        {
            Logger.Instance.FunctionCall("KillJiYu");

            if (JiYuProcessId <= 0)
            {
                Logger.Instance.Warn("[JiYuController] 极域未运行");
                return false;
            }

            // 优先使用驱动模式
            string mode = _settings.KillProcessMode;
            Logger.Instance.Info("[JiYuController] 杀进程模式: " + mode);

            // KernelMode: 内核驱动杀进程
            if (useDriver || mode == "KernelMode")
            {
                if (_driver.IsDriverOpened)
                {
                    bool result = _driver.KillProcess(JiYuProcessId);
                    if (result)
                    {
                        Logger.Instance.Info("[JiYuController] 内核级杀死极域成功 PID=" + JiYuProcessId);
                        IsJiYuRunning = false;
                        JiYuProcessId = 0;
                        _virusInstalled = false;
                        _studentControlled = false;
                        OnStatusChanged?.Invoke();
                    }
                    return result;
                }
                Logger.Instance.Warn("[JiYuController] 驱动未打开，回退到用户态杀进程");
            }

            // TerminateProcess / NtTerminateProcess: 用户态杀进程
            try
            {
                Process proc = Process.GetProcessById(JiYuProcessId);
                proc.Kill();
                Logger.Instance.Info("[JiYuController] 已杀死极域进程 PID=" + JiYuProcessId + " (模式: " + mode + ")");
                IsJiYuRunning = false;
                JiYuProcessId = 0;
                _virusInstalled = false;
                _studentControlled = false;
                OnStatusChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] 杀死极域失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 重启极域
        /// </summary>
        public bool RestartJiYu()
        {
            Logger.Instance.FunctionCall("RestartJiYu");

            string path = LocateJiYuPosition();
            if (string.IsNullOrEmpty(path))
            {
                Logger.Instance.Warn("[JiYuController] 未找到极域路径");
                MessageBox.Show("未找到极域电子教室，请先在设置中指定路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            KillJiYu();
            Thread.Sleep(1000);

            try
            {
                Process.Start(path);
                Logger.Instance.Info("[JiYuController] 已重启极域: " + path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] 重启极域失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 解除网络控制（卸载极域网络过滤驱动）
        /// </summary>
        public void UnloadNetFilter()
        {
            Logger.Instance.FunctionCall("UnloadNetFilter");

            try
            {
                // 对应原项目 MUnLoadDriverServiceWithMessage(L"TDNetFilter")
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "stop TDNetFilter")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                Process p = Process.Start(psi);
                p.WaitForExit(5000);

                psi = new ProcessStartInfo("sc.exe", "delete TDNetFilter")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                p = Process.Start(psi);
                p.WaitForExit(5000);

                Logger.Instance.Info("[JiYuController] 已尝试卸载极域网络过滤驱动 TDNetFilter");
                MessageBox.Show("已尝试解除网络控制（卸载 TDNetFilter 驱动）。\n\n如果极域仍限制网络，请重启电脑。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] 卸载网络过滤驱动失败", ex);
                MessageBox.Show("操作失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                return JiYuProcessPath;
            }

            // 注册表查找
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\e-Learning Class V6.0"))
                {
                    if (key != null)
                    {
                        string icon = key.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrEmpty(icon) && File.Exists(icon))
                        {
                            Logger.Instance.Info("[JiYuController] 从注册表定位到极域: " + icon);
                            return icon;
                        }
                    }
                }
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\e-Learning Class V6.0"))
                {
                    if (key != null)
                    {
                        string icon = key.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrEmpty(icon) && File.Exists(icon))
                        {
                            Logger.Instance.Info("[JiYuController] 从注册表(WOW64)定位到极域: " + icon);
                            return icon;
                        }
                    }
                }
            }
            catch { }

            // 常见安装路径（对应原项目）
            string[] commonPaths = new[]
            {
                @"C:\Program Files\Mythware\极域课堂管理系统软件V6.0 2016 豪华版\StudentMain.exe",
                @"C:\Program Files\Mythware\e-Learning Class\StudentMain.exe",
                @"C:\Program Files (x86)\Mythware\极域课堂管理系统软件V6.0 2016 豪华版\StudentMain.exe",
                @"C:\Program Files (x86)\Mythware\e-Learning Class\StudentMain.exe",
                @"C:\e-Learning Class\StudentMain.exe",
                @"C:\极域课堂管理系统软件V6.0 2016 豪华版\StudentMain.exe",
            };

            foreach (string path in commonPaths)
            {
                if (File.Exists(path))
                {
                    Logger.Instance.Info("[JiYuController] 从常见路径定位到极域: " + path);
                    return path;
                }
            }

            Logger.Instance.Warn("[JiYuController] 未能定位极域主进程位置");
            return "";
        }

        /// <summary>
        /// 获取状态文本
        /// </summary>
        public string GetStatusText()
        {
            if (IsJiYuRunning)
            {
                if (_studentControlled) return "已控制极域";
                if (_virusInstalled) return "DLL已注入";
                return "极域运行中 (PID: " + JiYuProcessId + ")";
            }
            return "极域未运行";
        }
    }
}
