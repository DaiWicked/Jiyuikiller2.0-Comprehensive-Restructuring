using System;
using System.Diagnostics;
using System.IO;
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

            // 初始化系统托盘
            InitTrayIcon();

            // 启动监控
            _controller.Start();

            // 定时更新状态
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

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
            this.Visibility = Visibility.Visible;
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            Services.Logger.Instance.Info("主窗口已显示");
        }

        private void HideToTray()
        {
            Services.Logger.Instance.FunctionCall("HideToTray");
            this.Visibility = Visibility.Collapsed;

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

            var result = System.Windows.MessageBox.Show("确定要退出学习不通吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                Services.Logger.Instance.Info("用户确认退出程序");
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _controller.Stop();
                Services.Logger.Instance.Close();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                _isExiting = false;
                Services.Logger.Instance.Info("用户取消退出");
            }
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

            // 根据背景色设置玻璃颜色
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

            // 计算透明度 (0-100 -> 0-255)
            byte alpha = (byte)(_settings.GlassOpacity * 2.55);
            bgColor.A = alpha;

            GlassBgBrush.Color = bgColor;

            Services.Logger.Instance.Debug($"Liquid Glass 已应用: ARGB={bgColor.A},{bgColor.R},{bgColor.G},{bgColor.B}");
        }

        #endregion

        #region 窗口控制

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 仅允许拖动窗口，不响应双击最大化（窗口固定大小不可最大化）
            DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("最小化(隐藏到托盘)", "BtnMinimize");
            // 参考原项目逻辑：最小化也隐藏到托盘
            HideToTray();
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

        private void ShowPage(string pageName)
        {
            PageSettings.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;
            PageAboutMe.Visibility = Visibility.Collapsed;
            PageDebug.Visibility = Visibility.Collapsed;

            // 重置导航按钮样式
            NavSetting.FontWeight = FontWeights.Normal;
            NavDebug.FontWeight = FontWeights.Normal;
            NavAbout.FontWeight = FontWeights.Normal;

            switch (pageName)
            {
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
                case "debug":
                    PageDebug.Visibility = Visibility.Visible;
                    NavDebug.FontWeight = FontWeights.Bold;
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
            }
        }

        private void Setting_Unchecked(object sender, RoutedEventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb != null)
            {
                Services.Logger.Instance.CheckboxChanged(cb.Content?.ToString() ?? cb.Name, false, cb.Name);
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("保存设置", "BtnSaveSettings");
            SaveSettingsFromUI();
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

        #region 状态更新

        private void UpdateStatus()
        {
            StatusText.Text = _controller.GetStatusText();
        }

        #endregion
    }
}
