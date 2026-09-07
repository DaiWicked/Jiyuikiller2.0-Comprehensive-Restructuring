using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace JiYuKiller
{
    public partial class MainWindow : Window
    {
        private Models.AppSettings _settings;
        private Services.JiYuController _controller;

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

            // 启动监控
            _controller.Start();

            // 定时更新状态
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            Services.Logger.Instance.WindowEvent("MainWindow", "构造函数完成");
        }

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
            if (e.ClickCount == 2)
            {
                // 双击最大化/还原
                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;
                else
                    WindowState = WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("最小化", "BtnMinimize");
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关闭", "BtnClose");
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            Services.Logger.Instance.WindowEvent("MainWindow", "OnClosed");
            _controller.Stop();
            Services.Logger.Instance.Close();
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

        private void ShowPage(string pageName)
        {
            PageSettings.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;
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
