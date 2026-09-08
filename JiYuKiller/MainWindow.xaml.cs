using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private WinForms.NotifyIcon _trayIcon;
        private bool _hideTipShown = false;
        private bool _isExiting = false;
        private bool _isTopMost = false;
        private Effects.GlassyWindowManager _glassyManager;

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

            // 初始化毛玻璃效果管理器（窗口加载后）
            this.Loaded += MainWindow_Loaded;

            // 初始化系统托盘
            InitTrayIcon();

            // 启动监控
            _controller.Start();

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
            // 使用绝对路径加载图标，避免工作目录变化导致找不到
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "JiYuTrainerLogo.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _trayIcon.Icon = new Drawing.Icon(iconPath);
                Services.Logger.Instance.Debug($"托盘图标已加载: {iconPath}");
            }
            else
            {
                // 回退：使用系统默认图标
                _trayIcon.Icon = Drawing.SystemIcons.Application;
                Services.Logger.Instance.Warn($"图标文件不存在，使用系统默认图标: {iconPath}");
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
            _controller.Stop();
            Services.Logger.Instance.Close();
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        #region 设置加载/保存

        private void ApplySettingsToUI()
        {
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
        }

        private void SaveSettingsFromUI()
        {
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
            Services.Logger.Instance.FunctionCall("ApplyLiquidGlass", $"opacity={_settings.GlassOpacity}, color={_settings.GlassBgColor}");

            // 根据背景色设置内容层透明度
            byte alpha = (byte)(_settings.GlassOpacity * 2.55);

            // 内容层背景色
            Color bgColor;
            switch (_settings.GlassBgColor.ToLower())
            {
                case "white":
                    bgColor = Color.FromRgb(255, 255, 255);
                    break;
                case "blue":
                    bgColor = Color.FromRgb(180, 210, 255);
                    break;
                case "gray":
                    bgColor = Color.FromRgb(200, 200, 200);
                    break;
                case "dark":
                    bgColor = Color.FromRgb(50, 50, 50);
                    break;
                case "purple":
                    bgColor = Color.FromRgb(200, 180, 255);
                    break;
                case "green":
                    bgColor = Color.FromRgb(180, 230, 180);
                    break;
                default:
                    bgColor = Color.FromRgb(255, 255, 255);
                    break;
            }
            bgColor.A = alpha;

            // 设置内容层背景（第三个Grid）
            // 注意：毛玻璃效果由 GlassyWindowManager 管理桌面截图和像素着色器
            Services.Logger.Instance.Debug($"Liquid Glass 已应用: ARGB={bgColor.A},{bgColor.R},{bgColor.G},{bgColor.B}");
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("窗口加载完成，初始化毛玻璃效果管理器");
            try
            {
                _glassyManager = new Effects.GlassyWindowManager(this, BackdropLayer, GlassyLayer);
                Services.Logger.Instance.Info("毛玻璃效果管理器初始化成功");
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

        private void NavGeek_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("极客工具", "NavGeek");
            ShowPage("geek");
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

            // 调整内容层背景透明度
            if (ContentLayer != null)
            {
                byte alpha = (byte)(e.NewValue * 255);
                SolidColorBrush brush = ContentLayer.Background as SolidColorBrush;
                if (brush != null)
                {
                    Color color = brush.Color;
                    color.A = alpha;
                    brush.Color = color;
                }
                else
                {
                    ContentLayer.Background = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
                }
            }

            Services.Logger.Instance.Debug($"内容层透明度调整: {e.NewValue:F2}");
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
            Services.Logger.Instance.Debug($"外层光晕滑块事件触发: NewValue={e.NewValue:F2}, OuterGlow null={OuterGlow == null}");

            if (TextOuterGlowValue != null)
            {
                TextOuterGlowValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (OuterGlow != null)
            {
                double oldOpacity = OuterGlow.Opacity;
                OuterGlow.Opacity = e.NewValue;
                Services.Logger.Instance.Debug($"外层光晕透明度: {oldOpacity:F2} -> {e.NewValue:F2}");
            }
        }

        private void SliderInnerGlow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Services.Logger.Instance.Debug($"中层光晕滑块事件触发: NewValue={e.NewValue:F2}, InnerGlow null={InnerGlow == null}");

            if (TextInnerGlowValue != null)
            {
                TextInnerGlowValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (InnerGlow != null)
            {
                double oldOpacity = InnerGlow.Opacity;
                InnerGlow.Opacity = e.NewValue;
                Services.Logger.Instance.Debug($"中层光晕透明度: {oldOpacity:F2} -> {e.NewValue:F2}");
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
            PageGeek.Visibility = Visibility.Collapsed;

            // 重置导航按钮样式
            NavQuick.FontWeight = FontWeights.Normal;
            NavSetting.FontWeight = FontWeights.Normal;
            NavCustom.FontWeight = FontWeights.Normal;
            NavHelp.FontWeight = FontWeights.Normal;
            NavDebug.FontWeight = FontWeights.Normal;
            NavAbout.FontWeight = FontWeights.Normal;
            NavGeek.FontWeight = FontWeights.Normal;

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

        #endregion

        #region 调试

        private void DebugMode_Checked(object sender, RoutedEventArgs e)
        {
            _settings.DebugMode = true;
            Services.Logger.Instance.Info("调试模式已开启");
        }

        private void DebugMode_Unchecked(object sender, RoutedEventArgs e)
        {
            _settings.DebugMode = false;
            Services.Logger.Instance.Info("调试模式已关闭");
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
                    // 读取最后 500 行
                    var lines = File.ReadAllLines(logPath);
                    int start = Math.Max(0, lines.Length - 500);
                    DebugLogBox.Text = string.Join("\n", lines, start, lines.Length - start);
                    DebugLogBox.ScrollToEnd();
                    Services.Logger.Instance.Debug($"日志已刷新，显示最后 {lines.Length - start} 行");
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
            HelpContentOthers.Visibility = Visibility.Collapsed;
            HelpContentDisclaimer.Visibility = Visibility.Collapsed;

            // 重置导航按钮样式
            HelpNavIntro.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavIntro.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavKey.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavKey.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
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
            return IntPtr.Zero;
        }

        #endregion
    }
}
