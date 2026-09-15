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

        // 用于中断监控线程的等待, 使 Stop() 不必等满一个 CKInterval 周期
        private readonly System.Threading.ManualResetEventSlim _stopEvent =
            new System.Threading.ManualResetEventSlim(false);

        // 服务
        private readonly DllInjectService _dllInject = new DllInjectService();
        private readonly DriverService _driver = new DriverService();

        // 状态
        private bool _virusInstalled = false;
        private bool _studentControlled = false;
        private IntPtr _currentBroadcastWnd = IntPtr.Zero;
        private IntPtr _currentBlackScreenWnd = IntPtr.Zero;
        private int _screenWidth, _screenHeight;
        private bool _gbFullManual = false;
        private bool _gbTopManual = false;  // 对应参考实现 gbFullManual（DLL菜单全屏）
        private IntPtr _mainWindowHandle = IntPtr.Zero;  // UI层登记的主窗口句柄

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
        public event Action OnAllowGbTopRequested;  // DLL请求允许广播置顶

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
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("ntdll.dll")]
        private static extern int NtTerminateProcess(IntPtr hProcess, int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const uint PROCESS_TERMINATE = 0x0001;

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
        private const uint SWP_DRAWFRAME = 0x0020;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_HIDE = 0;
        private const int SW_MINIMIZE = 6;
        private const uint WM_SIZE = 0x0005;
        private const uint WS_SYSMENU = 0x00080000;
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
            _stopEvent.Reset();
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
            _stopEvent.Set();
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

                // 每轮重新读取 CKInterval, 使设置界面修改的间隔立即生效(旧实现只在进入循环时读一次)。
                int checkInterval = Math.Max(1000, Math.Min(10000,
                    _settings.CKInterval > 0 ? _settings.CKInterval : 3000));

                // 可中断等待: Stop() 触发后立即退出, 不必等满一个周期
                if (_stopEvent.Wait(checkInterval))
                {
                    break;
                }
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

                // 每个检查周期都会枚举一次, 必须释放所有 Process 对象(含未选中的),
                // 否则进程句柄与 GC 压力会持续累积。
                if (processes.Length > 0)
                {
                    jiYuProcess = processes[0];
                    for (int i = 1; i < processes.Length; i++)
                    {
                        processes[i].Dispose();
                    }
                    break;
                }

                for (int i = 0; i < processes.Length; i++)
                {
                    processes[i].Dispose();
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

                jiYuProcess.Dispose();
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

            string dllPath = Services.EmbeddedResourceService.GetDllPath();

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
                SendVirusMessage("hk:ckstat");  // 对应参考实现 _NextLoopGetCkStat，DLL做版本探测+键盘解锁
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
        /// <summary>
        /// 发送设置到注入的DLL（通过INI文件，与原项目机制一致）
        /// </summary>
        private void SendSettingsToVirus()
        {
            try
            {
                string iniPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i.chaoxing.ini");
                WriteSettingsToIni(iniPath);
                Logger.Instance.Info("[JiYuController] 设置已写入INI: " + iniPath);
                SendVirusMessage("hk:inipath:" + iniPath);
                Logger.Instance.Info("[JiYuController] 已通知DLL重新读取设置");
                _fakeFull = true;
                SendVirusMessage("hk:fkfull:true");
                Logger.Instance.Info("[JiYuController] 已设置fakeFull=true（允许广播窗口全屏）");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] 写入设置INI失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 将设置写入INI文件（配置节[JTSettings]）
        /// </summary>
        private void WriteSettingsToIni(string iniPath)
        {
            WritePrivateProfileString("JTSettings", "AutoForceKill", _settings.AutoForceKill ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "AllowAllRunOp", "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "BandAllRunOp", _settings.BanJiYuRunOp ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "ProhibitKillProcess", _settings.ProhibitKillProcess ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "ProhibitCloseWindow", _settings.ProhibitCloseWindow ? "TRUE" : "FALSE", iniPath);
            // 修复: 原实现硬编码 "TRUE", 导致高级设置里"隐藏极域端控制输出窗口"取消勾选也不生效
            WritePrivateProfileString("JTSettings", "DoNotShowVirusWindow", _settings.DoNotShowVirusWindow ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "ForceDisableWatchDog", _settings.ForceDisableWatchDog ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "AllowGbTop", _settings.AllowGbTop ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "AllowMonitor", _settings.AllowMonitor ? "TRUE" : "FALSE", iniPath);
            WritePrivateProfileString("JTSettings", "AllowControl", _settings.AllowControl ? "TRUE" : "FALSE", iniPath);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string lpAppName, string lpKeyName, string lpString, string lpFileName);

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
        /// <summary>
        /// 与参考实现 CheckWindowTextIsGb 完全一致：广播/演示/共享 或 =="屏幕演播室窗口"
        /// </summary>
        private static bool IsBroadcastWindow(string title)
        {
            return title.Contains("广播") || title.Contains("演示") || title.Contains("共享")
                || title == "屏幕演播室窗口";
        }

        /// <summary>
        /// 与参考实现一致：精确匹配 "BlackScreen Window"
        /// </summary>
        private static bool IsBlackScreenWindow(string title) { return title == "BlackScreen Window"; }

        /// <summary>
        /// 对应参考实现 MsgCenter.cpp:31-46 的 MsgCenteAppendHWND：
        /// 把极域窗口句柄以十进制发给DLL，DLL会调用 VFixGuangBoWindow 接管其窗口过程。
        /// 注意：必须是十进制（DLL用 _wtol 解析），不要发十六进制、不要带 0x。
        /// </summary>
        private void AppendHwndToMsgCenter(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            SendVirusMessage("hw:" + hWnd.ToInt32());
        }

        /// <summary>
        /// 窗口处理（广播窗口化、黑屏窗口处理）
        /// </summary>

        /// <summary>
        /// 处理来自注入DLL的回调（对应参考实现 TrainerWorker.cpp:125-184 HandleMessageFromVirus）。
        /// DLL的广播窗口菜单只回发消息不改窗口，真正的窗口操作必须在这里执行。
        /// </summary>
        public void HandleVirusCallback(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                if (message.StartsWith("hkb:succ"))
                {
                    _studentControlled = true;
                    Logger.Instance.Info("[DLL回调] DLL注入成功确认");
                    // 回发自身窗口句柄给DLL
                    IntPtr hMain = _mainWindowHandle != IntPtr.Zero ? _mainWindowHandle : Process.GetCurrentProcess().MainWindowHandle;
                    if (hMain != IntPtr.Zero) SendVirusMessage("hs:" + hMain.ToInt32());
                }
                else if (message.StartsWith("hkb:jyk:"))
                {
                    Logger.Instance.Info("[DLL回调] 键盘锁定状态: " + message.Substring(7));
                }
                else if (message.StartsWith("hkb:wtf:"))
                {
                    Logger.Instance.Warn("[DLL回调] 检测到非极域进程注入, PID=" + message.Substring(8));
                }
                else if (message.StartsWith("hkb:immck"))
                {
                    Logger.Instance.Info("[DLL回调] 输入法检查完成，立即刷新窗口");
                    ResolveWindows();
                }
                else if (message.StartsWith("hkb:showhelp"))
                {
                    Logger.Instance.Info("[DLL回调] 请求显示帮助");
                }
                else if (message.StartsWith("hkb:algbtop"))
                {
                    Logger.Instance.Info("[DLL回调] 请求允许广播窗口置顶 → 自动打开该设置");
                    OnAllowGbTopRequested?.Invoke();
                }
                else if (message.StartsWith("hkb:gbuntop"))
                {
                    _gbTopManual = false;
                    Logger.Instance.Info("[DLL回调] 广播窗口取消置顶 → 执行");
                    ManualTop(false);
                }
                else if (message.StartsWith("hkb:gbtop"))
                {
                    _gbTopManual = true;
                    Logger.Instance.Info("[DLL回调] 广播窗口置顶 → 执行");
                    ManualTop(true);
                }
                else if (message.StartsWith("hkb:gbmfull"))
                {
                    Logger.Instance.Info("[DLL回调] 广播窗口全屏 → 执行");
                    _gbFullManual = true;
                    ManualFull(true);
                }
                else if (message.StartsWith("hkb:gbmnofull"))
                {
                    Logger.Instance.Info("[DLL回调] 广播窗口退出全屏 → 执行");
                    if (_fakeBroadcastFull) { _fakeBroadcastFull = false; }
                    ManualFull(false);
                }
                else if (message.StartsWith("wcd:"))
                {
                    Logger.Instance.Debug("[DLL回调] 看门狗心跳: " + message);
                }
                else
                {
                    Logger.Instance.Debug("[DLL回调] 未知消息: " + message);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[DLL回调] HandleVirusCallback异常", ex);
            }
        }

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

                // 跳过注入DLL自己的状态窗口（参考实现 EnumWindowsProc:1088）
                if (windowTitle == "JiYu Trainer Virus Window") return true;

                // 检测黑屏窗口
                if (IsBlackScreenWindow(windowTitle))
                {
                    _currentBlackScreenWnd = hWnd;
                    AppendHwndToMsgCenter(hWnd);
                    if (!_fakeBlackScreenFull) FixBlackScreenWindow(hWnd);
                    return true;
                }

                // 检测广播窗口
                if (IsBroadcastWindow(windowTitle))
                {
                    _currentBroadcastWnd = hWnd;
                    AppendHwndToMsgCenter(hWnd);
                    if (!_fakeBroadcastFull) FixBroadcastWindow(hWnd);
                    return true;
                }

                // 严格窗口控制模式：其他极域全屏窗口也处理（参考实现 EnumWindowsProc:1097）
                if (_settings.AutoIncludeFullWindow)
                {
                    RECT rc;
                    if (GetWindowRect(hWnd, out rc) &&
                        rc.Left == 0 && rc.Top == 0 &&
                        rc.Right == _screenWidth && rc.Bottom == _screenHeight)
                    {
                        AppendHwndToMsgCenter(hWnd);
                        FixFullScreenJiYuWindow(hWnd);
                    }
                }

                return true;
            }, IntPtr.Zero);
        }

        /// <summary>对应参考实现 FixWindow 的 setAutoIncludeFullWindow 分支（TrainerWorker.cpp:1061-1072）</summary>
        private void FixFullScreenJiYuWindow(IntPtr hWnd)
        {
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((ex & (int)WS_EX_TOPMOST) == (int)WS_EX_TOPMOST)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, ex & ~(int)WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            int style = GetWindowLong(hWnd, GWL_STYLE);
            int newStyle = style | (int)WS_BORDER | (int)WS_OVERLAPPEDWINDOW;
            if (newStyle != style) SetWindowLong(hWnd, GWL_STYLE, newStyle);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOZORDER | SWP_NOSIZE | SWP_NOMOVE | SWP_DRAWFRAME | SWP_NOACTIVATE);
            Logger.Instance.Debug("[JiYuController] 严格窗口控制: 已处理全屏窗口 HWND=" + hWnd);
        }
        public void SetMainWindowHandle(IntPtr hWnd) { _mainWindowHandle = hWnd; }

        /// <summary>对应参考实现 ManualTop（TrainerWorker.cpp:989-1003）</summary>
        public void ManualTop(bool top)
        {
            if (_currentBroadcastWnd == IntPtr.Zero) return;
            IntPtr hWnd = _currentBroadcastWnd;
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            if (top)
            {
                if ((ex & (int)WS_EX_TOPMOST) == 0)
                    SetWindowLong(hWnd, GWL_EXSTYLE, ex | (int)WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            else
            {
                if ((ex & (int)WS_EX_TOPMOST) == (int)WS_EX_TOPMOST)
                    SetWindowLong(hWnd, GWL_EXSTYLE, ex & ~(int)WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            Logger.Instance.Info("[JiYuController] ManualTop(" + top + ") HWND=" + hWnd);
        }

        /// <summary>对应参考实现 ManualFull（TrainerWorker.cpp:971-988）</summary>
        public void ManualFull(bool full)
        {
            if (_currentBroadcastWnd == IntPtr.Zero) return;
            IntPtr hWnd = _currentBroadcastWnd;
            if (full)
            {
                int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                SetWindowLong(hWnd, GWL_EXSTYLE, ex | (int)WS_EX_TOPMOST);
                int style = GetWindowLong(hWnd, GWL_STYLE);
                style = (style ^ ((int)WS_BORDER | (int)WS_OVERLAPPEDWINDOW)) | (int)WS_SYSMENU;
                SetWindowLong(hWnd, GWL_STYLE, style);
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, _screenWidth, _screenHeight, SWP_SHOWWINDOW);
                SendMessage(hWnd, WM_SIZE, IntPtr.Zero, (IntPtr)((_screenHeight << 16) | _screenWidth));
                Logger.Instance.Info("[JiYuController] ManualFull(true) HWND=" + hWnd);
            }
            else
            {
                _gbFullManual = false;
                FixBroadcastWindow(hWnd);
                int w = (int)(_screenWidth * 3.0 / 4.0);
                int h = (int)(_screenHeight * 4.0 / 5.0);
                SetWindowPos(hWnd, IntPtr.Zero, (_screenWidth - w) / 2, (_screenHeight - h) / 2, w, h,
                    SWP_NOZORDER | SWP_SHOWWINDOW);
                Logger.Instance.Info("[JiYuController] ManualFull(false) HWND=" + hWnd);
            }
        }

        /// <summary>
        /// 广播窗口窗口化（对应参考实现 TrainerWorker.cpp:1032-1075 的常规路径）。
        /// 关键：只翻样式位，绝不移动/缩放窗口。
        /// 极域的广播画面画在子窗口 TDDesk Render Window 上，该子窗口按创建时的全屏尺寸输出；
        /// 父窗口被改尺寸后不会重建，结果是画面黑屏/错位。
        /// </summary>
        private void FixBroadcastWindow(IntPtr hWnd)
        {
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);

            // AllowGbTop是"别动TOPMOST"（被动），不是"强制置顶"（主动）
            // 用户手动置顶(_gbTopManual=true)时不去掉，和_gbFullManual逻辑一致
            if (!_settings.AllowGbTop && !_gbTopManual && (ex & (int)WS_EX_TOPMOST) == (int)WS_EX_TOPMOST)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, ex & ~(int)WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            // 用户在DLL菜单选择过"广播窗口全屏"时，不要改回窗口化
            if (!_gbFullManual)
            {
                int style = GetWindowLong(hWnd, GWL_STYLE);
                int newStyle = style | (int)WS_BORDER | (int)WS_OVERLAPPEDWINDOW;
                if (newStyle != style) SetWindowLong(hWnd, GWL_STYLE, newStyle);
            }

            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOZORDER | SWP_NOSIZE | SWP_NOMOVE | SWP_DRAWFRAME | SWP_NOACTIVATE);

            Logger.Instance.Debug("[JiYuController] 广播窗口已窗口化(仅样式): HWND=" + hWnd);
        }
        private void FixBlackScreenWindow(IntPtr hWnd)
        {
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            // 切换APPWINDOW并加上NOACTIVATE
            SetWindowLong(hWnd, GWL_EXSTYLE, (ex ^ (int)WS_EX_APPWINDOW) | (int)WS_EX_NOACTIVATE);
            SetWindowPos(hWnd, IntPtr.Zero, 20, 20, 90, 150,
                SWP_NOZORDER | SWP_DRAWFRAME | SWP_NOACTIVATE);
            ShowWindow(hWnd, SW_HIDE);
            Logger.Instance.Debug("[JiYuController] 黑屏窗口已处理并隐藏: HWND=" + hWnd);
        }

        /// <summary>
        /// 加载驱动
        /// </summary>
        public bool LoadDriver()
        {
            Logger.Instance.FunctionCall("LoadDriver");

            string driverPath = Services.EmbeddedResourceService.GetDriverPath();

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
                    // 必须使用"真实"系统版本:
                    //   Environment.OSVersion 走 GetVersionEx, 在缺少 supportedOS 清单时会把
                    //   Win8.1/Win10 一律报成 6.2.9200, 于是 isWin7 与 isWinXP 会同时为 false,
                    //   驱动就会按错误的版本分支选择内核结构偏移。
                    bool isXp, isWin7;
                    DriverService.GetDriverInitFlags(out isXp, out isWin7);
                    uint buildVer = DriverService.GetRealWindowsBuild();

                    // 日志格式与参考实现 DriverLoader.cpp:XLoadDriver() 保持一致, 便于对照排查
                    Logger.Instance.Info("Windows Bulid version " + buildVer);
                    Logger.Instance.Info(string.Format(
                        "[JiYuController] 真实系统版本={0}, isWin7={1}, isWinXP={2}, build={3}",
                        DriverService.DescribeRealWindowsVersion(), isWin7, isXp, buildVer));

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

            // 按真实结果记日志: 原来无论成功失败都写"驱动已卸载"，卸载失败时日志会骗人
            //（具体错误码在上方 [Driver] 开头的日志里）。
            bool ok = _driver.UnloadDriver();
            if (ok)
            {
                Logger.Instance.Info("[JiYuController] 驱动已卸载");
            }
            else
            {
                Logger.Instance.Error("[JiYuController] 驱动卸载失败, 详见上方 [Driver] 日志");
            }
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
            int ntStatus;
            bool ok = _driver.KillProcess(pid, out ntStatus);
            if (!ok)
            {
                Logger.Instance.Error(string.Format(
                    "[JiYuController] 内核级杀进程失败, PID={0}, NTSTATUS=0x{1:X8}", pid, ntStatus));
            }
            return ok;
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
                    int ntStatus;
                    bool result = _driver.KillProcess(JiYuProcessId, out ntStatus);
                    if (result)
                    {
                        Logger.Instance.Info("[JiYuController] 内核级杀死极域成功 PID=" + JiYuProcessId);
                        IsJiYuRunning = false;
                        JiYuProcessId = 0;
                        _virusInstalled = false;
                        _studentControlled = false;
                        OnStatusChanged?.Invoke();
                    }
                    else
                    {
                        Logger.Instance.Error(string.Format(
                            "[JiYuController] 内核级杀进程失败, PID={0}, NTSTATUS=0x{1:X8}",
                            JiYuProcessId, ntStatus));
                    }
                    return result;
                }
                Logger.Instance.Warn("[JiYuController] 驱动未打开，回退到用户态杀进程");
            }

            // TerminateProcess / NtTerminateProcess: 用户态杀进程
            try
            {
                using (Process proc = Process.GetProcessById(JiYuProcessId))
                {
                    proc.Kill();
                }
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
                string stopOutput;
                int stopExit = RunSc("stop TDNetFilter", out stopOutput);
                string deleteOutput;
                int deleteExit = RunSc("delete TDNetFilter", out deleteOutput);

                Logger.Instance.Info(string.Format(
                    "[JiYuController] sc stop TDNetFilter 退出码={0}, 输出={1}", stopExit, stopOutput.Trim()));
                Logger.Instance.Info(string.Format(
                    "[JiYuController] sc delete TDNetFilter 退出码={0}, 输出={1}", deleteExit, deleteOutput.Trim()));

                if (deleteExit == 0)
                {
                    MessageBox.Show("已解除网络控制（TDNetFilter 驱动已停止并删除）。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    // 原实现无论成功失败都提示"已尝试解除", 这里改为按退出码给出真实结果
                    MessageBox.Show(
                        "未能删除 TDNetFilter 驱动（sc delete 退出码 " + deleteExit + "）。\n\n" +
                        "常见原因：服务正在运行或被其它进程占用。\n" +
                        "请重启电脑后再试，或手动执行：sc delete TDNetFilter",
                        "未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] 卸载网络过滤驱动失败", ex);
                MessageBox.Show("操作失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 运行 sc.exe 子命令，排空输出并返回退出码。
        /// 原实现只 WaitForExit 不读取输出：子进程写满管道后会阻塞，而且 Process 对象既不释放、
        /// 超时后也不结束，句柄会一直挂着。这里改用异步读取 + 超时强杀 + using 释放。
        /// </summary>
        private static int RunSc(string arguments, out string output)
        {
            output = "";

            ProcessStartInfo psi = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null)
                {
                    output = "(无法启动 sc.exe)";
                    return -1;
                }

                StringBuilder sb = new StringBuilder();
                object gate = new object();
                DataReceivedEventHandler collect = (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (gate)
                        {
                            sb.AppendLine(e.Data);
                        }
                    }
                };

                p.OutputDataReceived += collect;
                p.ErrorDataReceived += collect;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    lock (gate)
                    {
                        output = sb.ToString() + " (等待超时, 已强制结束)";
                    }
                    return -1;
                }

                // 无参 WaitForExit 会等异步输出读取结束, 否则可能读到不完整的输出
                p.WaitForExit();
                lock (gate)
                {
                    output = sb.ToString();
                }
                return p.ExitCode;
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
                if (_settings != null && _settings.JiYuMainPath != JiYuProcessPath)
                {
                    _settings.JiYuMainPath = JiYuProcessPath;
                    _settings.Save();
                    Logger.Instance.Info("[JiYuController] 极域路径已保存: " + JiYuProcessPath);
                }
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
                            if (_settings != null && _settings.JiYuMainPath != icon)
                            {
                                _settings.JiYuMainPath = icon;
                                _settings.Save();
                            }
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
                            if (_settings != null && _settings.JiYuMainPath != icon)
                            {
                                _settings.JiYuMainPath = icon;
                                _settings.Save();
                            }
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
                    if (_settings != null && _settings.JiYuMainPath != path)
                    {
                        _settings.JiYuMainPath = path;
                        _settings.Save();
                    }
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

        private bool _fakeFull = false;
        public bool IsFakeFull => _fakeFull;
        private bool _fakeBroadcastFull = false;
        private bool _fakeBlackScreenFull = false;
        private IntPtr _resizedBroadcastWnd = IntPtr.Zero;

        /// <summary>
        /// 切换紧急全屏（伪全屏）
        /// 对应原项目 SwitchFakeFull，通过DLL消息+窗口调整实现
        /// </summary>
        public bool SwitchFakeFull()
        {
            Logger.Instance.FunctionCall("SwitchFakeFull");

            if (!IsJiYuRunning || _currentBroadcastWnd == IntPtr.Zero)
            {
                Logger.Instance.Warn("[JiYuController] 未找到广播窗口，仅发送DLL消息");
                SendVirusMessage("hk:fkfull:" + (_fakeFull ? "false" : "true"));
                return _fakeFull;
            }

            _fakeFull = !_fakeFull;
            SendVirusMessage("hk:fkfull:" + (_fakeFull ? "true" : "false"));

            if (_fakeFull)
            {
                // 进入假装全屏（对应参考实现 FakeFull(true)）
                int ex = GetWindowLong(_currentBroadcastWnd, GWL_EXSTYLE);
                SetWindowLong(_currentBroadcastWnd, GWL_EXSTYLE, ex | (int)WS_EX_TOPMOST);
                int style = GetWindowLong(_currentBroadcastWnd, GWL_STYLE);
                SetWindowLong(_currentBroadcastWnd, GWL_STYLE,
                    style ^ ((int)WS_BORDER | (int)WS_OVERLAPPEDWINDOW));
                SetWindowPos(_currentBroadcastWnd, HWND_TOPMOST, 0, 0, _screenWidth, _screenHeight, SWP_SHOWWINDOW);
                SendMessage(_currentBroadcastWnd, WM_SIZE, IntPtr.Zero,
                    (IntPtr)((_screenHeight << 16) | _screenWidth));
                _fakeBroadcastFull = true;  // 守卫：轮询期间不再去改它
                Logger.Instance.Info("[JiYuController] 广播窗口已伪全屏");
            }
            else
            {
                _fakeBroadcastFull = false;
                // 退出假装全屏：先恢复样式，再做一次窗口化尺寸
                FixBroadcastWindow(_currentBroadcastWnd);
                int w = (int)(_screenWidth * 3.0 / 4.0);
                int h = (int)(_screenHeight * 4.0 / 5.0);
                SetWindowPos(_currentBroadcastWnd, IntPtr.Zero,
                    (_screenWidth - w) / 2, (_screenHeight - h) / 2, w, h,
                    SWP_NOZORDER | SWP_SHOWWINDOW);
                Logger.Instance.Info("[JiYuController] 广播窗口已恢复窗口化");
            }

            OnStatusChanged?.Invoke();
            return _fakeFull;
        }
        #region 极域密码读取（移植自原项目 ReadTopDomanPassword + UnDecryptJiyuKnock）

        /// <summary>
        /// 读取极域电子教室密码
        /// 对应原项目 TrainerWorker::ReadTopDomanPassword
        /// </summary>
        /// <param name="forceDecrypt">true=强制使用解密模式（6.0），false=先尝试普通注册表读取</param>
        /// <returns>密码字符串，失败返回null</returns>
        public string ReadJiYuPassword(bool forceDecrypt = false)
        {
            Logger.Instance.FunctionCall("ReadJiYuPassword forceDecrypt=" + forceDecrypt);

            if (!forceDecrypt)
            {
                string passwd = ReadRegistryPasswordNormal();
                if (passwd != null)
                {
                    if (passwd == "Passwd[123456]")
                    {
                        Logger.Instance.Info("[JiYuController] 检测到6.0版本标记，切换到解密模式");
                    }
                    else
                    {
                        string extracted = ExtractPasswordFromPasswdFormat(passwd);
                        if (extracted != null)
                        {
                            Logger.Instance.Info("[JiYuController] 普通模式读取密码成功");
                            return extracted;
                        }
                    }
                }
            }

            return ReadRegistryPasswordDecrypt();
        }

        private string ReadRegistryPasswordNormal()
        {
            string val = ReadRegistryString(
                @"SOFTWARE\TopDomain\e-Learning Class Standard\1.00",
                "UninstallPasswd",
                Microsoft.Win32.RegistryView.Registry64);
            if (val != null) return val;

            val = ReadRegistryString(
                @"SOFTWARE\Wow6432Node\TopDomain\e-Learning Class Standard\1.00",
                "UninstallPasswd",
                Microsoft.Win32.RegistryView.Registry64);
            return val;
        }

        private string ReadRegistryPasswordDecrypt()
        {
            string subKey = Environment.Is64BitOperatingSystem
                ? @"SOFTWARE\Wow6432Node\TopDomain\e-Learning Class\Student"
                : @"SOFTWARE\TopDomain\e-Learning Class\Student";

            byte[] data = ReadRegistryBinary(subKey, "Knock1", Microsoft.Win32.RegistryView.Registry64);
            if (data == null || data.Length < 4)
            {
                Logger.Instance.Warn("[JiYuController] 读取 Knock1 注册表值失败或数据不足");
                return null;
            }

            Logger.Instance.Info("[JiYuController] 读取 Knock1 成功，长度=" + data.Length);

            string passwd = DecryptJiYuKnock(data);
            if (passwd != null)
            {
                Logger.Instance.Info("[JiYuController] 解密模式读取密码成功");
            }
            else
            {
                Logger.Instance.Warn("[JiYuController] UnDecryptJiyuKnock 解密失败");
            }
            return passwd;
        }

        private string ExtractPasswordFromPasswdFormat(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            int start = raw.IndexOf('[');
            int end = raw.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                return raw.Substring(start + 1, end - start - 1);
            }
            if (raw.Length > 6) return raw.Substring(6);
            return raw;
        }

        /// <summary>
        /// 极域6.0 Knock1 解密算法
        /// 严格移植自原项目 UnDecryptJiyuKnock
        /// 1. 每个DWORD异或 0x50434C45
        /// 2. 每个DWORD再异或 0x454C4350
        /// 3. 从 Data[Data[0]] 偏移处读取 UTF-16LE 字符串
        /// </summary>
        private string DecryptJiYuKnock(byte[] data)
        {
            if (data == null || data.Length < 4) return null;

            try
            {
                int dwordCount = data.Length / 4;

                for (int i = 0; i < dwordCount; i++)
                {
                    int offset = i * 4;
                    uint val = BitConverter.ToUInt32(data, offset);
                    val ^= 0x50434C45u;
                    byte[] bytes = BitConverter.GetBytes(val);
                    Array.Copy(bytes, 0, data, offset, 4);
                }

                for (int i = 0; i < dwordCount; i++)
                {
                    int offset = i * 4;
                    uint val = BitConverter.ToUInt32(data, offset);
                    val ^= 0x454C4350u;
                    byte[] bytes = BitConverter.GetBytes(val);
                    Array.Copy(bytes, 0, data, offset, 4);
                }

                int startOffset = data[0];
                if (startOffset >= data.Length)
                {
                    Logger.Instance.Warn("[JiYuController] 解密后起始偏移越界: " + startOffset);
                    return null;
                }

                var strBytes = new System.Collections.Generic.List<byte>();
                for (int i = startOffset; i + 1 < data.Length; i += 2)
                {
                    byte lo = data[i];
                    byte hi = data[i + 1];
                    if (lo == 0 && hi == 0) break;
                    strBytes.Add(lo);
                    strBytes.Add(hi);
                }

                if (strBytes.Count == 0) return "";
                return Encoding.Unicode.GetString(strBytes.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[JiYuController] DecryptJiYuKnock 异常: " + ex.Message);
                return null;
            }
        }

        private string ReadRegistryString(string subKey, string valueName, Microsoft.Win32.RegistryView view)
        {
            try
            {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    object val = key.GetValue(valueName);
                    return val as string;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Debug("[JiYuController] 读取注册表字符串失败: " + subKey + " - " + ex.Message);
                return null;
            }
        }

        private byte[] ReadRegistryBinary(string subKey, string valueName, Microsoft.Win32.RegistryView view)
        {
            try
            {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    return key.GetValue(valueName) as byte[];
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Debug("[JiYuController] 读取注册表二进制失败: " + subKey + " - " + ex.Message);
                return null;
            }
        }

        #endregion


    }
}
