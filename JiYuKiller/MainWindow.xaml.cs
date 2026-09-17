using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JiYuKiller
{
    public partial class MainWindow : Window
    {
        private Models.AppSettings _settings;
        private Services.JiYuController _controller;
        private bool _isInitializing = false;
        private Services.TeacherSimService _teacherSimService;
        private WinForms.NotifyIcon _trayIcon;
        private bool _hideTipShown = false;
        private bool _isExiting = false;
        private bool _isTopMost = false;
        private Effects.GlassyWindowManager _glassyManager;
        private int _lastPageIndex = -1; // 用于页面方向过渡
        private int _eggClickCount = 0; // 彩蛋点击计数
        private string _eggTempPath = null; // 彩蛋视频临时路径
        private bool _eggShowing = false; // 彩蛋是否正在显示
        private bool _eggExtracted = false; // 视频是否已释放
        private readonly Services.ScreenshotService _screenshotService = new Services.ScreenshotService();
        private readonly Services.RealtimeReplaceService _realtimeService = new Services.RealtimeReplaceService();

        // ===== 底栏指针柔光：跟手但带一点惯性 =====
        // 直接赋值会让反光"啪"地跳到鼠标上，像手电筒；真实玻璃反光有惯性。
        // 用 MouseMove 里一次线性插值模拟（约 3~4 帧收敛，不建动画时钟、不加 timer）。
        private const double NavSpecularLag = 0.35;
        private double _navSpecularX = 0;
        private double _navSpecularY = 0;
        private bool _navSpecularInit = false;

        // 窗口是否已经显形（启动时先 Opacity=0，等首次桌面截图就绪再淡入）
        private bool _windowRevealed = false;

        // 底栏每个按钮各自的"磨砂玻璃"采样画刷（每按钮一份，见 UpdateNavButtonBackdrops）
        private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.Button, System.Windows.Media.VisualBrush> _navBtnGlassBrushes
            = new System.Collections.Generic.Dictionary<System.Windows.Controls.Button, System.Windows.Media.VisualBrush>();

        // Win32 API
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;

        // 全局快捷键 API（SetLastError=true 才能用 GetLastWin32Error 区分"被其它程序占用"）
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const int HOTKEY_FAKEFULL = 9000;
        private const int HOTKEY_SHOWHIDE = 9001;
        private const int WM_COPYDATA = 0x004A;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        private System.Windows.Interop.HwndSource _hwndSource;

        public MainWindow()
        {
            InitializeComponent();

            Services.Logger.Instance.WindowEvent("MainWindow", "构造函数开始");

            // 加载设置
            _settings = Models.AppSettings.Load();
            _controller = Services.JiYuController.Instance;
            _controller.UpdateSettings(_settings);

            // 应用设置到 UI
            ApplySettingsToUI();

            // 应用 Liquid Glass 效果
            ApplyLiquidGlass();

            // 圆角Clip：裁GlassClipRoot（内层），描边留在GlassContainer外层不被裁
            GlassClipRoot.SizeChanged += (s, e) => UpdateGlassClip();
            this.Loaded += (s, e) => UpdateGlassClip();
            // 底栏玻璃效果挂接
            NavBarClipRoot.SizeChanged += (s, e) => { UpdateNavBarClip(); UpdateNavBarBackdrop(); UpdateNavBarLensParams(); UpdateNavBarTextTheme(); };
            // 指示器"嵌套玻璃"：位移动画每一帧、以及宽度变化时都要重算采样区域
            if (NavIndicatorTransform != null)
            {
                NavIndicatorTransform.Changed += (s, e) => UpdateNavIndicatorBackdrop();
            }
            if (NavIndicator != null)
            {
                NavIndicator.SizeChanged += (s, e) => UpdateNavIndicatorBackdrop();
            }
            // 按钮磨砂：底栏横向滚动 / 窗口尺寸变化都要重算采样区域
            if (NavScrollViewer != null)
            {
                NavScrollViewer.ScrollChanged += (s, e) => UpdateNavButtonBackdrops();
            }
            this.SizeChanged += (s, e) => UpdateNavButtonBackdrops();
            this.Loaded += (s, e) => { UpdateNavBarClip(); UpdateNavBarBackdrop(); UpdateNavBarTextTheme(); };

            // 初始化毛玻璃效果管理器（窗口加载后）
            this.Loaded += MainWindow_Loaded;

            // 初始化系统托盘
            InitTrayIcon();

            // 提前添加HwndHook，确保DLL注入后的回调消息能被接收
            this.SourceInitialized += (s, e) =>
            {
                _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (_hwndSource != null)
                {
                    _hwndSource.AddHook(HwndHook);
                    _controller.SetMainWindowHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                    Services.Logger.Instance.Info("[HwndHook] 已提前添加消息钩子");
                }
            };

            // 启动监控
            _controller.Start();

            // 初始化教师端模拟服务
            _teacherSimService = new Services.TeacherSimService();
            _teacherSimService.ExePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "teacher_sim.exe");
            _teacherSimService.WorkDir = AppDomain.CurrentDomain.BaseDirectory;
            _teacherSimService.OnLogOutput += TeacherSim_OnLogOutput;
            _teacherSimService.OnLogFileOutput += TeacherSim_OnLogFileOutput;
            _teacherSimService.OnStateChanged += TeacherSim_OnStateChanged;

            // 注册全局快捷键
            this.Loaded += (s, e) => RegisterGlobalHotKeys();
            this.Closed += (s, e) => UnregisterGlobalHotKeys();

            // 定时更新状态
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            // 先不显形（Opacity=0），等首次桌面截图就绪再淡入 —— 见 RevealWindowAfterBackdrop。
            // 兜底：1.5 秒内无论如何都显形，绝不能留下一个看不见的窗口。
            this.Opacity = 0;
            var revealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            revealTimer.Tick += (s, ev) =>
            {
                revealTimer.Stop();
                if (!_windowRevealed)
                {
                    Services.Logger.Instance.Warn("[启动] 1.5 秒内仍未取得桌面截图, 强制显形(背景可能先为空)");
                    RevealWindowAfterBackdrop();
                }
            };
            revealTimer.Start();

            // 默认显示快捷栏
            // 自检钩子: JYKILLER_START_PAGE=advanced 可指定启动页, 用于截图核对各页面(无法模拟鼠标点击时)
            string startPage = Environment.GetEnvironmentVariable("JYKILLER_START_PAGE");
            string[] knownPages = { "quick", "settings", "custom", "udpattack", "chat", "screenshot", "teachersim", "games", "help", "debug", "about", "aboutme", "advanced" };
            startPage = string.IsNullOrWhiteSpace(startPage) ? "quick" : startPage.Trim().ToLowerInvariant();
            if (Array.IndexOf(knownPages, startPage) < 0) startPage = "quick";
            ShowPage(startPage);
            UpdateJiYuStatus();

            Services.Logger.Instance.WindowEvent("MainWindow", "构造函数完成");
        }

        #region 系统托盘

        private void InitTrayIcon()
        {
            Services.Logger.Instance.FunctionCall("InitTrayIcon");

            _trayIcon = new WinForms.NotifyIcon();
            _trayIcon.Text = "学习不通";
            // 从嵌入资源加载图标
            try
            {
                System.Uri iconUri = new System.Uri("pack://application:,,,/学习不通;component/Assets/JiYuTrainerLogo.ico", System.UriKind.Absolute);
                System.Windows.Resources.StreamResourceInfo sri = System.Windows.Application.GetResourceStream(iconUri);
                if (sri != null)
                {
                    _trayIcon.Icon = new Drawing.Icon(sri.Stream);
                    Services.Logger.Instance.Debug("托盘图标已从嵌入资源加载");
                }
                else
                {
                    _trayIcon.Icon = Drawing.SystemIcons.Application;
                    Services.Logger.Instance.Warn("嵌入资源图标加载失败，使用系统默认图标");
                }
            }
            catch (Exception ex)
            {
                _trayIcon.Icon = Drawing.SystemIcons.Application;
                Services.Logger.Instance.Warn("托盘图标加载异常: " + ex.Message);
            }
            _trayIcon.Visible = true;

            // 双击托盘显示/隐藏主窗口
            _trayIcon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    ToggleMainWindow();
                }
            };

            // 右键菜单
            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowMainWindow());
            menu.Items.Add("退出软件", null, (s, e) => ExitApplication());
            _trayIcon.ContextMenuStrip = menu;

            Services.Logger.Instance.Info("系统托盘图标已创建");
        }

        private void ToggleMainWindow()
        {
            if (this.Visibility == Visibility.Visible)
            {
                HideToTray();
            }
            else
            {
                ShowMainWindow();
            }
        }

        private void ShowMainWindow()
        {
            Services.Logger.Instance.ButtonClick("显示主界面", "TrayMenu");
            this.ShowInTaskbar = true;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            Services.Logger.Instance.Info("主窗口已显示");
        }
        private void HideToTray()
        {
            Services.Logger.Instance.FunctionCall("HideToTray");
            this.ShowInTaskbar = false;
            this.Hide();

            if (!_hideTipShown)
            {
                _trayIcon.ShowBalloonTip(3000, "学习不通 提示", "窗口隐藏到此处了，双击这里显示主界面", WinForms.ToolTipIcon.Info);
                _hideTipShown = true;
                Services.Logger.Instance.Info("首次隐藏提示已显示");
            }

            Services.Logger.Instance.Info("主窗口已隐藏到托盘");
        }
        private void ExitApplication()
        {
            Services.Logger.Instance.ButtonClick("退出软件", "TrayMenu");
            _isExiting = true;
            Services.Logger.Instance.Info("正在退出程序");
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            // 停止教师端模拟进程
            if (_teacherSimService != null && _teacherSimService.IsRunning)
            {
                _teacherSimService.Stop();
                Services.Logger.Instance.Info("已停止教师端模拟进程");
            }
            StopChatRoom();
            _controller.Stop();
            try { _glassyManager?.Dispose(); } catch { }
            Services.Logger.Instance.Close();
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        #region 设置加载/保存

        private void ApplySettingsToUI()
        {
                _isInitializing = true;
            Services.Logger.Instance.FunctionCall("ApplySettingsToUI");


            CheckBanRunOp.IsChecked = _settings.BanJiYuRunOp;
            CheckAllowTop.IsChecked = _settings.AllowGbTop;
            CheckProhibitKill.IsChecked = _settings.ProhibitKillProcess;
            CheckAllowMonitor.IsChecked = _settings.AllowMonitor;
            CheckProhibitClose.IsChecked = _settings.ProhibitCloseWindow;
            CheckAllowControl.IsChecked = _settings.AllowControl;
            CheckDebugMode.IsChecked = _settings.DebugMode;

            // 底栏液态玻璃（_isInitializing 为真时事件回调会直接返回，不会误判为用户操作）
            if (CheckNavBarLiquidGlass != null) CheckNavBarLiquidGlass.IsChecked = _settings.NavBarLiquidGlass;
            if (CheckNavLensOnTier0 != null) CheckNavLensOnTier0.IsChecked = _settings.NavBarLiquidGlassForceOnTier0;
            if (CheckNavSquircle != null) CheckNavSquircle.IsChecked = _settings.NavBarSquircle;
            if (SliderSquircleExt != null) SliderSquircleExt.Value = _settings.NavBarSquircleExtension;
            if (TextSquircleExtValue != null) TextSquircleExtValue.Text = _settings.NavBarSquircleExtension.ToString("F2");
            if (SliderNavLensStrength != null) SliderNavLensStrength.Value = _settings.NavBarLiquidGlassStrength;
            if (TextNavLensValue != null) TextNavLensValue.Text = string.Format("{0}%", (int)(_settings.NavBarLiquidGlassStrength * 100));
            ApplyHotKeysToUI();

            VersionText.Text = _settings.Version;
            AboutVersion.Text = _settings.Version;

            if (!string.IsNullOrEmpty(_settings.JiYuMainPath))
            {
                JiYuPathText.Text = $"极域路径: {_settings.JiYuMainPath}";
            }

            Services.Logger.Instance.Debug("设置已应用到 UI");
                _isInitializing = false;
        }

        private void SaveSettingsFromUI()
        {
            if (_isInitializing) return;
            Services.Logger.Instance.FunctionCall("SaveSettingsFromUI");


            _settings.BanJiYuRunOp = CheckBanRunOp.IsChecked ?? false;
            _settings.AllowGbTop = CheckAllowTop.IsChecked ?? false;
            _settings.ProhibitKillProcess = CheckProhibitKill.IsChecked ?? false;
            _settings.AllowMonitor = CheckAllowMonitor.IsChecked ?? false;
            _settings.ProhibitCloseWindow = CheckProhibitClose.IsChecked ?? false;
            _settings.AllowControl = CheckAllowControl.IsChecked ?? false;
            _settings.DebugMode = CheckDebugMode.IsChecked ?? false;

            // 底栏液态玻璃（这两个控件的事件也会写，这里统一再收一次，保证保存按钮生效）
            if (CheckNavBarLiquidGlass != null) _settings.NavBarLiquidGlass = CheckNavBarLiquidGlass.IsChecked ?? false;
            if (CheckNavLensOnTier0 != null) _settings.NavBarLiquidGlassForceOnTier0 = CheckNavLensOnTier0.IsChecked ?? false;
            if (CheckNavSquircle != null) _settings.NavBarSquircle = CheckNavSquircle.IsChecked ?? true;
            if (SliderSquircleExt != null) _settings.NavBarSquircleExtension = SliderSquircleExt.Value;
            if (SliderNavLensStrength != null) _settings.NavBarLiquidGlassStrength = SliderNavLensStrength.Value;

            // 快捷键：读回界面上的打包值（Tag），变化时立刻重新注册，不用重启
            int oldFake = _settings.HotKeyFakeFull, oldHide = _settings.HotKeyShowHide;
            if (TextHotKeyFakeFull != null && TextHotKeyFakeFull.Tag is int) _settings.HotKeyFakeFull = (int)TextHotKeyFakeFull.Tag;
            if (TextHotKeyShowHide != null && TextHotKeyShowHide.Tag is int) _settings.HotKeyShowHide = (int)TextHotKeyShowHide.Tag;
            bool hotKeyChanged = _settings.HotKeyFakeFull != oldFake || _settings.HotKeyShowHide != oldHide;

            _settings.Save();
            _controller.UpdateSettings(_settings);

            if (hotKeyChanged)
            {
                try
                {
                    UnregisterGlobalHotKeys();
                    RegisterGlobalHotKeys();
                    Services.Logger.Instance.Info(string.Format(
                        "[HotKey] 快捷键已更新: 紧急全屏={0}, 显示/隐藏={1}",
                        HotKeyToText(_settings.HotKeyFakeFull), HotKeyToText(_settings.HotKeyShowHide)));
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Warn("[HotKey] 重新注册失败: " + ex.Message);
                }
            }

            Services.Logger.Instance.Info("设置已从 UI 保存");
        }

        #endregion
















        private void BtnVideoPlayPause_Click(object sender, RoutedEventArgs e)
        {
            // 用系统默认播放器打开视频文件
            string videoPath = _realtimeService?.CurrentVideoPath;
            if (string.IsNullOrEmpty(videoPath) || !System.IO.File.Exists(videoPath))
            {
                System.Windows.MessageBox.Show("视频文件不存在", "实时屏幕替换");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(videoPath);
                Services.Logger.Instance.Info("[Realtime] 用系统播放器打开视频: " + videoPath);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[Realtime] 打开视频失败: " + ex.Message);
                System.Windows.MessageBox.Show("打开视频失败: " + ex.Message, "实时屏幕替换");
            }
        }
    }
}

