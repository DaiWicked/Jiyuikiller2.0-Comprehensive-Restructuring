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
        private int _chatTargetSeat = 0;

        // Win32 API
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;

        // 全局快捷键 API
        [DllImport("user32.dll")]
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
            NavBarClipRoot.SizeChanged += (s, e) => { UpdateNavBarClip(); UpdateNavBarBackdrop(); UpdateNavBarTextTheme(); };
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

            // 默认显示快捷栏
            ShowPage("quick");
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

            CheckMonitorProcess.IsChecked = _settings.MonitorJiYuProcess;
            CheckBanRunOp.IsChecked = _settings.BanJiYuRunOp;
            CheckAllowTop.IsChecked = _settings.AllowGbTop;
            CheckProhibitKill.IsChecked = _settings.ProhibitKillProcess;
            CheckAllowMonitor.IsChecked = _settings.AllowMonitor;
            CheckProhibitClose.IsChecked = _settings.ProhibitCloseWindow;
            CheckAllowControl.IsChecked = _settings.AllowControl;
            CheckDebugMode.IsChecked = _settings.DebugMode;

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

            _settings.MonitorJiYuProcess = CheckMonitorProcess.IsChecked ?? false;
            _settings.BanJiYuRunOp = CheckBanRunOp.IsChecked ?? false;
            _settings.AllowGbTop = CheckAllowTop.IsChecked ?? false;
            _settings.ProhibitKillProcess = CheckProhibitKill.IsChecked ?? false;
            _settings.AllowMonitor = CheckAllowMonitor.IsChecked ?? false;
            _settings.ProhibitCloseWindow = CheckProhibitClose.IsChecked ?? false;
            _settings.AllowControl = CheckAllowControl.IsChecked ?? false;
            _settings.DebugMode = CheckDebugMode.IsChecked ?? false;

            _settings.Save();
            _controller.UpdateSettings(_settings);

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

        /// <summary>初始化：BlurEffect，无着色器</summary>
        private void InitNavBarGlass()
        {
            try
            {
                NavBarGlass.Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = 5,
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian
                };
                Services.Logger.Instance.Info("[NavBar] 底栏玻璃已启用（快照裁切 + BlurEffect，无着色器）");
                Services.Logger.Instance.Info("[NavBar] 渲染层级 Tier = " + (System.Windows.Media.RenderCapability.Tier >> 16));
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[NavBar] 底栏玻璃初始化失败", ex);
                NavBarGlass.Effect = null;
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
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新底栏背景失败: " + ex.Message);
            }
        }

        /// <summary>只更新底栏圆角裁剪（几何操作，极廉价）</summary>
        private void UpdateNavBarClip()
        {
            if (NavBarClipRoot == null) return;
            double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var geo = NavBarClipRoot.Clip as System.Windows.Media.RectangleGeometry;
            if (geo == null)
            {
                geo = new System.Windows.Media.RectangleGeometry();
                NavBarClipRoot.Clip = geo;
            }
            geo.Rect = new System.Windows.Rect(0, 0, w, h);
            geo.RadiusX = 23;
            geo.RadiusY = 23;
        }

        // ==================== 底栏文字颜色自适应 ====================
        // 阈值推导：浅字(#F2F5F8)与深字(#1A1A22)等对比度点在背景相对亮度≈0.19
        //          → gamma空间≈0.47；本套scrim平均压暗≈31% → 原始背景阈值≈0.68
        // 迟滞带0.68/0.62：避免临界值附近来回闪烁
        private const double NavLumaToDark = 0.42;   // 漏算WallpaperLayer默认白色28%+WhiteOverlay 27.8%，原0.68有误
        private const double NavLumaToLight = 0.36;  // 迟滞下限

        private bool _navUseDarkText = false;
        private byte[] _navLumaBuffer;
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
                res["NavForegroundBrush"] = brush;
                _navFgBrush = brush;

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
                // 初始化底栏玻璃效果（必须在UpdateNavBarGlass之前）
                InitNavBarGlass();
                InitNavBarTextTheme();
                InitNoiseLayer();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("毛玻璃效果管理器初始化失败", ex);
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

        private void SliderContentOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
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

        private void SliderOuterGlow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OuterGlow != null)
            {
                OuterGlow.Opacity = e.NewValue;
            }
            if (TextOuterGlowValue != null)
            {
                TextOuterGlowValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }
        }

        private void SliderInnerGlow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (InnerGlow != null)
            {
                InnerGlow.Opacity = e.NewValue;
            }
            if (TextInnerGlowValue != null)
            {
                TextInnerGlowValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
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
            CheckInjectMasterHelper.IsChecked = _settings.InjectMasterHelper;
            CheckEnableController.IsChecked = _settings.EnableController;
            TextCKInterval.Text = _settings.CKInterval.ToString();

            // 结束进程模式
            switch (_settings.KillProcessMode)
            {
                case "TerminateProcess": RadioKillTP.IsChecked = true; break;
                case "NtTerminateProcess": RadioKillNTP.IsChecked = true; break;
                case "KernelMode": RadioKillKernel.IsChecked = true; break;
            }

            // 注入模式
            ComboInjectMode.SelectedIndex = _settings.InjectMode == "HookDllStub" ? 1 : 0;

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
            _settings.ForceDisableWatchDog = CheckForceDisableWatchDog.IsChecked ?? false;
            _settings.InjectMasterHelper = CheckInjectMasterHelper.IsChecked ?? false;
            _settings.EnableController = CheckEnableController.IsChecked ?? true;

            // 结束进程模式
            if (RadioKillTP.IsChecked == true) _settings.KillProcessMode = "TerminateProcess";
            else if (RadioKillNTP.IsChecked == true) _settings.KillProcessMode = "NtTerminateProcess";
            else if (RadioKillKernel.IsChecked == true) _settings.KillProcessMode = "KernelMode";

            // 注入模式
            _settings.InjectMode = ComboInjectMode.SelectedIndex == 1 ? "HookDllStub" : "RemoteThread";

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

            Services.Logger.Instance.Info($"高级设置已保存: CKInterval={_settings.CKInterval}, KillProcess={_settings.KillProcessMode}, InjectMode={_settings.InjectMode}");
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
                    var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
                    fadeAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    targetPage.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

                    var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(280));
                    slideAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
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
                        break;

                    case "killst":
                        var procs = Process.GetProcessesByName("StudentMain");
                        if (procs.Length > 0)
                        {
                            foreach (var p in procs) p.Kill();
                            AppendDebugOutput($"[成功] 已杀死 {procs.Length} 个极域进程");
                        }
                        else
                        {
                            AppendDebugOutput("[提示] 未找到极域进程");
                        }
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
            try { _controller.Stop(); } catch { }
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

            // 紧急全屏: Ctrl+Alt+F (VK_F = 0x46)
            bool result1 = RegisterHotKey(hwnd, HOTKEY_FAKEFULL, MOD_CONTROL | MOD_ALT, 0x46);
            Services.Logger.Instance.Info("[HotKey] 注册紧急全屏 Ctrl+Alt+F: " + (result1 ? "成功" : "失败"));

            // 显示/隐藏窗口: Ctrl+Alt+H (VK_H = 0x48)
            bool result2 = RegisterHotKey(hwnd, HOTKEY_SHOWHIDE, MOD_CONTROL | MOD_ALT, 0x48);
            Services.Logger.Instance.Info("[HotKey] 注册显示/隐藏 Ctrl+Alt+H: " + (result2 ? "成功" : "失败"));
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
                    string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "学习不通");
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
                EggMedia.Source = new System.Uri(_eggTempPath);
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
