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
        private readonly Services.ChatService _chatService = new Services.ChatService();
        private readonly Services.ScreenshotService _screenshotService = new Services.ScreenshotService();
        private readonly Services.RealtimeReplaceService _realtimeService = new Services.RealtimeReplaceService();
        private string _chatTargetIP = "";

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
        private int _chatTargetSeat = 0;

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
            _teacherSimService.OnCollisionDetected += TeacherSim_OnCollisionDetected;

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
            _controller.Stop();
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

        #region Liquid Glass

        private void ApplyLiquidGlass()
        {
            Services.Logger.Instance.FunctionCall("ApplyLiquidGlass", $"opacity={_settings.GlassOpacity}");

            double value = _settings.GlassOpacity / 100.0;
            byte whiteAlpha = (byte)((1 - value) * 255);
            double wallpaperOpacity = 1 - value;

            if (WhiteOverlayLayer != null)
            {
                SolidColorBrush whiteBrush = WhiteOverlayLayer.Background as SolidColorBrush;
                if (whiteBrush != null)
                {
                    Color c = whiteBrush.Color;
                    c.A = whiteAlpha;
                    whiteBrush.Color = c;
                }
            }
            if (WallpaperLayer != null)
            {
                WallpaperLayer.Opacity = wallpaperOpacity;
            }
            if (SliderContentOpacity != null)
            {
                SliderContentOpacity.Value = value;
            }

            Services.Logger.Instance.Debug($"Liquid Glass: value={value:F2}, whiteAlpha={whiteAlpha}, wallpaperOpacity={wallpaperOpacity:F2}");
        }

        /// <summary>
        /// 给GlassContainer加真正的圆角裁剪（RectangleGeometry）。
        /// 修复AllowsTransparency窗口四角变黑问题：
        /// ClipToBounds只裁矩形、CornerRadius不裁子元素，
        /// 而Effect(GlassyEffect/DropShadowEffect)的输出会铺满元素矩形，
        /// 因此必须在祖先容器上用圆角RectangleGeometry裁整棵子树。
        /// </summary>
        private void UpdateGlassClip()
        {
            if (GlassClipRoot == null) return;

            double w = GlassClipRoot.ActualWidth;
            double h = GlassClipRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var geo = GlassClipRoot.Clip as System.Windows.Media.RectangleGeometry;
            if (geo == null)
            {
                geo = new System.Windows.Media.RectangleGeometry();
                GlassClipRoot.Clip = geo;
            }
            geo.Rect = new System.Windows.Rect(0, 0, w, h);
            geo.RadiusX = 15;   // 外圆角16 - 描边1，同心
            geo.RadiusY = 15;

            Services.Logger.Instance.Debug($"[圆角Clip] 已应用到GlassClipRoot: {w:F0}x{h:F0}, Radius=15");
        }

        // ========== 底栏玻璃效果 ==========
        // ========== 底栏玻璃效果（快照裁切方案，无着色器，避免色差偏色） ==========
        // 底栏背景用VisualBrush，不需要缓存字段

        /// <summary>初始化：模糊层 + 液态玻璃折射层（受设置与渲染层级控制）</summary>
        private void InitNavBarGlass()
        {
            try
            {
                if (NavBarGlass != null)
                {
                    NavBarGlass.Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 5,
                        KernelType = System.Windows.Media.Effects.KernelType.Gaussian
                    };
                }

                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                navLensTier = tier;
                Services.Logger.Instance.Info("[NavBar] 底栏玻璃已启用（快照裁切 + BlurEffect）");
                Services.Logger.Instance.Info("[NavBar] 渲染层级 Tier = " + tier);

                // Tier 0 的逐像素着色器是 CPU 实现，先问系统到底支持不支持 ——
                // 不支持时强行开只会得到错误或空白的渲染结果（"3D 不正常"的老机器/虚拟机正是这一类）。
                bool swShaderOk = System.Windows.Media.RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0);   // 旧 API IsShaderEffectSoftwareRenderingSupported 已过时
                bool hwPs2 = System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0);
                Services.Logger.Instance.Info(string.Format(
                    "[NavBar] 着色器能力: 软件渲染支持={0}, 硬件PS2.0={1}, 进程渲染模式={2}",
                    swShaderOk, hwPs2, System.Windows.Media.RenderOptions.ProcessRenderMode));

                // 液态玻璃折射层：受设置开关 + 渲染层级闸门控制，
                // 因为 Tier==0 是纯软件渲染，逐像素着色器在老机器上可能拖慢拖动窗口。
                bool wantLens = _settings != null && _settings.NavBarLiquidGlass;
                if (tier == 0 && !_settings.NavBarLiquidGlassForceOnTier0)
                {
                    wantLens = false;
                }
                if (tier == 0 && !swShaderOk)
                {
                    // 软件渲染 + 系统不支持软件着色器：勾了"强制启用"也只是白开，直接关掉并在状态行说明
                    if (wantLens) Services.Logger.Instance.Warn("[NavBar] Tier0 且系统不支持软件着色器, 折射强制关闭");
                    wantLens = false;
                }
                ApplyNavBarLens(wantLens, tier == 0 ? (swShaderOk ? "Tier0 默认关闭" : "Tier0 不支持软件着色器") : "设置允许");
                UpdateNavButtonBackdrops();   // 折射开关状态变了，按钮磨砂要跟着挂/摘
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[NavBar] 底栏玻璃初始化失败", ex);
                if (NavBarGlass != null) NavBarGlass.Effect = null;
            }
        }

        /// <summary>
        /// 挂上/摘掉液态玻璃折射层，并按当前尺寸刷新它的几何参数。
        /// </summary>
        private void ApplyNavBarLens(bool enable, string reason)
        {
            if (NavBarLens == null) return;

            try
            {
                if (!enable)
                {
                    NavBarLens.Effect = null;
                    navLensActive = false;
                    Services.Logger.Instance.Info("[NavBar] 液态玻璃: 关闭 (" + reason + ")");
                    return;
                }

                if (NavBarLensFx == null)
                {
                    Services.Logger.Instance.Warn("[NavBar] 液态玻璃: 未找到 NavBarLensFx，跳过");
                    return;
                }

                if (navLensActive && NavBarLens.Effect == NavBarLensFx)
                {
                    UpdateNavBarLensParams();
                    return;
                }

                if (!NavBarLensFx.IsShaderLoaded)
                {
                    navLensActive = false;
                    Services.Logger.Instance.Warn("[NavBar] 液态玻璃: 着色器未加载成功, 保持普通模糊");
                    return;
                }

                NavBarLens.Effect = NavBarLensFx;
                navLensActive = true;
                UpdateNavBarLensParams();
                Services.Logger.Instance.Info("[NavBar] 液态玻璃: 已启用 (" + reason + ")");
            }
            catch (Exception ex)
            {
                navLensActive = false;
                NavBarLens.Effect = null;
                Services.Logger.Instance.Error("[NavBar] 液态玻璃启用失败, 已回退普通模糊", ex);
            }

            UpdateNavLensStatusText();
        }

        /// <summary>
        /// 把底栏的实际几何换算成着色器参数（全部用设备像素，DPI 无关）。
        /// 关键：玻璃圆角矩形 = 去掉外扩边距后的那个矩形；折射只发生在它边缘的内侧。
        /// </summary>
        private void UpdateNavBarLensParams()
        {
            if (!navLensActive || NavBarLensFx == null || NavBarLens == null || NavBarClipRoot == null) return;

            try
            {
                // 设备像素换算（100% 缩放时 scale = 1）
                double scale = 1.0;
                var src = System.Windows.PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    scale = src.CompositionTarget.TransformToDevice.M11;
                }
                if (scale <= 0) scale = 1.0;

                double lensW = NavBarLens.ActualWidth * scale;      // 外扩后的元素尺寸(px)
                double lensH = NavBarLens.ActualHeight * scale;
                double barW = NavBarClipRoot.ActualWidth * scale;   // 玻璃本体的尺寸(px)
                double barH = NavBarClipRoot.ActualHeight * scale;
                if (lensW <= 1 || lensH <= 1 || barW <= 1 || barH <= 1) return;

                double strength = _settings != null ? _settings.NavBarLiquidGlassStrength : 0.35;
                if (strength < 0) strength = 0;
                if (strength > 1) strength = 1;

                NavBarLensFx.TextureSize = new System.Windows.Point(lensW, lensH);
                NavBarLensFx.GlassHalf = new System.Windows.Point(barW * 0.5, barH * 0.5);
                NavBarLensFx.GlassRadius = 23.0 * scale;
                // 折射带宽 ≈ 21px，强度随设置线性放大（最大 16px 向内位移）
                NavBarLensFx.EdgeWidth = 21.0 * scale;
                NavBarLensFx.RefractStrength = 16.0 * scale * strength;
                // 色散用绝对像素量: 1.2px 的 R/B 分离在边缘才看得出彩边（比例写法只有 0.14px，看不见）
                NavBarLensFx.AberrationPx = 1.2 * scale;
                NavBarLensFx.RimBoost = 0.12;
                // 圆角附加折射（参考实现的 cornerBoost）：按基础折射量的一半给圆角"加料"，
                // 于是强度滑块整体缩放时，圆角凸起始终与基础折射保持比例
                NavBarLensFx.CornerBoostPx = NavBarLensFx.RefractStrength * 0.5;
                NavBarLensFx.CornerFalloff = 20.0 * scale;
                Services.Logger.Instance.Debug(string.Format(
                    "[NavBar] 圆角附加折射: 加料={0:F1}px, 影响范围={1:F1}px",
                    NavBarLensFx.CornerBoostPx, NavBarLensFx.CornerFalloff));
                NavBarLensFx.Strength = strength > 0 ? 1.0 : 0.0;

                Services.Logger.Instance.Debug(string.Format(
                    "[NavBar] 液态玻璃参数: 元素={0:F0}x{1:F0}px, 玻璃={2:F0}x{3:F0}px, 半径={4:F1}px, 折射={5:F1}px, 强度={6:F2}, scale={7:F2}",
                    lensW, lensH, barW, barH, NavBarLensFx.GlassRadius, NavBarLensFx.RefractStrength, strength, scale));
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 液态玻璃参数刷新失败: " + ex.Message);
            }
        }

        /// <summary>更新底栏背景：VisualBrush取主程序背景（BackdropContainer）的底栏区域</summary>
        private void UpdateNavBarBackdrop()
        {
            if (NavBarGlassBrush == null || NavBarGlass == null || BackdropContainer == null) return;
            double w = NavBarGlass.ActualWidth;
            double h = NavBarGlass.ActualHeight;
            if (w <= 0 || h <= 0) return;

            try
            {
                // 底栏在BackdropContainer坐标系中的矩形（与主窗口GlassyLayer同一套换算）
                Point p = NavBarGlass.TranslatePoint(new Point(0, 0), BackdropContainer);
                NavBarGlassBrush.ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NavBarGlassBrush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
                UpdateNavIndicatorBackdrop();
                UpdateNavButtonBackdrops();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新底栏背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 更新指示器的"嵌套玻璃"采样区域。
        /// 指示器会左右滑动、宽度也随按钮变化，所以必须在动画过程中持续更新 Viewbox；
        /// 否则背后的内容会跟着指示器一起走 —— 那就成了贴纸，不是玻璃。
        /// </summary>
        private void UpdateNavIndicatorBackdrop()
        {
            if (NavIndicatorBrush == null || NavIndicator == null || BackdropContainer == null) return;

            try
            {
                double w = NavIndicator.ActualWidth;
                double h = NavIndicator.ActualHeight;
                if (w <= 0 || h <= 0) return;

                Point p = NavIndicator.TranslatePoint(new Point(0, 0), BackdropContainer);
                NavIndicatorBrush.ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NavIndicatorBrush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新指示器背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 给底栏每个按钮铺一层"它自己的磨砂玻璃"。
        ///
        /// 背景：按钮模板里原本就有个 BlurEffect，但它作用在**纯色**背景上 —— 模糊纯色还是纯色，
        /// 所以除了把胶囊边缘糊软之外没有任何模糊效果（这就是"加了模糊却看不出"的原因）。
        /// 要让按钮真的比底栏更"霜"，必须让按钮背景去采样桌面原图再模糊。
        ///
        /// VisualBrush 由代码 new 出来、而不是写在模板里：模板里的 Freezable 是否按实例克隆并不可靠，
        /// 万一被共享，所有按钮会互相抢 Viewbox（全部显示同一块背景）。
        /// </summary>
        private void UpdateNavButtonBackdrops()
        {
            if (BackdropContainer == null || NavStackPanel == null) return;

            // 只在底栏折射层真的在跑时才铺按钮磨砂：这一层是"叠在折射玻璃上的嵌套玻璃"，
            // 折射被关掉时（Tier 0 软件渲染 / 用户关掉开关）再叠一层会让按钮和底栏质感分家（按钮像贴纸）；
            // 而且软件渲染下每个模糊层都走 CPU，11 个按钮各一个模糊会明显拖慢合成。
            if (!navLensActive)
            {
                DetachNavButtonBackdrops();
                return;
            }

            try
            {
                foreach (object child in NavStackPanel.Children)
                {
                    var btn = child as System.Windows.Controls.Button;
                    if (btn == null) continue;

                    System.Windows.Media.VisualBrush brush;
                    if (!_navBtnGlassBrushes.TryGetValue(btn, out brush))
                    {
                        btn.ApplyTemplate();
                        var host = (btn.Template != null)
                            ? btn.Template.FindName("glassBlur", btn) as System.Windows.Controls.Border
                            : null;
                        if (host == null) continue;

                        brush = new System.Windows.Media.VisualBrush
                        {
                            Visual = BackdropContainer,
                            Stretch = System.Windows.Media.Stretch.Fill,
                            ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                            Viewbox = new System.Windows.Rect(0, 0, 1, 1)
                        };
                        host.Background = brush;
                        _navBtnGlassBrushes[btn] = brush;
                    }

                    double w = btn.ActualWidth, h = btn.ActualHeight;
                    if (w <= 0 || h <= 0) continue;

                    Point p = btn.TranslatePoint(new Point(0, 0), BackdropContainer);
                    brush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新按钮磨砂背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 摘掉按钮磨砂层：折射关闭时让按钮与底栏质感保持一致，
        /// 同时省掉软件渲染(Tier 0)下 11 个按钮各一份 CPU 模糊的开销。
        /// </summary>
        private void DetachNavButtonBackdrops()
        {
            if (_navBtnGlassBrushes.Count == 0) return;

            foreach (var kv in _navBtnGlassBrushes)
            {
                try
                {
                    var btn = kv.Key;
                    var host = (btn.Template != null)
                        ? btn.Template.FindName("glassBlur", btn) as System.Windows.Controls.Border
                        : null;
                    if (host != null) host.Background = null;
                }
                catch { }
            }
            _navBtnGlassBrushes.Clear();
        }
        /// <summary>
        /// 更新底栏圆角几何（几何操作，极廉价）。
        /// 描边(Path)与裁剪(Clip)用同一个生成器算出，保证拐角处完全对齐；
        /// 是否使用"超椭圆"由设置决定（false 则退化为普通圆角矩形）。
        /// </summary>
        private void UpdateNavBarClip()
        {
            if (NavBarClipRoot == null) return;
            double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;

            bool squircle = _settings == null || _settings.NavBarSquircle;
            double ext = _settings != null ? _settings.NavBarSquircleExtension : 1.2819;

            // 内层裁剪：半径 23（外圆角 24 - 描边 1，同心）
            NavBarClipRoot.Clip = Effects.SquircleGeometry.Create(w, h, 23.0, squircle, ext);

            // 外层描边：与外层同尺寸（比裁剪根大 1px），半径 24
            if (NavBarFramePath != null)
            {
                double fw = NavBarFramePath.ActualWidth, fh = NavBarFramePath.ActualHeight;
                if (fw > 0 && fh > 0)
                {
                    NavBarFramePath.Data = Effects.SquircleGeometry.Create(fw, fh, 24.0, squircle, ext);
                }
            }
        }

        // ==================== 底栏文字颜色自适应 ====================
        // 阈值推导：浅字(#F2F5F8)与深字(#1A1A22)等对比度点在背景相对亮度≈0.19
        //          → gamma空间≈0.47；本套scrim平均压暗≈31% → 原始背景阈值≈0.68
        // 迟滞带0.68/0.62：避免临界值附近来回闪烁
        private const double NavLumaToDark = 0.42;   // 漏算WallpaperLayer默认白色28%+WhiteOverlay 27.8%，原0.68有误
        private const double NavLumaToLight = 0.36;  // 迟滞下限

        private bool _navUseDarkText = false;
        private byte[] _navLumaBuffer;
        private bool navLensActive = false;   // 液态玻璃折射层当前是否挂着
        private int navLensTier = 0;          // 记录渲染层级，便于设置变更时重新判定
        private System.Windows.Media.SolidColorBrush _navFgBrush;
        private System.Windows.Media.GradientStop _navIndTop, _navIndBottom;

        /// <summary>只执行一次：抓取资源引用（必须在InitializeComponent之后）</summary>
        private void InitNavBarTextTheme()
        {
            try
            {
                var res = System.Windows.Application.Current.Resources;

                // 文字画刷：冻结则Clone出可变副本
                System.Windows.Media.SolidColorBrush brush = null;
                if (res.Contains("NavForegroundBrush"))
                    brush = res["NavForegroundBrush"] as System.Windows.Media.SolidColorBrush;

                if (brush == null)
                {
                    brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xF8));
                }
                else if (brush.IsFrozen)
                {
                    brush = brush.Clone();
                }

                // ⚠️ 这里【绝对不要】写 res["NavForegroundBrush"] = brush;
                //
                // 实测结论(本机用 PowerShell + WPF 复现，两步对照实验):
                //   新建画刷                                        -> IsFrozen = False
                //   放进普通 ResourceDictionary                     -> IsFrozen = False
                //   放进 Application.Resources                      -> IsFrozen = **True**  ← 会自动冻结
                //   把 Clone() 出来的可变副本写进 Application.Resources -> IsFrozen = **True**  ← 又被冻住
                // 所以原先那句 `res["NavForegroundBrush"] = brush;` 会把刚 Clone 出来的可变副本
                // 立刻重新冻住，于是下面给按钮设的本地值也是冻结对象，
                // UpdateNavBarTextTheme 里的 BeginAnimation 必然抛
                //   "无法在 System.Windows.Media.SolidColorBrush 上激活 Color 属性，因为该对象已密封或已冻结"
                // （32 位 VM 实测日志里就是：初始化时 fg冻结=True，之后每次刷新都报"文字动画失败"，
                //   "本次配色未生效，保持原状态待下次重试" 重试了 9 次全部失败。）
                //
                // 11 个导航按钮用的是本地值，优先级高于 Style Setter 里的
                // {DynamicResource NavForegroundBrush}，所以既不需要、也不应该去改资源字典。

                // 关键：Style密封后Setter里的画刷已被冻结，必须直接给按钮设本地值
                // 本地值优先级高于Style Setter，不会被冻结
                var navButtons = new System.Windows.Controls.Button[] {
                    NavQuick, NavSetting, NavCustom, NavUdpAttack, NavChat,
                    NavScreenshot, NavTeacherSim, NavGames, NavHelp, NavDebug, NavAbout
                };
                foreach (var btn in navButtons)
                {
                    if (btn != null) btn.Foreground = brush;
                }
                _navFgBrush = brush;

                // 兜底：万一将来又被冻结(例如有人把画刷重新塞回资源字典)，再换一份可变副本。
                // 不入任何资源字典的 Clone 不会被自动冻结，可以安全动画。
                if (_navFgBrush.IsFrozen)
                {
                    Services.Logger.Instance.Warn("[NavBar] 文字画刷仍为冻结状态，已改用可变副本");
                    _navFgBrush = _navFgBrush.Clone();
                    foreach (var btn in navButtons)
                    {
                        if (btn != null) btn.Foreground = _navFgBrush;
                    }
                }

                // 滑动指示器渐变：同理
                if (NavIndicator != null)
                {
                    var lg = NavIndicator.Background as System.Windows.Media.LinearGradientBrush;
                    if (lg != null && lg.GradientStops.Count >= 2)
                    {
                        if (lg.IsFrozen)
                        {
                            lg = lg.Clone();
                            NavIndicator.Background = lg;
                        }
                        _navIndTop = lg.GradientStops[0];
                        _navIndBottom = lg.GradientStops[1];
                    }
                }

                Services.Logger.Instance.Info(
                    $"[NavBar] 文字主题初始化: fg冻结={_navFgBrush.IsFrozen}, " +
                    $"指示器={(_navIndTop != null ? "已取得" : "未取得")}");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[NavBar] 文字主题初始化失败", ex);
            }
        }

        /// <summary>从已冻结的桌面截图求底栏区域平均luma（Rec.601，0~1），零分配</summary>
        private bool TryComputeNavBarLuma(out double luma)
        {
            luma = 1.0;
            var snap = Services.ScreenCaptureHelper.FullScreenSnapshot;
            if (snap == null || NavBarGlass == null) return false;
            if (NavBarGlass.ActualWidth <= 0 || NavBarGlass.ActualHeight <= 0) return false;

            try
            {
                System.Windows.Point onScreen = this.PointToScreen(NavBarGlass.TranslatePoint(new System.Windows.Point(0, 0), this));
                int x = (int)System.Math.Round(onScreen.X - Services.ScreenCaptureHelper.VirtualScreenX);
                int y = (int)System.Math.Round(onScreen.Y - Services.ScreenCaptureHelper.VirtualScreenY);
                int w = (int)System.Math.Round(NavBarGlass.ActualWidth);
                int h = (int)System.Math.Round(NavBarGlass.ActualHeight);

                x = System.Math.Max(0, System.Math.Min(x, snap.PixelWidth - 1));
                y = System.Math.Max(0, System.Math.Min(y, snap.PixelHeight - 1));
                w = System.Math.Max(1, System.Math.Min(w, snap.PixelWidth - x));
                h = System.Math.Max(1, System.Math.Min(h, snap.PixelHeight - y));

                int bytesPerPixel = (snap.Format.BitsPerPixel + 7) / 8;
                int stride = w * bytesPerPixel;
                int need = stride * h;
                if (_navLumaBuffer == null || _navLumaBuffer.Length < need)
                    _navLumaBuffer = new byte[need];

                snap.CopyPixels(new System.Windows.Int32Rect(x, y, w, h), _navLumaBuffer, stride, 0);

                int step = (w * h > 40000) ? 2 : 1;
                long sum = 0; int count = 0;
                for (int row = 0; row < h; row += step)
                {
                    int rowOff = row * stride;
                    for (int col = 0; col < w; col += step)
                    {
                        int i = rowOff + col * bytesPerPixel;
                        byte b = _navLumaBuffer[i];
                        byte g = _navLumaBuffer[i + 1];
                        byte r = _navLumaBuffer[i + 2];
                        sum += (77 * r + 150 * g + 29 * b) >> 8;
                        count++;
                    }
                }
                if (count == 0) return false;
                luma = (sum / (double)count) / 255.0;
                return true;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 亮度计算失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>按亮度切换底栏文字/指示器配色（迟滞+180ms平滑过渡）</summary>
        private void UpdateNavBarTextTheme()
        {
            double luma;
            if (!TryComputeNavBarLuma(out luma)) return;

            bool wantDark = _navUseDarkText ? (luma > NavLumaToLight) : (luma > NavLumaToDark);
            if (wantDark == _navUseDarkText) return;

            System.Windows.Media.Color fg = wantDark
                ? System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x22)
                : System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xF8);
            System.Windows.Media.Color indTop = wantDark
                ? System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF);
            System.Windows.Media.Color indBottom = wantDark
                ? System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.TimeSpan dur = System.TimeSpan.FromMilliseconds(180);

            // 文字：动画失败退回直接赋值
            bool fgOk = false;
            if (_navFgBrush != null)
            {
                try
                {
                    _navFgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(fg, dur) { EasingFunction = ease });
                    fgOk = true;
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Warn("[NavBar] 文字动画失败，退回直接赋值: " + ex.Message);
                    try
                    {
                        if (!_navFgBrush.IsFrozen) { _navFgBrush.Color = fg; fgOk = true; }
                    }
                    catch (Exception ex2)
                    {
                        Services.Logger.Instance.Error("[NavBar] 文字配色赋值失败", ex2);
                    }
                }
            }

            // 指示器两个GradientStop，各自独立try
            try
            {
                if (_navIndTop != null)
                    _navIndTop.BeginAnimation(System.Windows.Media.GradientStop.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(indTop, dur) { EasingFunction = ease });
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("[NavBar] 指示器上色失败: " + ex.Message); }

            try
            {
                if (_navIndBottom != null)
                    _navIndBottom.BeginAnimation(System.Windows.Media.GradientStop.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(indBottom, dur) { EasingFunction = ease });
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("[NavBar] 指示器下色失败: " + ex.Message); }

            // 只有真正切成功才推进状态；失败保持原值，下次刷新自动重试
            if (fgOk)
            {
                _navUseDarkText = wantDark;
                Services.Logger.Instance.Debug(
                    $"[NavBar] 文字配色切换 -> {(wantDark ? "深色" : "浅色")} (luma={luma:F3})");
            }
            else
            {
                Services.Logger.Instance.Warn(
                    $"[NavBar] 本次配色未生效，保持原状态待下次重试 (luma={luma:F3}, wantDark={wantDark})");
            }
        }


        /// <summary>
        /// 首次桌面截图就绪后把窗口淡入（只做一次）。
        /// 这样"窗口可见"与"玻璃有背景可显示"两个时刻就重合了，不会再出现开窗瞬间的白板帧。
        /// </summary>
        private void RevealWindowAfterBackdrop()
        {
            if (_windowRevealed) return;
            _windowRevealed = true;
            try
            {
                if (_glassyManager != null) _glassyManager.BackdropUpdated -= RevealWindowAfterBackdrop;
                Services.Logger.Instance.Info("[启动] 首次桌面截图就绪 -> 窗口淡入");
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                anim.Completed += (s, e) =>
                {
                    try { this.BeginAnimation(UIElement.OpacityProperty, null); this.Opacity = 1.0; } catch { }
                };
                this.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[启动] 窗口淡入失败: " + ex.Message);
                try { this.Opacity = 1.0; } catch { }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("窗口加载完成，初始化毛玻璃效果管理器");
            try
            {
                _glassyManager = new Effects.GlassyWindowManager(this, BackdropLayer, GlassyLayer);
                Services.Logger.Instance.Info("毛玻璃效果管理器初始化成功");
                // 确保模糊强度默认值生效（Slider ValueChanged在_glassyManager初始化前触发）
                if (SliderBlurIntensity != null)
                {
                    _glassyManager.BlurIntensity = SliderBlurIntensity.Value;
                    Services.Logger.Instance.Debug($"毛玻璃模糊强度已同步: {SliderBlurIntensity.Value:F2}");
                }
                InitWallpaper();
                // 桌面截图刷新时同步底栏背景
                _glassyManager.BackdropUpdated += () => { UpdateNavBarBackdrop(); UpdateNavBarTextTheme(); };
                // 首次桌面截图就绪后再让窗口显形：否则开窗瞬间所有玻璃层都没有背景图，
                // 会先看到约 200ms 的"无背景白板"再跳成玻璃。
                _glassyManager.BackdropUpdated += RevealWindowAfterBackdrop;
                // 管理器在构造过程中可能就把首帧快照拍好了（那次 BackdropUpdated 早于本次订阅），
                // 这时必须立刻显形，否则窗口要白等到兜底计时器才出现。
                if (Services.ScreenCaptureHelper.FullScreenSnapshot != null)
                {
                    UpdateNavBarBackdrop();
                    UpdateNavBarTextTheme();
                    RevealWindowAfterBackdrop();
                }
                // 初始化底栏玻璃效果（必须在UpdateNavBarGlass之前）
                InitNavBarGlass();
                InitNavBarTextTheme();
                UpdateNavIndicatorBackdrop();
                UpdateNavButtonBackdrops();
                InitNoiseLayer();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("毛玻璃效果管理器初始化失败", ex);
                RevealWindowAfterBackdrop();   // 初始化失败也必须显形，不能留下一个看不见的窗口
            }
            // 初始化导航指示器位置到第一个按钮
            try
            {
                if (NavQuick != null && NavIndicator != null)
                {
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AnimateNavIndicator(NavQuick);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("导航指示器初始化失败: " + ex.Message);
            }

            // 底栏液态玻璃自检（供自动化/无头验证使用；平时不触发）
            //   用法: 设置环境变量 JYKILLER_NAVLENS_SELFTEST=1 后启动本程序，
            //   加载完成后会自动跑一次自检并把结果写进日志，同时导出两张对比 PNG。
            try
            {
                if (Environment.GetEnvironmentVariable("JYKILLER_NAVLENS_SELFTEST") == "1")
                {
                    Services.Logger.Instance.Info("[NavLens自检] 检测到 JYKILLER_NAVLENS_SELFTEST=1, 加载后自动执行");
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { DebugNavLensReport(Environment.GetEnvironmentVariable("JYKILLER_NAVLENS_STRENGTH")); }
                        catch (Exception ex2) { Services.Logger.Instance.Error("[NavLens自检] 自动执行失败", ex2); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavLens自检] 触发检查失败: " + ex.Message);
            }
        }
        #endregion

        #region 窗口控制

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 仅允许拖动窗口，不响应双击最大化（窗口固定大小不可最大化）
            DragMove();
        }


        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关闭(隐藏到托盘)", "BtnClose");
            // 参考原项目逻辑：关闭按钮不退出程序，而是隐藏到托盘
            HideToTray();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 参考原项目 WM_CLOSE: return TRUE 的逻辑
            // 除非是真正的退出操作，否则取消关闭，改为隐藏到托盘
            if (!_isExiting)
            {
                Services.Logger.Instance.WindowEvent("MainWindow", "OnClosing - 取消关闭，隐藏到托盘");
                e.Cancel = true;
                HideToTray();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Services.Logger.Instance.WindowEvent("MainWindow", "OnClosed");
            if (_isExiting)
            {
                _controller.Stop();
                Services.Logger.Instance.Close();
            }
            base.OnClosed(e);
        }

        #endregion

        #region 页面导航

        private void NavSetting_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("设置", "NavSetting");
            ShowPage("settings");
        }

        private void NavDebug_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("调试", "NavDebug");
            ShowPage("debug");
            RefreshLog();
        }

        private void NavAbout_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关于", "NavAbout");
            ShowPage("about");
        }

        private void NavChat_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("小小私聊", "NavChat");
            ShowPage("chat");
        }

        private void NavScreenshot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("截图替换", "NavScreenshot");
            ShowPage("screenshot");
        }

        private void NavScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 将鼠标滚轮垂直滚动转换为横向滚动
            if (NavScrollViewer != null && e.Delta != 0)
            {
                double newOffset = NavScrollViewer.HorizontalOffset - e.Delta;
                NavScrollViewer.ScrollToHorizontalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void AnimateNavIndicator(Button targetBtn)
        {
            if (targetBtn == null || NavIndicator == null || NavIndicatorTransform == null || NavContentGrid == null) return;
            try
            {
                // 指示器和按钮同在 NavContentGrid 内，相对于 NavContentGrid 计算位置
                Point relativePoint = targetBtn.TranslatePoint(new Point(0, 0), NavContentGrid);
                double targetX = relativePoint.X;
                double targetWidth = targetBtn.ActualWidth;
                if (targetWidth <= 0) targetWidth = targetBtn.MinWidth;
                NavIndicator.Width = targetWidth;
                Services.Logger.Instance.Debug("指示器动画: 目标X=" + targetX.ToString("F1") + ", 宽度=" + targetWidth.ToString("F1") + ", 按钮=" + targetBtn.Content);
                DoubleAnimation anim = new DoubleAnimation { To = targetX, Duration = TimeSpan.FromMilliseconds(350), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                NavIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, anim);
                // 滚动到可见区域
                if (NavScrollViewer != null)
                {
                    double scrollTo = targetX - 20;
                    if (scrollTo < 0) scrollTo = 0;
                    NavScrollViewer.ScrollToHorizontalOffset(scrollTo);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("导航指示器动画失败: " + ex.Message);
            }
        }

        private void NavCustom_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("自定义设置", "NavCustom");
            ShowPage("custom");
        }

        private void NavUdpAttack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击", "NavUdpAttack");
            ShowPage("udpattack");
        }

        #region 自定义设置（毛玻璃效果）

        private void SliderBlurIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextBlurValue != null)
            {
                TextBlurValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (_glassyManager != null)
            {
                _glassyManager.BlurIntensity = e.NewValue;
                Services.Logger.Instance.Debug($"毛玻璃模糊强度调整: {e.NewValue:F2}");
            }
        }

        // ===== 底栏液态玻璃设置 =====

        /// <summary>强度滑块：实时刷新折射量</summary>
        private void SliderNavLensStrength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextNavLensValue != null)
            {
                TextNavLensValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (_settings == null) return;
            _settings.NavBarLiquidGlassStrength = e.NewValue;

            // 拖滑块时不写盘（避免频繁 IO），松手后由保存按钮统一落盘
            UpdateNavBarLensParams();
            UpdateNavLensStatusText();
            Services.Logger.Instance.Debug(string.Format("液态玻璃强度调整: {0:F2}", e.NewValue));
        }

        /// <summary>顺滑度滑块：实时重建圆角几何</summary>
        private void SliderSquircleExt_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextSquircleExtValue != null)
            {
                TextSquircleExtValue.Text = e.NewValue.ToString("F2");
            }
            if (_settings == null) return;

            _settings.NavBarSquircleExtension = e.NewValue;
            UpdateNavBarClip();
            Services.Logger.Instance.Debug(string.Format("圆角顺滑度调整: {0:F2}", e.NewValue));
        }

        /// <summary>开关 / Tier0 强制开关：重新挂载或摘除折射层</summary>
        private void NavLensSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;

            _settings.NavBarLiquidGlass = CheckNavBarLiquidGlass.IsChecked ?? false;
            _settings.NavBarLiquidGlassForceOnTier0 = CheckNavLensOnTier0.IsChecked ?? false;
            _settings.NavBarSquircle = CheckNavSquircle != null ? (CheckNavSquircle.IsChecked ?? true) : true;

            UpdateNavBarClip();   // 圆角风格变化要立刻重建几何（裁剪 + 描边共用同一套）
            ApplyNavBarLens(_settings.NavBarLiquidGlass, "用户设置");
            UpdateNavButtonBackdrops();   // 折射开关状态变了，按钮磨砂要跟着挂/摘
            UpdateNavLensStatusText();
            Services.Logger.Instance.Info(string.Format(
                "液态玻璃设置变更: 启用={0}, Tier0强制={1}, 强度={2:F2}, 超椭圆={3}",
                _settings.NavBarLiquidGlass, _settings.NavBarLiquidGlassForceOnTier0,
                _settings.NavBarLiquidGlassStrength, _settings.NavBarSquircle));
        }

        /// <summary>把当前生效状态写到界面上，避免"点了开关却不知道为什么没效果"</summary>
        private void UpdateNavLensStatusText()
        {
            if (TextNavLensStatus == null || _settings == null) return;
            try
            {
                if (!_settings.NavBarLiquidGlass)
                {
                    TextNavLensStatus.Text = "当前状态：已关闭（底栏为普通磨砂玻璃）";
                    return;
                }
                if (navLensTier == 0 && !System.Windows.Media.RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0))
                {
                    // 这种情况勾"强制启用"也没用（会被 InitNavBarGlass 的闸门挡掉），所以不要提示用户去勾
                    TextNavLensStatus.Text = "当前状态：未生效 —— 本机是软件渲染(Tier 0)且系统不支持软件着色器，折射无法开启（已自动关闭）。";
                    return;
                }
                if (navLensTier == 0 && !_settings.NavBarLiquidGlassForceOnTier0)
                {
                    TextNavLensStatus.Text = "当前状态：未生效 —— 本机是软件渲染(Tier 0)，默认关闭以避免卡顿。勾选上面的强制启用即可打开。";
                    return;
                }
                if (!navLensActive)
                {
                    TextNavLensStatus.Text = "当前状态：未生效（着色器未加载成功，请查看日志）";
                    return;
                }
                TextNavLensStatus.Text = string.Format(
                    "当前状态：已启用（Tier {0}，折射量 {1:F1}px）",
                    navLensTier, NavBarLensFx != null ? NavBarLensFx.RefractStrength : 0.0);
            }
            catch (Exception ex)
            {
                TextNavLensStatus.Text = "状态获取失败: " + ex.Message;
            }
        }

        // ===== 底栏液态动态效果：指针跟随柔光 + 点击波纹（纯 WPF，无着色器开销）=====

        /// <summary>指针在底栏上移动：柔光团带惯性跟随鼠标（线性插值，不建动画时钟）</summary>
        private void NavBarClipRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (NavBarSpecularTransform == null || NavBarFxLayer == null) return;
            try
            {
                Point p = e.GetPosition(NavBarFxLayer);

                // 垂直方向只跟 35% 并整体上偏 6px：真实光源来自上方，柔光该落在玻璃上半部，
                // 而不是跟着鼠标在 64px 高的底栏里上下晃（那看起来像探照灯而不是反光）。
                double barH = NavBarFxLayer.ActualHeight;
                double targetY = 6.0 + (p.Y - barH * 0.5) * 0.35;

                // 首次移动直接就位，避免从 (0,0) 滑过来
                if (!_navSpecularInit)
                {
                    _navSpecularX = p.X;
                    _navSpecularY = targetY;
                    _navSpecularInit = true;
                }

                _navSpecularX += (p.X - _navSpecularX) * NavSpecularLag;
                _navSpecularY += (targetY - _navSpecularY) * NavSpecularLag;

                NavBarSpecularTransform.X = _navSpecularX;
                NavBarSpecularTransform.Y = _navSpecularY;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 柔光跟随失败: " + ex.Message);
            }
        }

        private void NavBarClipRoot_MouseEnter(object sender, MouseEventArgs e)
        {
            FadeNavBarSpecular(1.0);
        }

        private void NavBarClipRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            // 复位惯性状态：再次进入时柔光直接在指针处出现，而不是从上次离开的位置滑过来
            _navSpecularInit = false;
            FadeNavBarSpecular(0.0);
        }

        private void FadeNavBarSpecular(double to)
        {
            if (NavBarSpecular == null) return;
            try
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    to, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                NavBarSpecular.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 柔光淡入淡出失败: " + ex.Message);
            }
        }

        /// <summary>底栏上按下左键：从按压点扩散一圈波纹，播完自动移除</summary>
        private void NavBarClipRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (NavBarFxLayer == null) return;
            try
            {
                Point p = e.GetPosition(NavBarFxLayer);

                // 波纹几何：直径从 12px 扩散到 76px。
                // 注意不要用 ScaleTransform 做这件事 —— 缩放会连 StrokeThickness 一起放大，
                // 2px 描边在终点会变成约 8.4px，环越扩越"肥"，看起来是一团糊光而不是水波。
                // 改成动画 Width/Height + Canvas.Left/Top，描边恒定 2px；终点 76px 也避免在
                // 64px 高的底栏里被裁得太狠。
                const double startDiameter = 12;
                const double endDiameter = 76;
                var dur = TimeSpan.FromMilliseconds(430);

                var ripple = new System.Windows.Shapes.Ellipse
                {
                    Width = startDiameter,
                    Height = startDiameter,
                    StrokeThickness = 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                    Fill = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                };

                double left0 = p.X - startDiameter / 2, top0 = p.Y - startDiameter / 2;
                double left1 = p.X - endDiameter / 2, top1 = p.Y - endDiameter / 2;
                Canvas.SetLeft(ripple, left0);
                Canvas.SetTop(ripple, top0);

                // 防止狂点导致子元素无限增长（只保留柔光 + 最近 10 个波纹）
                while (NavBarFxLayer.Children.Count > 11)
                {
                    NavBarFxLayer.Children.RemoveAt(1);
                }
                NavBarFxLayer.Children.Add(ripple);

                var ease = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                };
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0.55, 0.0, dur) { EasingFunction = ease };
                fade.Completed += (s, a) =>
                {
                    try { NavBarFxLayer.Children.Remove(ripple); } catch { }
                };
                // 直接对元素 BeginAnimation：Canvas.Left/Top 虽是附加属性，但依然是 DP，可以挂动画
                // （WPF 的 Timeline 上没有 TargetProperty —— 那是 Silverlight 的写法，编译不过）
                var dW = new System.Windows.Media.Animation.DoubleAnimation(startDiameter, endDiameter, dur) { EasingFunction = ease };
                var dH = new System.Windows.Media.Animation.DoubleAnimation(startDiameter, endDiameter, dur) { EasingFunction = ease };
                var dL = new System.Windows.Media.Animation.DoubleAnimation(left0, left1, dur) { EasingFunction = ease };
                var dT = new System.Windows.Media.Animation.DoubleAnimation(top0, top1, dur) { EasingFunction = ease };
                ripple.BeginAnimation(FrameworkElement.WidthProperty, dW);
                ripple.BeginAnimation(FrameworkElement.HeightProperty, dH);
                ripple.BeginAnimation(Canvas.LeftProperty, dL);
                ripple.BeginAnimation(Canvas.TopProperty, dT);
                ripple.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 点击波纹失败: " + ex.Message);
            }
        }

        private void SliderContentOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)        {
            if (TextContentOpacityValue != null)
            {
                TextContentOpacityValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            // 正向逻辑：值越大，白色层和壁纸层越透明，桌面越可见
            byte whiteAlpha = (byte)((1 - e.NewValue) * 255);
            double wallpaperOpacity = 1 - e.NewValue;

            if (WhiteOverlayLayer != null)
            {
                SolidColorBrush whiteBrush = WhiteOverlayLayer.Background as SolidColorBrush;
                if (whiteBrush != null)
                {
                    Color c = whiteBrush.Color;
                    c.A = whiteAlpha;
                    whiteBrush.Color = c;
                }
            }

            if (WallpaperLayer != null)
            {
                WallpaperLayer.Opacity = wallpaperOpacity;
            }

            if (_settings != null)
            {
                _settings.GlassOpacity = (int)(e.NewValue * 100);
            }

            Services.Logger.Instance.Debug($"内容层透明度: {e.NewValue:F2}, whiteAlpha={whiteAlpha}, wallpaperOpacity={wallpaperOpacity:F2}");
            Services.CrashReportService.UpdateWallpaperInfo(_settings?.WallpaperPath ?? "", !string.IsNullOrEmpty(_settings?.WallpaperPath), wallpaperOpacity, whiteAlpha, (int)(e.NewValue * 100));
        }

        private void BtnRefreshGlass_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新玻璃背景", "BtnRefreshGlass");
            if (_glassyManager != null)
            {
                _glassyManager.RefreshBackdrop();
                Services.Logger.Instance.Info("毛玻璃背景已刷新");
            }
            else
            {
                Services.Logger.Instance.Warn("毛玻璃管理器未初始化");
            }
        }

        #endregion

        private void BtnAboutMe_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关于我", "BtnAboutMe");
            ShowPage("aboutme");
        }

        private void BtnBackFromAboutMe_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回(关于我)", "BtnBackFromAboutMe");
            ShowPage("about");
        }

        private void BtnGithub_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("GitHub仓库", "BtnGithub");
            try
            {
                System.Diagnostics.Process.Start("https://github.com/DaiWicked/Jiyuikiller2.0-Comprehensive-Restructuring");
                Services.Logger.Instance.Info("已打开 GitHub 仓库链接");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("打开 GitHub 链接失败", ex);
                System.Windows.MessageBox.Show("打开浏览器失败，请手动访问:\nhttps://github.com/DaiWicked/Jiyuikiller2.0-Comprehensive-Restructuring", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 软件高级设置

        private void BtnAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("软件高级设置", "BtnAdvancedSettings");
            LoadAdvancedSettingsToUI();
            ShowPage("advanced");
        }

        private void BtnBackFromAdvanced_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回(高级设置)", "BtnBackFromAdvanced");
            ShowPage("settings");
        }

        private void LoadAdvancedSettingsToUI()
        {
            Services.Logger.Instance.FunctionCall("LoadAdvancedSettingsToUI");

            CheckDisableDriver.IsChecked = _settings.DisableDriver;
            CheckSelfProtect.IsChecked = _settings.SelfProtect;
            CheckAutoForceKill.IsChecked = _settings.AutoForceKill;
            CheckAutoIncludeFullWindow.IsChecked = _settings.AutoIncludeFullWindow;
            CheckDoNotShowVirusWindow.IsChecked = _settings.DoNotShowVirusWindow;
            CheckDoNotShowTrayIcon.IsChecked = _settings.DoNotShowTrayIcon;
            CheckForceInstallInCurrentDir.IsChecked = _settings.ForceInstallInCurrentDir;
            CheckForceDisableWatchDog.IsChecked = _settings.ForceDisableWatchDog;

            CheckEnableController.IsChecked = _settings.EnableController;
            TextCKInterval.Text = _settings.CKInterval.ToString();

            // 结束进程模式
            switch (_settings.KillProcessMode)
            {
                case "TerminateProcess": RadioKillTP.IsChecked = true; break;
                case "NtTerminateProcess": RadioKillNTP.IsChecked = true; break;
                case "KernelMode": RadioKillKernel.IsChecked = true; break;
            }


            Services.Logger.Instance.Debug("高级设置已加载到 UI");
        }

        private void BtnSaveAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("保存高级设置", "BtnSaveAdvancedSettings");

            _settings.DisableDriver = CheckDisableDriver.IsChecked ?? false;
            _settings.SelfProtect = CheckSelfProtect.IsChecked ?? true;
            _settings.AutoForceKill = CheckAutoForceKill.IsChecked ?? false;
            _settings.AutoIncludeFullWindow = CheckAutoIncludeFullWindow.IsChecked ?? false;
            _settings.DoNotShowVirusWindow = CheckDoNotShowVirusWindow.IsChecked ?? true;
            _settings.DoNotShowTrayIcon = CheckDoNotShowTrayIcon.IsChecked ?? false;
            _settings.ForceInstallInCurrentDir = CheckForceInstallInCurrentDir.IsChecked ?? false;
            // D3: 立刻同步到释放服务（目录缓存会失效，下次释放按新策略走）；驱动/DLL 已释放的需重启才搬家
            Services.EmbeddedResourceService.ForceInstallInCurrentDir = _settings.ForceInstallInCurrentDir;
            Services.Logger.Instance.Info("[高级设置] 释放目录策略: " + (_settings.ForceInstallInCurrentDir ? "当前目录(exe 所在目录)" : "用户目录 %LOCALAPPDATA%"));
            _settings.ForceDisableWatchDog = CheckForceDisableWatchDog.IsChecked ?? false;

            _settings.EnableController = CheckEnableController.IsChecked ?? true;

            // 结束进程模式
            if (RadioKillTP.IsChecked == true) _settings.KillProcessMode = "TerminateProcess";
            else if (RadioKillNTP.IsChecked == true) _settings.KillProcessMode = "NtTerminateProcess";
            else if (RadioKillKernel.IsChecked == true) _settings.KillProcessMode = "KernelMode";


            // 检查间隔
            int ckInterval;
            if (int.TryParse(TextCKInterval.Text, out ckInterval))
            {
                if (ckInterval < 1000) ckInterval = 1000;
                if (ckInterval > 10000) ckInterval = 10000;
                _settings.CKInterval = ckInterval;
            }
            else
            {
                _settings.CKInterval = 3100;
            }

            _settings.Save();
            _controller.UpdateSettings(_settings);

            Services.Logger.Instance.Info($"高级设置已保存: CKInterval={_settings.CKInterval}, KillProcess={_settings.KillProcessMode}, ");
            System.Windows.MessageBox.Show("高级设置已保存！\n部分设置需要重启软件生效。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CheckDoNotShowTrayIcon_Checked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("隐藏任务栏图标", true, "CheckDoNotShowTrayIcon");
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        private void CheckDoNotShowTrayIcon_Unchecked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("隐藏任务栏图标", false, "CheckDoNotShowTrayIcon");
            if (_trayIcon != null) _trayIcon.Visible = true;
        }

        private void CheckEnableController_Checked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("启用控制器", true, "CheckEnableController");
            _controller.Start();
            // 教师端模拟服务已在构造函数中初始化, 此处不重复创建
        }

        private void CheckEnableController_Unchecked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("启用控制器", false, "CheckEnableController");
            _controller.Stop();
        }

        #endregion

        private void ShowPage(string pageName)
        {
            PageQuick.Visibility = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;
            PageAboutMe.Visibility = Visibility.Collapsed;
            PageAdvancedSettings.Visibility = Visibility.Collapsed;
            PageHelp.Visibility = Visibility.Collapsed;
            PageDebug.Visibility = Visibility.Collapsed;
            PageCustom.Visibility = Visibility.Collapsed;
            PageUdpAttack.Visibility = Visibility.Collapsed;
            PageChat.Visibility = Visibility.Collapsed;
            PageScreenshot.Visibility = Visibility.Collapsed;
            PageTeacherSim.Visibility = Visibility.Collapsed;
            PageGames.Visibility = Visibility.Collapsed;

            // 离开小游戏页面时必须清空内容：Visibility.Collapsed 不会触发 Unloaded，
            // 否则游戏定时器会继续空跑（甚至弹窗到别的页面），挂在窗口上的空格键钩子也会一直吞按键
            if (GameContent != null && GameContent.Content != null) GameContent.Content = null;

            // 重置导航按钮样式
            NavQuick.FontWeight = FontWeights.Normal;
            NavSetting.FontWeight = FontWeights.Normal;
            NavCustom.FontWeight = FontWeights.Normal;
            NavHelp.FontWeight = FontWeights.Normal;
            NavDebug.FontWeight = FontWeights.Normal;
            NavAbout.FontWeight = FontWeights.Normal;
            NavUdpAttack.FontWeight = FontWeights.Normal;
            NavChat.FontWeight = FontWeights.Normal;
            NavScreenshot.FontWeight = FontWeights.Normal;
            NavTeacherSim.FontWeight = FontWeights.Normal;
            NavGames.FontWeight = FontWeights.Normal;

            switch (pageName)
            {
                case "quick":
                    PageQuick.Visibility = Visibility.Visible;
                    NavQuick.FontWeight = FontWeights.Bold;
                    break;
                case "settings":
                    PageSettings.Visibility = Visibility.Visible;
                    NavSetting.FontWeight = FontWeights.Bold;
                    break;
                case "about":
                    PageAbout.Visibility = Visibility.Visible;
                    NavAbout.FontWeight = FontWeights.Bold;
                    break;
                case "aboutme":
                    PageAboutMe.Visibility = Visibility.Visible;
                    break;
                case "advanced":
                    PageAdvancedSettings.Visibility = Visibility.Visible;
                    break;
                case "help":
                    PageHelp.Visibility = Visibility.Visible;
                    NavHelp.FontWeight = FontWeights.Bold;
                    break;
                case "debug":
                    PageDebug.Visibility = Visibility.Visible;
                    NavDebug.FontWeight = FontWeights.Bold;
                    break;
                case "custom":
                    PageCustom.Visibility = Visibility.Visible;
                    NavCustom.FontWeight = FontWeights.Bold;
                    break;
                case "udpattack":
                    PageUdpAttack.Visibility = Visibility.Visible;
                    NavUdpAttack.FontWeight = FontWeights.Bold;
                    UpdateUdpLocalInfo();
                    RegisterUdpLog();
                    break;
                case "chat":
                    PageChat.Visibility = Visibility.Visible;
                    NavChat.FontWeight = FontWeights.Bold;
                    InitChat();
                    break;
                case "screenshot":
                    PageScreenshot.Visibility = Visibility.Visible;
                    NavScreenshot.FontWeight = FontWeights.Bold;
                    InitScreenshot();
                    break;
                case "teachersim":
                    PageTeacherSim.Visibility = Visibility.Visible;
                    NavTeacherSim.FontWeight = FontWeights.Bold;
                    InitTeacherSim();
                    break;
                case "games":
                    PageGames.Visibility = Visibility.Visible;
                    NavGames.FontWeight = FontWeights.Bold;
                    ResetGameContent();
                    break;
            }

            // 页面方向过渡（借鉴COUI NavTransition: 左右滑入+淡入, 根据导航顺序决定方向）
            FrameworkElement targetPage = null;
            int currentIndex = 0;
            switch (pageName)
            {
                case "quick": targetPage = PageQuick; currentIndex = 0; break;
                case "settings": targetPage = PageSettings; currentIndex = 1; break;
                case "custom": targetPage = PageCustom; currentIndex = 2; break;
                case "udpattack": targetPage = PageUdpAttack; currentIndex = 3; break;
                case "chat": targetPage = PageChat; currentIndex = 4; break;
                case "screenshot": targetPage = PageScreenshot; currentIndex = 5; break;
                case "teachersim": targetPage = PageTeacherSim; currentIndex = 6; break;
                case "games": targetPage = PageGames; currentIndex = 7; break;
                case "help": targetPage = PageHelp; currentIndex = 8; break;
                case "debug": targetPage = PageDebug; currentIndex = 9; break;
                case "about": targetPage = PageAbout; currentIndex = 10; break;
                case "aboutme": targetPage = PageAboutMe; currentIndex = 10; break;
                case "advanced": targetPage = PageAdvancedSettings; currentIndex = 1; break;
            }
            if (targetPage != null)
            {
                try
                {
                    // 方向: 新页面索引 > 旧页面 -> 从右滑入; 否则从左滑入
                    double fromX = (_lastPageIndex >= 0 && currentIndex < _lastPageIndex) ? -20 : 20;
                    if (_lastPageIndex < 0) fromX = 0; // 首次无方向
                    _lastPageIndex = currentIndex;

                    targetPage.Opacity = 0;
                    var trans = new System.Windows.Media.TranslateTransform(fromX, 0);
                    targetPage.RenderTransform = trans;

                    // 直接用BeginAnimation, 不用Storyboard(Storyboard无法对不在可视化树的TranslateTransform动画)
                    // 动画默认 FillBehavior=HoldEnd, 结束后属性会永久停留在"被动画接管"的状态,
                    // 之后再直接赋值 Opacity/RenderTransform 都不会生效, 因此这里在完成时把终值写回基值并摘掉动画。
                    var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
                    fadeAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    fadeAnim.Completed += (s, a) =>
                    {
                        try
                        {
                            targetPage.Opacity = 1;
                            targetPage.BeginAnimation(UIElement.OpacityProperty, null);
                        }
                        catch (Exception ex)
                        {
                            Services.Logger.Instance.Debug("页面淡入动画收尾失败: " + ex.Message);
                        }
                    };
                    targetPage.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

                    var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(280));
                    slideAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    slideAnim.Completed += (s, a) =>
                    {
                        try
                        {
                            trans.X = 0;
                            trans.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                        }
                        catch (Exception ex)
                        {
                            Services.Logger.Instance.Debug("页面滑动动画收尾失败: " + ex.Message);
                        }
                    };
                    trans.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideAnim);

                    Services.Logger.Instance.Debug($"页面方向过渡: {pageName}, fromX={fromX}");
                    Services.CrashReportService.UpdateUIState("page_transition", pageName, true, "");
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error($"页面方向过渡失败: {ex.Message}");
                    Services.CrashReportService.UpdateUIState("page_transition", pageName, false, ex.Message);
                }
            }

            Services.Logger.Instance.Debug($"页面切换: {pageName}");
        }

        #endregion

        #region 设置事件

        private void Setting_Checked(object sender, RoutedEventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb != null)
            {
                Services.Logger.Instance.CheckboxChanged(cb.Content?.ToString() ?? cb.Name, true, cb.Name);
                SaveSettingsFromUI();
                _controller.UpdateSettings(_settings);
            }
        }

        private void Setting_Unchecked(object sender, RoutedEventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb != null)
            {
                Services.Logger.Instance.CheckboxChanged(cb.Content?.ToString() ?? cb.Name, false, cb.Name);
                SaveSettingsFromUI();
                _controller.UpdateSettings(_settings);
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("保存设置", "BtnSaveSettings");
            SaveSettingsFromUI();
            _controller.UpdateSettings(_settings);
            MessageBox.Show("设置已保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("恢复默认", "BtnResetSettings");
            var result = MessageBox.Show("确定要恢复默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _settings.ResetToDefault();
                _settings.Save();
                ApplySettingsToUI();
                ApplyLiquidGlass();
                _controller.UpdateSettings(_settings);
            }
        }

        private void BtnUnloadNetFilter_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("解除网络控制", "BtnUnloadNetFilter");
            _controller.UnloadNetFilter();
        }

        private void BtnLoadDriver_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("加载内核驱动", "BtnLoadDriver");
            _controller.LoadDriver();
            UpdateDriverStatus();
        }

        private void BtnUnloadDriver_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("卸载内核驱动", "BtnUnloadDriver");
            _controller.UnloadDriver();
            UpdateDriverStatus();
        }

        private void UpdateDriverStatus()
        {
            if (_controller.IsDriverLoaded)
            {
                DriverStatusText.Text = "驱动状态: 已加载";
                DriverStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                DriverStatusText.Text = "驱动状态: 未加载";
                DriverStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
            }
        }

        private void BtnLocateJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("选择极域主进程位置", "BtnLocateJiYu");

            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择极域主进程 (StudentMain.exe)"
            };

            if (!string.IsNullOrEmpty(_settings.JiYuMainPath) && File.Exists(_settings.JiYuMainPath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(_settings.JiYuMainPath);
                dlg.FileName = Path.GetFileName(_settings.JiYuMainPath);
            }

            if (dlg.ShowDialog() == true)
            {
                _settings.JiYuMainPath = dlg.FileName;
                JiYuPathText.Text = $"极域路径: {dlg.FileName}";
                Services.Logger.Instance.Info($"已选择极域路径: {dlg.FileName}");
                MessageBox.Show("极域路径已设置，点击保存设置生效。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnReadJiYuPassword_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("读取极域密码", "BtnReadJiYuPassword");

            try
            {
                string passwd = Services.JiYuController.Instance.ReadJiYuPassword(false);

                if (!string.IsNullOrEmpty(passwd))
                {
                    MessageBox.Show("已成功读取极域密码：\n\n" + passwd, "极域密码", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("极域密码读取失败！\n\n您可以尝试万能密码：\nmythware_super_password", "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[MainWindow] 读取极域密码异常: " + ex.Message);
                MessageBox.Show("读取过程中发生异常：\n" + ex.Message + "\n\n您可以尝试万能密码：\nmythware_super_password", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #endregion

        #region 调试

        private void DebugMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.DebugMode = true;
            _settings.Save();
            Services.Logger.Instance.Enable();
            Services.Logger.Instance.Info("调试模式已开启，日志已启用");
        }

        private void DebugMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.DebugMode = false;
            _settings.Save();
            Services.Logger.Instance.Info("调试模式已关闭，即将禁用日志");
            Services.Logger.Instance.Disable();
        }

        private void BtnRefreshLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新日志", "BtnRefreshLog");
            RefreshLog();
        }

        private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("打开日志目录", "BtnOpenLogFolder");
            string logDir = AppDomain.CurrentDomain.BaseDirectory;
            Process.Start("explorer.exe", logDir);
        }

        private void RefreshLog()
        {
            try
            {
                string logPath = Services.Logger.Instance.LogPath;
                if (File.Exists(logPath))
                {
                    // 读取最后 500 行 (用FileShare.ReadWrite避免文件被占用)
                    var lines = new System.Collections.Generic.List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        while (!sr.EndOfStream)
                        {
                            lines.Add(sr.ReadLine());
                        }
                    }
                    int start = Math.Max(0, lines.Count - 500);
                    DebugLogBox.Text = string.Join("\n", lines.ToArray(), start, lines.Count - start);
                    DebugLogBox.ScrollToEnd();
                    Services.Logger.Instance.Debug($"日志已刷新，显示最后 {lines.Count - start} 行");
                }
                else
                {
                    DebugLogBox.Text = "日志文件不存在";
                }
            }
            catch (Exception ex)
            {
                DebugLogBox.Text = $"读取日志失败: {ex.Message}";
                Services.Logger.Instance.Error("读取日志失败", ex);
            }
        }

        #endregion

        #region 调试命令

        private void BtnRunCmd_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDebugCommand();
        }

        private void TextDebugCmd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteDebugCommand();
            }
        }

        private void ExecuteDebugCommand()
        {
            string cmd = TextDebugCmd.Text.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                Services.Logger.Instance.Warn("调试命令为空");
                AppendDebugOutput("[错误] 请输入命令！输入 help 查看可用命令");
                return;
            }

            Services.Logger.Instance.Info($"执行调试命令: {cmd}");
            AppendDebugOutput($"> {cmd}");

            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLower();

            try
            {
                switch (command)
                {
                    case "help":
                        AppendDebugOutput("可用命令:");
                        AppendDebugOutput("  help          - 显示帮助");
                        AppendDebugOutput("  killst        - 杀死极域进程");
                        AppendDebugOutput("  rerunst       - 重启极域进程");
                        AppendDebugOutput("  status        - 显示当前状态");
                        AppendDebugOutput("  whereisi      - 显示程序路径");
                        AppendDebugOutput("  shutdown      - 关闭计算机");
                        AppendDebugOutput("  reboot        - 重新启动计算机");
                        AppendDebugOutput("  exit          - 退出软件");
                        AppendDebugOutput("  clear         - 清空日志显示");
                        AppendDebugOutput("  refresh       - 刷新日志");
                        AppendDebugOutput("  navlens [强度] - 底栏液态玻璃自检(导出对比图并打印量化结果)");
                        break;

                    case "killst":
                        var procs = Process.GetProcessesByName("StudentMain");
                        if (procs.Length > 0)
                        {
                            foreach (var p in procs)
                            {
                                try { p.Kill(); }
                                catch { /* 进程可能已退出 */ }
                            }
                            AppendDebugOutput($"[成功] 已杀死 {procs.Length} 个极域进程");
                        }
                        else
                        {
                            AppendDebugOutput("[提示] 未找到极域进程");
                        }
                        // Process 对象持有进程句柄, 必须释放
                        foreach (var p in procs) p.Dispose();
                        UpdateJiYuStatus();
                        break;

                    case "rerunst":
                        BtnRestartJiYu_Click(null, null);
                        AppendDebugOutput("[成功] 已执行重启极域命令");
                        break;

                    case "status":
                        AppendDebugOutput($"当前状态: {StatusText.Text}");
                        AppendDebugOutput($"极域状态: {TextJiYuStatus.Text}");
                        break;

                    case "whereisi":
                        string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        AppendDebugOutput($"程序路径: {appPath}");
                        break;

                    case "shutdown":
                        AppendDebugOutput("[执行] 正在关闭计算机...");
                        BtnShutdown_Click(null, null);
                        break;

                    case "reboot":
                        AppendDebugOutput("[执行] 正在重新启动计算机...");
                        BtnReboot_Click(null, null);
                        break;

                    case "exit":
                        AppendDebugOutput("[执行] 正在退出软件...");
                        ExitApplication();
                        break;

                    case "clear":
                        DebugLogBox.Clear();
                        break;

                    case "refresh":
                        BtnRefreshLog_Click(null, null);
                        AppendDebugOutput("[成功] 日志已刷新");
                        break;

                    case "navlens":
                        DebugNavLensReport(parts.Length > 1 ? parts[1] : null);
                        break;

                    default:
                        AppendDebugOutput($"[错误] 未知命令: {command}，输入 help 查看可用命令");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendDebugOutput($"[错误] 命令执行失败: {ex.Message}");
                Services.Logger.Instance.Error($"调试命令执行失败: {cmd}", ex);
            }

            TextDebugCmd.Clear();
            TextDebugCmd.Focus();
        }

        private void AppendDebugOutput(string text)
        {
            DebugLogBox.AppendText(text + Environment.NewLine);
            DebugLogBox.ScrollToEnd();
        }

        // ========== 底栏液态玻璃自检 ==========
        // 因为无法"用眼睛看"渲染结果，这里用可量化的方式验证三件事:
        //   ① 中心区域与"关折射"的差异应 ≈ 0      -> 证明没有整片偏色（上次的坑）
        //   ② 边缘带与"关折射"的差异应明显 > 0      -> 证明真的在折射
        //   ③ 边缘带的 RGB 通道离散应 > 中心        -> 证明色散存在且只在边缘
        // 同时导出两张 PNG 供人工目视对比。

        /// <summary>等一个渲染周期，确保刚改的依赖属性已经反映到画面上</summary>
        private static void FlushRenderPass()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        private System.Windows.Media.Imaging.BitmapSource RenderNavBarToBitmap(double w, double h)
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(NavBarClipRoot);
            rtb.Freeze();
            return rtb;
        }

        private static void SaveBitmapPng(System.Windows.Media.Imaging.BitmapSource bmp, string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                {
                    enc.Save(fs);
                }
                Services.Logger.Instance.Info("[NavLens自检] 已导出: " + path);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("[NavLens自检] 导出 PNG 失败: " + ex.Message);
            }
        }

        private static byte[] ReadBgra(System.Windows.Media.Imaging.BitmapSource bmp, out int stride)
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            stride = w * 4;
            var buf = new byte[stride * h];
            bmp.CopyPixels(buf, stride, 0);
            return buf;
        }

        /// <summary>两个位图在指定矩形内的平均通道绝对差（0~255）</summary>
        private static double MeanAbsDiff(byte[] a, byte[] b, int stride, int x0, int y0, int x1, int y1)
        {
            long sum = 0; long n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    sum += Math.Abs(a[i] - b[i]);         // B
                    sum += Math.Abs(a[i + 1] - b[i + 1]); // G
                    sum += Math.Abs(a[i + 2] - b[i + 2]); // R
                    n += 3;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>位图在指定矩形内的平均通道离散度 (|R-G| + |G-B|)/2 —— 用于度量色散</summary>
        private static double MeanChannelSpread(byte[] px, int stride, int x0, int y0, int x1, int y1)
        {
            long sum = 0; long n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    sum += (Math.Abs(r - g) + Math.Abs(g - b)) / 2;
                    n++;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>
        /// 在指定通道上，找把 on 对齐到 off 所需的水平位移（整数像素）。
        /// 返回 argmin_d Σ|on(x+d) - off(x)|。
        /// 若 on(x) = off(x+k)（即采样点被偏移了 k），则最佳 d = -k。
        /// </summary>
        private static int BestHorizontalShift(byte[] on, byte[] off, int stride,
            int chOffset, int x0, int y0, int x1, int y1, int maxShift)
        {
            int bestD = 0;
            long bestCost = long.MaxValue;
            for (int d = -maxShift; d <= maxShift; d++)
            {
                long cost = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int xs = x + d;
                        if (xs < 0 || xs >= (stride / 4)) continue;
                        cost += Math.Abs(on[row + xs * 4 + chOffset] - off[row + x * 4 + chOffset]);
                    }
                }
                if (cost < bestCost) { bestCost = cost; bestD = d; }
            }
            return bestD;
        }

        /// <summary>
        /// 合成测试背景：一个"非周期"的灰阶台阶（在元素宽度约 6.5% 处从黑跳到白，即玻璃左边缘内侧几像素）。
        /// 为什么不用周期条纹：12px 周期配 ~5px 位移会产生周期性歧义，相关性函数变平，
        /// 实测就会顶到搜索边界（上一版就是这么测出 -10px 的假值的）。
        /// 单一台阶 + 梯度质心法可以给出稳定、可测亚像素的位移量。
        /// </summary>
        private static System.Windows.Media.Brush CreateTestStepBrush()
        {
            var lgb = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0),
                MappingMode = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox
            };
            // 台阶落在元素宽度的 5.5%~7.5%（元素=底栏+左右各24px，所以对应玻璃左边缘内侧约 1~10px）
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 0.0));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 0.055));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White, 0.075));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White, 1.0));
            return lgb;
        }

        /// <summary>
        /// 在指定通道上求 |dI/dx| 的质心（水平像素坐标）。
        /// 单一台阶时，质心就是"那条边在哪儿"，对高斯模糊鲁棒（模糊对称，不移动质心）。
        /// </summary>
        private static double GradientCentroid(byte[] px, int stride, int ch, int x0, int y0, int x1, int y1)
        {
            double num = 0, den = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1 - 1; x++)
                {
                    double g = Math.Abs(px[row + (x + 1) * 4 + ch] - px[row + x * 4 + ch]);
                    num += g * x;
                    den += g;
                }
            }
            return den <= 0 ? 0 : num / den;
        }

        /// <summary>
        /// 合成台阶图模式自检：逐通道测量"那条边移动了多少像素"。
        /// 这是唯一能"不用眼睛"确认折射方向、量级与色散的办法。
        /// </summary>
        private void DebugNavLensPatternTest(double strength)
        {
            if (NavBarGlass == null || NavBarLensFx == null || NavBarClipRoot == null)
            {
                AppendDebugOutput("[错误] 底栏未初始化");
                return;
            }

            var savedBackground = NavBarGlass.Background;
            double savedAberr = NavBarLensFx.AberrationPx;
            try
            {
                // 几何参数先按正式设置刷一次；之后【绝对不要】再调用 UpdateNavBarLensParams()，
                // 因为它会按设置把 Strength / RimBoost / RefractStrength 覆盖回去，
                // 那样 A/B 两组就变成同一种渲染、测出来永远是 0（上一版自检就是这么错的）。
                UpdateNavBarLensParams();

                NavBarGlass.Background = CreateTestStepBrush();
                FlushRenderPass();

                double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;

                // A: 关折射（只改 DP，不碰几何）
                NavBarLensFx.RimBoost = 0.0;
                NavBarLensFx.AberrationPx = 0.0;
                NavBarLensFx.Strength = 0.0;
                FlushRenderPass();
                var off = RenderNavBarToBitmap(w, h);

                // B: 纯折射（无色散、无柔光，专测位移方向与量级）
                NavBarLensFx.RefractStrength = 16.0 * strength;
                NavBarLensFx.Strength = 1.0;
                FlushRenderPass();
                var refractOnly = RenderNavBarToBitmap(w, h);

                // C: 折射 + 色散
                NavBarLensFx.AberrationPx = 1.2;
                FlushRenderPass();
                var withAberr = RenderNavBarToBitmap(w, h);

                int s1, s2, s3;
                byte[] pOff = ReadBgra(off, out s1);
                byte[] pRef = ReadBgra(refractOnly, out s2);
                byte[] pAbr = ReadBgra(withAberr, out s3);

                int iw = withAberr.PixelWidth, ih = withAberr.PixelHeight;
                int cy0 = ih / 2 - 12, cy1 = ih / 2 + 12;

                // ★ 坐标基准（很容易搞错，这里写明）：
                //   1) 台阶画在 NavBarGlass 上、按元素宽度 458 的 5.5%~7.5% 定位 => 元素 x ≈ 25~34
                //   2) 而渲染图是"被 Clip 过的 410x64"，x=0 就是玻璃左边缘（元素 x=24）
                //      => 台阶在渲染图里位于 x ≈ 1~10
                //   3) 底栏正文内容（按钮/文字）是另一层，从 x≈12 才开始
                //   所以测量窗口取 [0,14]，正好只框住台阶、不碰内容。
                int wx0 = 0, wx1 = Math.Min(iw, 14);

                double gOff = GradientCentroid(pOff, s1, 1, wx0, cy0, wx1, cy1);
                double gRef = GradientCentroid(pRef, s2, 1, wx0, cy0, wx1, cy1);
                double refractShift = gRef - gOff;   // 正=内容右移, 负=内容左移(向左=向外放大)

                // 色散：R/G/B 各自的边位置（都在"折射+色散"那张图上分别量，再互相比较）
                double cR = GradientCentroid(pAbr, s3, 2, wx0, cy0, wx1, cy1);
                double cG = GradientCentroid(pAbr, s3, 1, wx0, cy0, wx1, cy1);
                double cB = GradientCentroid(pAbr, s3, 0, wx0, cy0, wx1, cy1);

                // 中心区应当完全不位移（台阶不在中心，这里量的是"有没有意外位移"）
                double cOffMid = GradientCentroid(pOff, s1, 1, iw / 2 - 30, cy0, iw / 2 + 30, cy1);
                double cRefMid = GradientCentroid(pRef, s2, 1, iw / 2 - 30, cy0, iw / 2 + 30, cy1);

                // 理论预期：位移 = RefractStrength * edge，edge 取台阶处 depth≈2~8px 的平均
                double edgeAvg = (Math.Pow(1.0 - 2.0 / 21.0, 2) + Math.Pow(1.0 - 8.0 / 21.0, 2)) / 2.0;
                double theoShift = 16.0 * strength * edgeAvg;

                SaveBitmapPng(off, "底栏台阶_关折射.png");
                SaveBitmapPng(refractOnly, "底栏台阶_开折射.png");
                SaveBitmapPng(withAberr, "底栏台阶_折射加色散.png");

                AppendDebugOutput($"[台阶自检] 底栏 {iw}x{ih}px, 强度={strength:F2}, 台阶窗口 x∈[{wx0},{wx1}]");
                AppendDebugOutput($"[台阶自检] ① 边缘位移 = {refractShift:F2} px  (理论 -{theoShift:F1}px; 负=内容向外=透镜放大)");
                AppendDebugOutput($"[台阶自检] ② 中心位移 = {(cRefMid - cOffMid):F2} px  (期望 ≈ 0)");
                AppendDebugOutput($"[台阶自检] ③ 色散边位置 R={cR:F2}, G={cG:F2}, B={cB:F2}  -> R-G={cR - cG:F2}px, B-G={cB - cG:F2}px (期望两者反号)");

                Services.Logger.Instance.Info(string.Format(
                    "[NavLens台阶自检] 尺寸={0}x{1}, 强度={2:F2}, 边缘位移={3:F2}px(理论-{4:F2}), 中心位移={5:F2}px, 色散R-G={6:F2}px, B-G={7:F2}px",
                    iw, ih, strength, refractShift, theoShift, (cRefMid - cOffMid), (cR - cG), (cB - cG)));
            }
            catch (Exception ex)
            {
                AppendDebugOutput("[错误] 条纹自检失败: " + ex.Message);
                Services.Logger.Instance.Error("[NavLens条纹自检] 失败", ex);
            }
            finally
            {
                NavBarGlass.Background = savedBackground;
                NavBarLensFx.AberrationPx = savedAberr;
                UpdateNavBarLensParams();
                FlushRenderPass();
            }
        }

        private void DebugNavLensReport(string strengthArg)
        {
            try
            {
                if (NavBarClipRoot == null || NavBarLens == null || NavBarLensFx == null)
                {
                    AppendDebugOutput("[错误] 底栏未初始化");
                    return;
                }

                if (!string.IsNullOrEmpty(strengthArg))
                {
                    double v;
                    if (double.TryParse(strengthArg, out v) && _settings != null)
                    {
                        _settings.NavBarLiquidGlassStrength = Math.Max(0, Math.Min(1, v));
                        ApplyNavBarLens(true, "自检临时开启");
                        AppendDebugOutput($"[信息] 临时把强度设为 {_settings.NavBarLiquidGlassStrength:F2}");
                    }
                }

                var savedEffect = NavBarLens.Effect;
                bool attachedHere = false;

                // 先确保桌面截图已就绪：否则底栏背景是一片纯色，折射再怎么算也看不出变化，
                // 导出的对比图会毫无意义（上一版自检"边缘差异只有 0.9/255"就是这个原因）。
                try
                {
                    if (_glassyManager != null)
                    {
                        _glassyManager.RefreshBackdrop();
                        FlushRenderPass();
                    }
                }
                catch (Exception ex0)
                {
                    Services.Logger.Instance.Debug("[NavLens自检] 刷新背景失败: " + ex0.Message);
                }
                if (NavBarLens.Effect != NavBarLensFx)
                {
                    NavBarLens.Effect = NavBarLensFx;
                    attachedHere = true;
                    UpdateNavBarLensParams();
                }
                if (!navLensActive)
                {
                    navLensActive = true;
                    UpdateNavBarLensParams();
                }

                double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;
                if (w < 32 || h < 16)
                {
                    AppendDebugOutput($"[错误] 底栏尺寸异常: {w:F0}x{h:F0}");
                    return;
                }

                // A: 关折射（Strength=0 → 着色器退化为恒等变换，模糊层不变，是干净的对照组）
                NavBarLensFx.Strength = 0.0;
                FlushRenderPass();
                var bmpOff = RenderNavBarToBitmap(w, h);
                double savedStrength = _settings != null ? _settings.NavBarLiquidGlassStrength : 0.35;

                // B: 开折射（按设置强度）
                NavBarLensFx.Strength = savedStrength > 0 ? 1.0 : 0.0;
                UpdateNavBarLensParams();
                FlushRenderPass();
                var bmpOn = RenderNavBarToBitmap(w, h);

                int strideOff, strideOn;
                byte[] pxOff = ReadBgra(bmpOff, out strideOff);
                byte[] pxOn = ReadBgra(bmpOn, out strideOn);

                int iw = bmpOn.PixelWidth, ih = bmpOn.PixelHeight;
                int band = 24;                                   // 24px 边缘带（略大于折射带宽 21px）
                if (iw < band * 3 || ih < band * 3)
                {
                    AppendDebugOutput($"[警告] 底栏太小({iw}x{ih})，无法分区比较");
                    band = Math.Max(4, Math.Min(iw, ih) / 6);
                }

                // 中心区（四边各内缩 band）
                double centerDiff = MeanAbsDiff(pxOff, pxOn, strideOff,
                    0 + band, 0 + band, iw - band, ih - band);

                // 边缘带（四边各 band 宽，减去中心区）
                long rimSum = 0; long rimN = 0;
                Action<int, int, int, int> addRim = (x0, y0, x1, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * strideOff;
                        for (int x = x0; x < x1; x++)
                        {
                            int i = row + x * 4;
                            rimSum += Math.Abs(pxOff[i] - pxOn[i]);
                            rimSum += Math.Abs(pxOff[i + 1] - pxOn[i + 1]);
                            rimSum += Math.Abs(pxOff[i + 2] - pxOn[i + 2]);
                            rimN += 3;
                        }
                    }
                };
                addRim(0, 0, iw, band);                    // 上
                addRim(0, ih - band, iw, ih);              // 下
                addRim(0, band, band, ih - band);          // 左
                addRim(iw - band, band, iw, ih - band);    // 右
                double rimDiff = rimN == 0 ? 0 : (double)rimSum / rimN;

                SaveBitmapPng(bmpOff, "底栏对比_关折射.png");
                SaveBitmapPng(bmpOn, "底栏对比_开折射.png");

                AppendDebugOutput($"[结果] 底栏 {iw}x{ih}px, 强度={savedStrength:F2}, 边缘带宽={band}px");
                AppendDebugOutput($"[结果] ① 中心区平均差异 = {centerDiff:F3} / 255   (期望 ≈ 0，证明无整片偏色)");
                AppendDebugOutput($"[结果] ② 边缘带平均差异 = {rimDiff:F3} / 255   (期望明显 > ①，证明边缘在折射)");
                AppendDebugOutput($"[结果] 注意: ②的大小取决于「背后画面有多少细节」。背后是纯色时折射看不出来是物理必然(位移纯色仍是纯色)，");
                AppendDebugOutput($"[结果]       此时靠边缘柔光体现玻璃感。要看色散量请用台阶自检里逐通道的数字。");
                AppendDebugOutput($"[结果] 已导出 底栏对比_关折射.png / 底栏对比_开折射.png 到程序目录，可自行对比");

                Services.Logger.Instance.Info(string.Format(
                    "[NavLens自检] 尺寸={0}x{1}, 强度={2:F2}, 中心差异={3:F3}, 边缘差异={4:F3}",
                    iw, ih, savedStrength, centerDiff, rimDiff));

                // 再做一次"合成条纹图"自检：把背景换成灰阶竖条，逐通道精确测量位移与色散
                DebugNavLensPatternTest(savedStrength > 0 ? savedStrength : 0.35);

                NavBarLensFx.Strength = savedStrength > 0 ? 1.0 : 0.0;
                if (attachedHere) NavBarLens.Effect = savedEffect;
                UpdateNavBarLensParams();
            }
            catch (Exception ex)
            {
                AppendDebugOutput("[错误] 自检失败: " + ex.Message);
                Services.Logger.Instance.Error("[NavLens自检] 失败", ex);
            }
        }

        #endregion

        #region 电源控制

        private void NavQuick_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("快捷栏", "NavQuick");
            UpdateJiYuStatus();
            ShowPage("quick");
        }

        private void BtnTopMost_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("本窗口置顶", "BtnTopMost");
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hWnd = helper.Handle;

            if (_isTopMost)
            {
                _isTopMost = false;
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
                BtnTopMost.Content = "本窗口置顶";
                BtnTopMost.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                Services.Logger.Instance.Info("已取消窗口置顶");
            }
            else
            {
                _isTopMost = true;
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
                BtnTopMost.Content = "取消置顶";
                BtnTopMost.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                BtnTopMost.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                Services.Logger.Instance.Info("已设置窗口置顶");
            }
        }

        private void BtnKillJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("杀死极域", "BtnKillJiYu");
            bool result = _controller.KillJiYu();
            if (result)
            {
                System.Windows.MessageBox.Show("已成功结束极域电子教室", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("未找到极域进程或杀死失败", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateJiYuStatus();
        }

        private void BtnRestartJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("重启极域", "BtnRestartJiYu");
            bool result = _controller.RestartJiYu();
            if (result)
            {
                System.Windows.MessageBox.Show("已重启极域电子教室", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            UpdateJiYuStatus();
        }

        private void UpdateJiYuStatus()
        {
            try
            {
                var processes = Process.GetProcessesByName("StudentMain");
                try
                {
                    if (processes.Length > 0)
                    {
                        TextJiYuStatus.Text = $"极域状态: 运行中 (PID={processes[0].Id})";
                        TextJiYuStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                    }
                    else
                    {
                        TextJiYuStatus.Text = "极域状态: 未运行";
                        TextJiYuStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                    }
                }
                finally
                {
                    // 该函数在每次状态刷新时都会调用, 必须释放 Process 句柄
                    foreach (var p in processes) p.Dispose();
                }
            }
            catch
            {
                TextJiYuStatus.Text = "极域状态: 检测失败";
            }
        }

        private void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关闭计算机", "BtnShutdown");
            var result = System.Windows.MessageBox.Show("你是否真的要关闭电脑？\n\n关机前会自动停止极域控制。", "电源控制 - 警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Services.Logger.Instance.Info("用户确认关机，正在停止控制器...");
                _controller.Stop();
                Services.Logger.Instance.Info("执行关机命令: shutdown /s /t 0");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    Services.Logger.Instance.Info("关机命令已发送，正在退出软件");
                    ForceExit();
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("执行关机命令失败", ex);
                    System.Windows.MessageBox.Show("关机失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("重新启动", "BtnReboot");
            var result = System.Windows.MessageBox.Show("你是否真的要重启电脑？\n\n重启前会自动停止极域控制。", "电源控制 - 警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Services.Logger.Instance.Info("用户确认重启，正在停止控制器...");
                _controller.Stop();
                Services.Logger.Instance.Info("执行重启命令: shutdown /r /t 0");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    Services.Logger.Instance.Info("重启命令已发送，正在退出软件");
                    ForceExit();
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("执行重启命令失败", ex);
                    System.Windows.MessageBox.Show("重启失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("退出软件", "BtnExitApp");
            ExitApplication();
        }

        private void ForceExit()
        {
            Services.Logger.Instance.Info("强制退出应用程序");

            // 关键: 必须置位。OnClosing 在 _isExiting==false 时会 e.Cancel=true 并把窗口隐藏到托盘,
            // 原先这条路径没有置位, 关机/重启流程里窗口的关闭会被取消。
            _isExiting = true;

            try { _controller.Stop(); } catch { }

            // 与 ExitApplication 保持一致: 教师端模拟进程也需要停止
            try
            {
                if (_teacherSimService != null && _teacherSimService.IsRunning)
                {
                    _teacherSimService.Stop();
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("停止教师端模拟进程失败: " + ex.Message);
            }

            try { _glassyManager?.Dispose(); } catch { }

            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
            }
            catch { }
            try { Services.Logger.Instance.Close(); } catch { }
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        #region 帮助文档

        private void NavHelp_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助文档", "NavHelp");
            ShowHelpSubPage("intro");
            ShowPage("help");
        }

        private void HelpNavIntro_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-介绍", "HelpNavIntro");
            ShowHelpSubPage("intro");
        }

        private void HelpNavKey_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-快捷键", "HelpNavKey");
            ShowHelpSubPage("key");
        }

        private void HelpNavAntiMonitor_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-虚假反监视", "HelpNavAntiMonitor");
            ShowHelpSubPage("antimonitor");
        }

        private void HelpNavOthers_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-其他", "HelpNavOthers");
            ShowHelpSubPage("others");
        }

        private void HelpNavDisclaimer_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-免责声明", "HelpNavDisclaimer");
            ShowHelpSubPage("disclaimer");
        }

        private void ShowHelpSubPage(string page)
        {
            HelpContentIntro.Visibility = Visibility.Collapsed;
            HelpContentKey.Visibility = Visibility.Collapsed;
            HelpContentAntiMonitor.Visibility = Visibility.Collapsed;
            HelpContentOthers.Visibility = Visibility.Collapsed;
            HelpContentDisclaimer.Visibility = Visibility.Collapsed;

            // 重置导航按钮样式
            HelpNavIntro.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavIntro.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavKey.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavKey.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavAntiMonitor.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavAntiMonitor.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavOthers.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavOthers.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavDisclaimer.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavDisclaimer.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            switch (page)
            {
                case "intro":
                    HelpContentIntro.Visibility = Visibility.Visible;
                    HelpNavIntro.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavIntro.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "key":
                    HelpContentKey.Visibility = Visibility.Visible;
                    HelpNavKey.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavKey.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "antimonitor":
                    HelpContentAntiMonitor.Visibility = Visibility.Visible;
                    HelpNavAntiMonitor.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavAntiMonitor.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "others":
                    HelpContentOthers.Visibility = Visibility.Visible;
                    HelpNavOthers.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavOthers.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "disclaimer":
                    HelpContentDisclaimer.Visibility = Visibility.Visible;
                    HelpNavDisclaimer.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavDisclaimer.Foreground = new SolidColorBrush(Colors.White);
                    break;
            }
        }

        #endregion

        #region 状态更新

        private void UpdateStatus()
        {
            StatusText.Text = _controller.GetStatusText();
            UpdateJiYuStatus();
            UpdateDriverStatus();
            // 同步极域路径（LocateJiYuPosition可能在后台保存了路径）
            if (!string.IsNullOrEmpty(_settings.JiYuMainPath) && JiYuPathText.Text != $"极域路径: {_settings.JiYuMainPath}")
            {
                JiYuPathText.Text = $"极域路径: {_settings.JiYuMainPath}";
            }
        }

        #endregion

        #region 全局快捷键

        private void RegisterGlobalHotKeys()
        {
            Services.Logger.Instance.FunctionCall("RegisterGlobalHotKeys");

            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(HwndHook);
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // 快捷键现在可配置：编码沿用设置里既有的方式（高字节修饰键 / 低字节虚拟键码），
            // 与 Win32 RegisterHotKey 的 MOD_* 常量一致 —— WPF 的 ModifierKeys 枚举值恰好就等于 MOD_*。
            int pack1 = (_settings != null && _settings.HotKeyFakeFull != 0) ? _settings.HotKeyFakeFull : 1606;
            int pack2 = (_settings != null && _settings.HotKeyShowHide != 0) ? _settings.HotKeyShowHide : 1604;
            uint mod1 = (uint)((pack1 >> 8) & 0xF); uint vk1 = (uint)(pack1 & 0xFF);
            uint mod2 = (uint)((pack2 >> 8) & 0xF); uint vk2 = (uint)(pack2 & 0xFF);

            bool result1 = vk1 != 0 && RegisterHotKey(hwnd, HOTKEY_FAKEFULL, mod1, vk1);
            int err1 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Services.Logger.Instance.Info("[HotKey] 注册紧急全屏 " + HotKeyToText(pack1) + ": " + (vk1 == 0 ? "未设置, 跳过" : (result1 ? "成功" : "失败")));

            bool result2 = vk2 != 0 && RegisterHotKey(hwnd, HOTKEY_SHOWHIDE, mod2, vk2);
            int err2 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Services.Logger.Instance.Info("[HotKey] 注册显示/隐藏窗口 " + HotKeyToText(pack2) + ": " + (vk2 == 0 ? "未设置, 跳过" : (result2 ? "成功" : "失败")));

            // vk==0 表示用户故意留空，不该报"注册失败"
            ReportHotKeyWarning(vk1 == 0 || result1, err1, HotKeyToText(pack1) + "（紧急全屏）",
                                vk2 == 0 || result2, err2, HotKeyToText(pack2) + "（显示/隐藏窗口）");
        }

        /// <summary>
        /// 热键注册失败时在「高级设置 → 快捷键」卡片里给出可见提示。
        /// 原来只在日志里写一行 INFO，用户界面上完全看不出快捷键没生效。
        /// </summary>
        private void ReportHotKeyWarning(bool ok1, int err1, string name1, bool ok2, int err2, string name2)
        {
            if (TextHotKeyWarn == null) return;

            if (ok1 && ok2)
            {
                TextHotKeyWarn.Text = "";
                TextHotKeyWarn.Visibility = Visibility.Collapsed;
                return;
            }

            var sb = new System.Text.StringBuilder("注意：");
            if (!ok1) sb.Append(DescribeHotKeyFailure(name1, err1));
            if (!ok1 && !ok2) sb.Append("；");
            if (!ok2) sb.Append(DescribeHotKeyFailure(name2, err2));
            sb.Append("。该快捷键不会生效；多为其它程序已占用同一组合键，关闭占用它的程序后重启本软件即可。");

            TextHotKeyWarn.Text = sb.ToString();
            TextHotKeyWarn.Visibility = Visibility.Visible;
            Services.Logger.Instance.Warn("[HotKey] " + sb.ToString());
        }

        /// <summary>把 RegisterHotKey 的失败原因翻译成人话（1409 = ERROR_HOTKEY_ALREADY_REGISTERED）</summary>
        private static string DescribeHotKeyFailure(string name, int err)
        {
            string reason = (err == 1409) ? "已被其它程序占用" : (err == 0 ? "原因未知" : "系统错误码 " + err);
            return $"{name} 注册失败（{reason}）";
        }

        /// <summary>打包的快捷键编码 -> 可读文本（高字节修饰键 / 低字节虚拟键码）</summary>
        private static string HotKeyToText(int packed)
        {
            int vk = packed & 0xFF;
            if (vk == 0) return "未设置";

            int mods = (packed >> 8) & 0xF;
            var sb = new System.Text.StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & 0x8) != 0) sb.Append("Win+");

            sb.Append(HotKeyKeyName(KeyInterop.KeyFromVirtualKey(vk)));
            return sb.ToString();
        }

        private static string HotKeyKeyName(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "小键盘" + (k - Key.NumPad0);
            return k.ToString();
        }

        /// <summary>把设置里的快捷键显示到输入框（Tag 存打包值，保存时读回）</summary>
        private void ApplyHotKeysToUI()
        {
            if (_settings == null) return;
            if (TextHotKeyFakeFull != null)
            {
                TextHotKeyFakeFull.Tag = _settings.HotKeyFakeFull;
                TextHotKeyFakeFull.Text = HotKeyToText(_settings.HotKeyFakeFull);
            }
            if (TextHotKeyShowHide != null)
            {
                TextHotKeyShowHide.Tag = _settings.HotKeyShowHide;
                TextHotKeyShowHide.Text = HotKeyToText(_settings.HotKeyShowHide);
            }
        }

        /// <summary>
        /// 快捷键输入框：点进去后直接按下组合键即可。
        /// 必须带修饰键 —— 不带修饰键的全局热键会把那个键从所有程序手里抢走。
        /// </summary>
        private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;   // 不要让它变成普通文本输入
            try
            {
                Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

                // 只按修饰键本身（或 Esc）不算一个完整组合
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin ||
                    key == Key.None || key == Key.Escape)
                {
                    return;
                }

                int mods = (int)Keyboard.Modifiers & 0xF;
                if (mods == 0)
                {
                    TextHotKeyWarn.Text = "快捷键必须包含 Ctrl / Alt / Shift / Win 中的至少一个修饰键。";
                    TextHotKeyWarn.Visibility = Visibility.Visible;
                    return;
                }

                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk <= 0) return;

                var box = sender as System.Windows.Controls.TextBox;
                if (box == null) return;

                int packed = (mods << 8) | (vk & 0xFF);
                box.Tag = packed;
                box.Text = HotKeyToText(packed);

                TextHotKeyWarn.Text = "";
                TextHotKeyWarn.Visibility = Visibility.Collapsed;
                Services.Logger.Instance.Info("[HotKey] 界面选择了新快捷键: " + box.Text + "（点保存设置后立刻生效）");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[HotKey] 捕获快捷键失败: " + ex.Message);
            }
        }

        private void UnregisterGlobalHotKeys()
        {
            Services.Logger.Instance.FunctionCall("UnregisterGlobalHotKeys");

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource = null;
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_FAKEFULL);
            UnregisterHotKey(hwnd, HOTKEY_SHOWHIDE);
            Services.Logger.Instance.Info("[HotKey] 已注销全部快捷键");
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_FAKEFULL)
                {
                    Services.Logger.Instance.ButtonClick("快捷键-紧急全屏", "GlobalHotKey");
                    bool result = _controller.SwitchFakeFull();
                    Services.Logger.Instance.Info("[HotKey] 紧急全屏切换: " + (result ? "已全屏" : "已恢复"));
                    handled = true;
                }
                else if (id == HOTKEY_SHOWHIDE)
                {
                    Services.Logger.Instance.ButtonClick("快捷键-显示/隐藏", "GlobalHotKey");
                    if (this.Visibility == Visibility.Visible)
                    {
                        HideToTray();
                    }
                    else
                    {
                        ShowMainWindow();
                    }
                    handled = true;
                }
            }
            else if (msg == WM_COPYDATA)
            {
                // 接收DLL回调消息 (对应原项目VSendMessageBack)
                try
                {
                    COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                    if (cds.lpData != IntPtr.Zero)
                    {
                        string message = Marshal.PtrToStringUni(cds.lpData);
                        if (!string.IsNullOrEmpty(message))
                        {
                            Services.Logger.Instance.Info("[DLL回调] " + message);
                            HandleDllCallback(message);
                        }
                    }
                    handled = true;
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("[DLL回调] 处理WM_COPYDATA异常", ex);
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 处理DLL回调消息 (对应原项目hkb:系列消息)
        /// </summary>
        /// <summary>
        /// 处理DLL回调消息 - 薄转发到JiYuController.HandleVirusCallback
        /// 真正的窗口操作在Controller中执行（对应参考实现 HandleMessageFromVirus）
        /// </summary>
        private void HandleDllCallback(string message)
        {
            try
            {
                _controller.HandleVirusCallback(message);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[DLL回调] HandleDllCallback异常", ex);
            }
        }

        #endregion

        #region UDP攻击

        private void UpdateUdpLocalInfo()
        {
            try
            {
                var svc = Services.UdpAttackService.Instance;
                string ip = svc.GetLocalIP();
                Services.Logger.Instance.Info($"UDP攻击-本机IP: {ip}");
                if (string.IsNullOrEmpty(ip))
                {
                    TextUdpLocalInfo.Text = "本机: 无网络";
                    return;
                }
                // 先显示IP
                TextUdpLocalInfo.Text = $"本机: ... - {ip}";
                // 异步获取MAC
                Task.Run(() =>
                {
                    string mac = "未知";
                    try
                    {
                        // 遍历网卡找匹配IP的MAC
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                            var props = ni.GetIPProperties();
                            foreach (var ua in props.UnicastAddresses)
                            {
                                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    Services.Logger.Instance.Debug($"网卡: {ni.Name} IP={ua.Address} MAC={ni.GetPhysicalAddress()}");
                                    if (ua.Address.ToString() == ip)
                                    {
                                        byte[] macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                                        if (macBytes.Length >= 6)
                                        {
                                            mac = BitConverter.ToString(macBytes, 0, 6);
                                        }
                                        break;
                                    }
                                }
                            }
                            if (mac != "未知") break;
                        }
                        // 如果没找到，用SendARP
                        if (mac == "未知")
                        {
                            mac = svc.GetMacAddress(ip);
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.Logger.Instance.Error("获取本机MAC异常: " + ex.Message);
                    }
                    Services.Logger.Instance.Info($"UDP攻击-本机MAC: {mac}");
                    Dispatcher.Invoke(() =>
                    {
                        TextUdpLocalInfo.Text = $"本机: {mac} - {ip}";
                    });
                });
            }
            catch (Exception ex)
            {
                TextUdpLocalInfo.Text = "本机: 获取失败";
                Services.Logger.Instance.Error("获取本机信息失败: " + ex.Message);
            }
        }

        private bool _udpLogRegistered = false;
        private void RegisterUdpLog()
        {
            if (!_udpLogRegistered)
            {
                var svc = Services.UdpAttackService.Instance;
                svc.OnLog += (msg) => Dispatcher.Invoke(() => TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\n"));
                svc.OnSendResult += (success, msg) => Dispatcher.Invoke(() =>
                {
                    if (success)
                    {
                        System.Windows.MessageBox.Show(msg, "发送成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(msg, "发送失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    }
                });
                _udpLogRegistered = true;
            }
        }

        private void BtnBackFromUdpAttack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-返回", "BtnBackFromUdpAttack");
            ShowPage("quick");
        }

        private void BtnScanLan_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-扫描局域网", "BtnScanLan");
            RegisterUdpLog();
            var svc = Services.UdpAttackService.Instance;
            svc.OnScanComplete += (hosts) => Dispatcher.Invoke(() =>
            {
                ListUdpScanResult.Items.Clear();
                foreach (var host in hosts)
                {
                    ListUdpScanResult.Items.Add(host);
                }
                TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + $" 扫描完成，发现 {hosts.Count} 台主机，点击列表选择目标\n");
            });
            svc.ScanNetwork();
        }

        private void ListUdpScanResult_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListUdpScanResult.SelectedItem is Services.NetworkHost host)
            {
                TextUdpTargetIp.Text = host.IP;
                TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " 已选择目标: " + host.ToString() + "\n");
                Services.Logger.Instance.ButtonClick("UDP攻击-选择目标", "ListUdpScanResult");
            }
        }

        private void BtnUdpSendMsg_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-发送消息", "BtnUdpSendMsg");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendText(ip, 4705, TextUdpMessage.Text);
        }

        private void BtnUdpSendCmd_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-发送命令", "BtnUdpSendCmd");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendCommand(ip, 4705, TextUdpMessage.Text);
        }

        private void BtnUdpQuickCmd_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            string cmd = btn.Tag.ToString();
            TextUdpMessage.Text = cmd;
            Services.Logger.Instance.ButtonClick("UDP攻击-快捷指令", cmd);
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendCommand(ip, 4705, cmd);
        }
        private void BtnUdpShutdown_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-远程关机", "BtnUdpShutdown");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendShutdown(ip, 4705);
        }

        private void BtnUdpReboot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-远程重启", "BtnUdpReboot");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendReboot(ip, 4705);
        }


        private void BtnUdpClearLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-清空日志", "BtnUdpClearLog");
            TextUdpLog.Text = "日志已清空\n";
        }
        #endregion

        #region 小小私聊

        private bool _chatInitialized = false;

        private void InitChat()
        {
            if (!_chatInitialized)
            {
                _chatService.OnLog += (msg) => Dispatcher.Invoke(() => { TextChatLocalInfo.Text = msg; });
                _chatService.OnChatRecord += (msg) => Dispatcher.Invoke(() => { TextChatLog.AppendText(msg + "\n"); TextChatLog.ScrollToEnd(); });
                _chatService.OnSendResult += (success, msg) => Dispatcher.Invoke(() => { System.Windows.MessageBox.Show(msg, success ? "发送成功" : "发送失败"); });
                _chatInitialized = true;
                Services.Logger.Instance.Info("[Chat] 小小私聊事件注册完成");
            }
            TextChatLog.Clear();
            TextChatLog.AppendText("~~ 欢迎使用小小私聊 ~~\n");
            TextChatLog.AppendText("原理：极域学生端不对UDP包做身份验证，可构造数据包发送消息。\n");
            TextChatLog.AppendText("提示：座位号换算算法移植自jiyu_chat，按6人一排布局推算。\n");
            _chatService.InitLocalInfo();
        }

        private async void BtnChatFind_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Chat] 点击查找同学");
            if (!int.TryParse(TextChatTargetSeat.Text, out int seatID) || seatID <= 0)
            {
                TextChatTargetStatus.Text = "请输入有效的座位号！";
                TextChatLog.AppendText("请输入有效的座位号！\n");
                return;
            }
            TextChatTargetStatus.Text = "查找中...";
            var result = await _chatService.FindClassmate(seatID);
            TextChatTargetStatus.Text = result.Item3;
            if (result.Item1)
            {
                _chatTargetIP = result.Item2;
                _chatTargetSeat = seatID;
            }
        }

        private async void BtnChatSend_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Chat] 点击发送消息");
            await _chatService.SendMessage(_chatTargetIP, TextChatMessage.Text, _chatTargetSeat);
            TextChatMessage.Clear();
        }

        private void BtnChatClear_Click(object sender, RoutedEventArgs e)
        {
            TextChatLog.Clear();
            Services.Logger.Instance.Info("[Chat] 清空聊天记录");
        }

        private void BtnChatBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion

        #region 截图替换

        private bool _screenshotInitialized = false;
        private string _screenshotTempPath = "";

        private void InitScreenshot()
        {
            if (!_screenshotInitialized)
            {
                _screenshotService.OnLog += (msg) => Services.Logger.Instance.Info("[Screenshot] " + msg);
                _screenshotService.OnStatusChanged += (msg) => Dispatcher.Invoke(() => { TextScreenshotState.Text = msg; });
                _screenshotInitialized = true;
                Services.Logger.Instance.Info("[Screenshot] 截图替换事件注册完成");
            }
            _screenshotService.LoadCurrent();
            UpdateScreenshotPreview();
            InitRealtimeReplace();
        }

        private void UpdateScreenshotPreview()
        {
            var img = _screenshotService.LoadPreviewImage();
            ImgScreenshotPreview.Source = img;
            TextScreenshotPath.Text = string.IsNullOrEmpty(_screenshotService.CurrentImagePath) ? "（未设置）" : _screenshotService.CurrentImagePath;
            UpdateScreenshotState();
        }

        /// <summary>
        /// 更新截图替换状态显示
        /// </summary>
        private void UpdateScreenshotState()
        {
            if (!string.IsNullOrEmpty(_screenshotService.CurrentImagePath) && System.IO.File.Exists(_screenshotService.CurrentImagePath))
            {
                TextScreenshotState.Text = "已替换";
                TextScreenshotState.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                ScreenshotStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextScreenshotState.Text = "未替换";
                TextScreenshotState.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                ScreenshotStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
        }

        private void BtnScreenshotChoose_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击选择图片");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于替换屏幕截图的图片",
                Filter = "图片文件(*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _screenshotTempPath = dlg.FileName;
                _screenshotService.ChooseImage(dlg.FileName);
                UpdateScreenshotPreview();
            }
        }

        private void BtnScreenshotClear_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击清除图片");
            _screenshotTempPath = "";
            _screenshotService.ClearImage();
            UpdateScreenshotPreview();
        }

        private void BtnScreenshotApply_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击应用");
            bool ok = _screenshotService.Apply(_controller);
            UpdateScreenshotPreview();
            System.Windows.MessageBox.Show(ok ? "应用成功" : "应用失败", "截图替换");
        }

        private void BtnScreenshotCancel_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击取消");
            _screenshotService.LoadCurrent();
            UpdateScreenshotPreview();
        }

        private void BtnScreenshotCancelReplace_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击取消替换");
            _screenshotService.ClearImage();
            bool ok = _screenshotService.Apply(_controller);
            UpdateScreenshotPreview();
            System.Windows.MessageBox.Show(ok ? "已取消截图替换" : "取消失败", "截图替换");
        }

        private void BtnScreenshotBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion

        #region 实时屏幕替换

        private bool _realtimeInitialized = false;

        private void InitRealtimeReplace()
        {
            if (!_realtimeInitialized)
            {
                _realtimeService.OnLog += (msg) => Services.Logger.Instance.Info("[Realtime] " + msg);
                _realtimeService.OnStatusChanged += (msg) => Dispatcher.Invoke(() => { TextRealtimeStatus.Text = msg; });
                _realtimeInitialized = true;
                Services.Logger.Instance.Info("[Realtime] 实时屏幕替换事件注册完成");
            }
            _realtimeService.LoadCurrent();
            UpdateRealtimeState();
        }

        private void UpdateRealtimeState()
        {
            if (_realtimeService.IsEnabled)
            {
                TextRealtimeStatus.Text = _realtimeService.IsVideoMode
                    ? "已启用（视频循环播放）"
                    : "已启用 - 教师端观看时生效";
                TextRealtimeStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                RealtimeStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextRealtimeStatus.Text = "未启用";
                TextRealtimeStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                RealtimeStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
            // 更新路径显示
            if (_realtimeService.IsVideoMode)
            {
                TextRealtimePath.Text = "[视频] " + (_realtimeService.CurrentVideoPath ?? "");
            }
            else
            {
                TextRealtimePath.Text = string.IsNullOrEmpty(_realtimeService.CurrentImagePath)
                    ? "（未设置）"
                    : _realtimeService.CurrentImagePath;
            }
            // 图片模式立即更新预览，视频模式由选择视频时的后台线程处理
            if (!_realtimeService.IsVideoMode)
            {
                try
                {
                    ImgRealtimePreview.Source = _realtimeService.LoadPreviewImage();
                }
                catch { }
            }
        }

        private void BtnRealtimeChooseImage_Click(object sender, RoutedEventArgs e)
        {
            // 图片模式：显示Image，隐藏文字提示和视频按钮
            ImgRealtimePreview.Visibility = System.Windows.Visibility.Visible;
            TextVideoPreviewHint.Visibility = System.Windows.Visibility.Collapsed;
            GridVideoControls.Visibility = System.Windows.Visibility.Collapsed;
            InitRealtimeReplace();
            Services.Logger.Instance.Info("[Realtime] 点击选择图片");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于实时屏幕替换的图片",
                Filter = "图片文件(*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _realtimeService.ChooseImage(dlg.FileName);
                UpdateRealtimeState();
            }
        }

        private void BtnRealtimeChooseVideo_Click(object sender, RoutedEventArgs e)
        {
            InitRealtimeReplace();
            Services.Logger.Instance.Info("[Realtime] 点击选择视频");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于实时屏幕替换的视频（建议小于100MB）",
                Filter = "视频文件(*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv)|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                if (_realtimeService.ChooseVideo(dlg.FileName))
                {
                    UpdateRealtimeState();
                    // 视频模式：显示文字提示，隐藏Image
                    ImgRealtimePreview.Visibility = System.Windows.Visibility.Collapsed;
                    TextVideoPreviewHint.Visibility = System.Windows.Visibility.Visible;
                    GridVideoControls.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    System.Windows.MessageBox.Show("视频文件选择失败，请查看状态提示", "实时屏幕替换");
                }
            }
        }

        private void BtnRealtimeApply_Click(object sender, RoutedEventArgs e)
        {
            // 不调用InitRealtimeReplace()，避免LoadCurrent覆盖用户已选择的视频/图片状态
            Services.Logger.Instance.Info("[Realtime] 点击启用");
            try
            {
                bool ok = _realtimeService.Apply();
                UpdateRealtimeState();
                if (ok)
                {
                    string iniPath = @"C:\Users\Public\JiYuKiller\realtime_replace.ini";
                    string yuvPath = @"C:\Users\Public\JiYuKiller\fake_screen.yuv";
                    string mode = _realtimeService.IsVideoMode ? "视频循环播放" : "静态图片";
                    System.Windows.MessageBox.Show(
                        "实时替换已启用（" + mode + "）\n" +
                        "教师端发起实时观看时生效\n" +
                        "配置存在: " + System.IO.File.Exists(iniPath) + "\n" +
                        "YUV存在: " + System.IO.File.Exists(yuvPath),
                        "实时屏幕替换");
                }
                else
                {
                    System.Windows.MessageBox.Show("启用失败，请先选择图片或视频", "实时屏幕替换");
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[Realtime] 启用异常: " + ex);
                System.Windows.MessageBox.Show("启用异常: " + ex.Message, "实时屏幕替换");
            }
        }

        private void BtnRealtimeDisable_Click(object sender, RoutedEventArgs e)
        {
            // 不调用InitRealtimeReplace()，避免LoadCurrent覆盖状态
            Services.Logger.Instance.Info("[Realtime] 点击关闭");
            _realtimeService.Disable();
            UpdateRealtimeState();
        }

        #endregion

        #region 小游戏

        private void NavGames_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("小游戏", "NavGames");
            ShowPage("games");
        }

        /// <summary>每次进入小游戏页面都回到菜单，避免残留上一局的状态。</summary>
        private void ResetGameContent()
        {
            try
            {
                GameContent.Content = new Games.GameMenu();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("ResetGameContent 失败: " + ex.Message);
            }
        }

        #endregion

        #region 极域教师端模拟

        private void NavTeacherSim_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("教师模拟", "NavTeacherSim");
            ShowPage("teachersim");
        }

        private void InitTeacherSim()
        {
            Services.Logger.Instance.FunctionCall("InitTeacherSim");
            // 获取本机IP
            try
            {
                string localIP = Services.UdpAttackService.Instance.GetLocalIP();
                TextTeacherSimInfo.Text = $"本机IP: {localIP}，频道: {TextTeacherSimChannel.Text}";
            }
            catch
            {
                TextTeacherSimInfo.Text = "本机IP: 获取失败";
            }
            UpdateTeacherSimState();
        }

        private void UpdateTeacherSimState()
        {
            if (_teacherSimService != null && _teacherSimService.IsRunning)
            {
                TextTeacherSimStatus.Text = "运行中";
                TextTeacherSimStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                TeacherSimStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextTeacherSimStatus.Text = "未启动";
                TextTeacherSimStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                TeacherSimStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
        }

        private bool _parsingStudentList = false;

        private void TeacherSim_OnLogOutput(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TextTeacherSimConsole.AppendText(message + Environment.NewLine);
                TextTeacherSimConsole.ScrollToEnd();

                // 解析list命令输出，填充学生列表
                if (message.Contains("[命令] 已登录学生"))
                {
                    _parsingStudentList = true;
                    ListTeacherSimStudents.Items.Clear();
                }
                else if (_parsingStudentList)
                {
                    // 格式: "  1. 192.168.3.150  DESKTOP-xxx  用户:xxx  MAC:xx-xx-xx-xx-xx-xx"
                    var match = System.Text.RegularExpressions.Regex.Match(message,
                        @"^\s+\d+\.\s+(\d+\.\d+\.\d+\.\d+)\s+.*MAC:([0-9A-Fa-f\-]+)");
                    if (match.Success)
                    {
                        string ip = match.Groups[1].Value;
                        string mac = match.Groups[2].Value;
                        ListTeacherSimStudents.Items.Add(ip + "  " + mac);
                    }
                    else if (message.StartsWith("teacher>") || message.Contains("[命令]") || string.IsNullOrWhiteSpace(message))
                    {
                        _parsingStudentList = false;
                    }
                }
            });
        }
        private void TeacherSim_OnLogFileOutput(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TextTeacherSimLog.AppendText(message + Environment.NewLine);
                TextTeacherSimLog.ScrollToEnd();
            });
        }

        private void BtnTeacherSimClearLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("清空日志", "BtnTeacherSimClearLog");
            TextTeacherSimLog.Clear();
        }

        private void BtnTeacherSimRefreshList_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新学生列表", "BtnTeacherSimRefreshList");
            if (_teacherSimService != null && _teacherSimService.IsRunning)
            {
                _teacherSimService.SendCommand("list");
            }
        }

        private void ListTeacherSimStudents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListTeacherSimStudents.SelectedItem != null)
            {
                string item = ListTeacherSimStudents.SelectedItem.ToString();
                string[] parts = item.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    TextTeacherSimTargetIP.Text = parts[0];
                }
            }
        }


        private void TeacherSim_OnStateChanged(bool isRunning)
        {
            Dispatcher.Invoke(() => UpdateTeacherSimState());
        }

        /// <summary>
        /// 检测到网络碰撞时弹出确认框
        /// 用户选"继续"则以--skip-collision参数重新启动，避免循环弹窗
        /// </summary>
        private void TeacherSim_OnCollisionDetected(string collisionInfo)
        {
            Dispatcher.Invoke(() =>
            {
                string msg = "检测到局域网内已有其他教师端运行：\n\n" + collisionInfo +
                    "\n\n同时使用可能导致学生端无法连接或网络风暴。\n\n是否仍要继续启动？";
                var result = System.Windows.MessageBox.Show(msg, "网络碰撞警告",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    _teacherSimService.SkipCollisionCheck = true;
                    if (int.TryParse(TextTeacherSimChannel.Text, out int channel))
                    {
                        _teacherSimService.Channel = channel;
                    }
                    _teacherSimService.Start();
                    UpdateTeacherSimState();
                }
                else
                {
                    _teacherSimService.SkipCollisionCheck = false;
                    UpdateTeacherSimState();
                }
            });
        }

        private void BtnTeacherSimStart_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("启动模拟", "BtnTeacherSimStart");
            if (int.TryParse(TextTeacherSimChannel.Text, out int channel))
            {
                _teacherSimService.Channel = channel;
            }
            _teacherSimService.Start();
            UpdateTeacherSimState();
        }

        private void BtnTeacherSimStop_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("停止模拟", "BtnTeacherSimStop");
            _teacherSimService.Stop();
            UpdateTeacherSimState();
        }

        private void BtnTeacherSimSend_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("发送命令", "BtnTeacherSimSend");
            string cmd = TextTeacherSimCommand.Text.Trim();
            if (!string.IsNullOrEmpty(cmd))
            {
                _teacherSimService.SendCommand(cmd);
                TextTeacherSimCommand.Clear();
            }
        }

        private void TextTeacherSimCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnTeacherSimSend_Click(sender, e);
            }
        }

        private void BtnTeacherSimClear_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("清空控制台", "BtnTeacherSimClear");
            TextTeacherSimConsole.Clear();
        }

        private void BtnTeacherSimBack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回", "BtnTeacherSimBack");
            ShowPage("quick");
        }

        private void BtnTeacherSimQuickList_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("list");
        }

        private void BtnTeacherSimQuickAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("all");
        }

        private void BtnTeacherSimQuickBlackAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("bsall");
        }

        private void BtnTeacherSimQuickUnlockAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("unlock_all");
        }

        private void BtnTeacherSimQuickShutdownAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要关闭所有已登录学生机吗？", "确认全体关机", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand("sdall");
            }
        }

        private void BtnTeacherSimQuickRebootAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要重启所有已登录学生机吗？", "确认全体重启", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand("rball");
            }
        }

        private void BtnTeacherSimQuickHelp_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("help");
        }


        private void BtnTeacherSimQuickMsg_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            TextTeacherSimCommand.Text = $"msg {ip} ";
            TextTeacherSimCommand.Focus();
        }

        private void BtnTeacherSimQuickMsgAll_Click(object sender, RoutedEventArgs e)
        {
            string msg = Microsoft.VisualBasic.Interaction.InputBox("请输入要群发的消息内容：", "群发消息", "", -1, -1);
            if (string.IsNullOrWhiteSpace(msg)) return;
            _teacherSimService.SendCommand("msgall " + msg);
        }
        private void BtnTeacherSimQuickBlack_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"bs {ip}");
        }

        private void BtnTeacherSimQuickUnlock_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"unlock {ip}");
        }

        private void BtnTeacherSimQuickShutdown_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            if (MessageBox.Show($"确定要关闭 {ip} 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand($"shutdown {ip}");
            }
        }

        private void BtnTeacherSimQuickReboot_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            if (MessageBox.Show($"确定要重启 {ip} 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand($"reboot {ip}");
            }
        }

        private void BtnTeacherSimQuickPreview_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"preview {ip}");
        }

        private void BtnTeacherSimQuickView_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"view {ip}");
        }

        private void BtnTeacherSimQuickViewStop_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"view_stop {ip}");
        }

        private void BtnTeacherSimQuickInfo_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"info {ip}");
        }
        #endregion

        #region 自定义壁纸

        private Services.WallpaperService _wallpaperService = new Services.WallpaperService();
        private string _pendingWallpaperPath = "";

        private void BtnWallpaperSelect_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("选择壁纸", "BtnWallpaperSelect");
            try
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Title = "选择壁纸图片";
                dlg.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (dlg.ShowDialog() == true)
                {
                    _pendingWallpaperPath = dlg.FileName;
                    if (_wallpaperService.ValidateWallpaper(dlg.FileName))
                    {
                        BitmapSource thumb = _wallpaperService.GetThumbnail(dlg.FileName);
                        if (thumb != null) ImageWallpaperPreview.Source = thumb;
                        TextWallpaperError.Text = "";
                    }
                    else { TextWallpaperError.Text = _wallpaperService.LastError; }
                }
            }
            catch (Exception ex) { TextWallpaperError.Text = "选择失败: " + ex.Message; }
        }

        private void BtnWallpaperApply_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("应用壁纸", "BtnWallpaperApply");
            string path = !string.IsNullOrEmpty(_pendingWallpaperPath) ? _pendingWallpaperPath : _settings.WallpaperPath;
            if (string.IsNullOrEmpty(path)) { TextWallpaperError.Text = "请先选择壁纸"; return; }
            ApplyWallpaper(path);
        }

        private void BtnWallpaperReset_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("恢复默认", "BtnWallpaperReset");
            _settings.WallpaperPath = "";
            _pendingWallpaperPath = "";
            _settings.Save();
            ImageWallpaperPreview.Source = null;
            TextWallpaperError.Text = "";
            if (WallpaperLayer != null)
            {
                WallpaperLayer.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            }
            Services.CrashReportService.UpdateWallpaperInfo("", false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
        }

        private void ApplyWallpaper(string path)
        {
            try
            {
                BitmapSource wallpaper = _wallpaperService.LoadWallpaper(path);
                if (wallpaper == null)
                {
                    TextWallpaperError.Text = _wallpaperService.LastError;
                    Services.CrashReportService.UpdateWallpaperInfo(path, false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
                    return;
                }
                if (WallpaperLayer != null)
                {
                    WallpaperLayer.Background = new ImageBrush(wallpaper) { Stretch = Stretch.Fill };
                }
                _settings.WallpaperPath = path;
                _settings.Save();
                TextWallpaperError.Text = "";
                Services.CrashReportService.UpdateWallpaperInfo(path, true, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
            }
            catch (Exception ex)
            {
                TextWallpaperError.Text = "应用失败: " + ex.Message;
                Services.CrashReportService.UpdateWallpaperInfo(path, false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
            }
        }

        /// <summary>
        /// 初始化噪声层（借鉴COUI noiseCoefficient=0.0045, 抗色带）
        /// 程序化生成128x128灰度噪声纹理, ImageBrush平铺
        /// </summary>
        private void InitNoiseLayer()
        {
            try
            {
                const int size = 128;
                var bmp = new System.Windows.Media.Imaging.WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Gray8, null);
                var pixels = new byte[size * size];
                var rand = new Random(42); // 固定种子保证一致
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (byte)rand.Next(0, 256);
                bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size, 0);
                var brush = new System.Windows.Media.ImageBrush(bmp);
                brush.TileMode = System.Windows.Media.TileMode.Tile;
                brush.Viewport = new System.Windows.Rect(0, 0, size, size);
                brush.ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NoiseLayer.Background = brush;
                Services.Logger.Instance.Debug("噪声层初始化成功, 128x128平铺");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("噪声层初始化失败", ex);
            }
        }

        private void InitWallpaper()
        {
            if (!string.IsNullOrEmpty(_settings.WallpaperPath))
            {
                if (_wallpaperService.ValidateWallpaper(_settings.WallpaperPath))
                {
                    BitmapSource thumb = _wallpaperService.GetThumbnail(_settings.WallpaperPath);
                    if (thumb != null) ImageWallpaperPreview.Source = thumb;
                    ApplyWallpaper(_settings.WallpaperPath);
                }
                else { TextWallpaperError.Text = "壁纸文件已丢失，请重新选择或恢复默认"; }
            }
        }

        #endregion

        #region 彩蛋

        /// <summary>
        /// 版本号点击 - 点击3次触发彩蛋视频
        /// </summary>
        private void AboutVersion_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_eggShowing)
            {
                Services.Logger.Instance.Debug("[彩蛋] 彩蛋已显示, 忽略点击");
                return;
            }
            _eggClickCount++;
            Services.Logger.Instance.Debug($"彩蛋点击: {_eggClickCount}/3");
            if (_eggClickCount >= 3)
            {
                _eggClickCount = 0;
                ShowEggVideo();
            }
        }

        /// <summary>
        /// 显示彩蛋视频 - 从嵌入资源释放到临时目录并播放
        /// </summary>
        private void ShowEggVideo()
        {
            try
            {
                _eggShowing = true;

                // 文件只释放一次, 避免重复IO
                if (!_eggExtracted || !System.IO.File.Exists(_eggTempPath))
                {
                    // 与驱动/DLL 使用同一个释放目录, 避免在多处硬编码临时路径
                    string tempDir = Services.EmbeddedResourceService.TempDir;
                    System.IO.Directory.CreateDirectory(tempDir);
                    _eggTempPath = System.IO.Path.Combine(tempDir, "egg.mp4");

                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    string resourceName = null;
                    foreach (var name in assembly.GetManifestResourceNames())
                    {
                        if (name.EndsWith("egg.mp4"))
                        {
                            resourceName = name;
                            break;
                        }
                    }

                    if (resourceName == null)
                    {
                        Services.Logger.Instance.Error("[彩蛋] 未找到嵌入资源 egg.mp4");
                        _eggShowing = false;
                        return;
                    }

                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        using (var file = new System.IO.FileStream(_eggTempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            stream.CopyTo(file);
                        }
                    }
                    _eggExtracted = true;
                    Services.Logger.Instance.Info($"[彩蛋] 视频已释放: {_eggTempPath}");
                }
                else
                {
                    Services.Logger.Instance.Debug("[彩蛋] 视频已存在, 跳过释放");
                }

                EggWindow.Visibility = System.Windows.Visibility.Visible;
                // 上一次播放失败时可能把视频元素隐藏了, 这里恢复
                EggMedia.Visibility = System.Windows.Visibility.Visible;
                // 必须显式 Absolute: 相对 Uri 会让 MediaElement 无法定位到本地文件
                EggMedia.Source = new System.Uri(_eggTempPath, System.UriKind.Absolute);
                EggMedia.Play();
                Services.Logger.Instance.Info("[彩蛋] 视频开始播放");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[彩蛋] 播放失败", ex);
                _eggShowing = false;
            }
        }

        /// <summary>
        /// 彩蛋视频播放失败时的回退处理。
        /// 播放失败时 MediaElement 只留一个黑框, 之前没有任何提示或日志。
        /// </summary>
        private void EggMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string reason = e != null && e.ErrorException != null
                ? e.ErrorException.Message
                : "(未知错误)";

            Services.Logger.Instance.Error(
                "[彩蛋] 视频播放失败, 文件: " + _eggTempPath + ", 原因: " + reason +
                "。常见原因: 目标机器缺少 H.264 解码器(Win7 N/KN 版无 Media Feature Pack)或本窗口为分层窗口(AllowsTransparency=True)导致视频无法合成。");

            try
            {
                // 失败时给出可读提示, 不要留一个纯黑窗口
                EggMedia.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 关闭彩蛋窗口
        /// </summary>
        private void EggClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EggMedia.Stop();
                EggMedia.Source = null;
                EggWindow.Visibility = System.Windows.Visibility.Collapsed;
                _eggShowing = false;
                _eggClickCount = 0;
                Services.Logger.Instance.Info("[彩蛋] 窗口已关闭, 状态已重置");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[彩蛋] 关闭失败", ex);
                _eggShowing = false;
            }
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


