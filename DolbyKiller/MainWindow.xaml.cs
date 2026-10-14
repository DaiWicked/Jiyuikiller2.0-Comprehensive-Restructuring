using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DolbyKiller.Models;
using DolbyKiller.Services;

namespace DolbyKiller
{
    public enum AppState { Idle, Scanning, ThreatFound, Cleared, Cleaning }

    public partial class MainWindow : Window
    {
        private readonly Scanner _scanner = new Scanner();
        private readonly Cleaner _cleaner = new Cleaner();
        private ScanReport _report;

        private AppState _state = AppState.Idle;
        private bool _logExpanded = true;
        private readonly ObservableCollection<string> _logs = new ObservableCollection<string>();
        private readonly ObservableCollection<ThreatItem> _uiItems = new ObservableCollection<ThreatItem>();
        private Timer _itemAddTimer;

        public MainWindow()
        {
            InitializeComponent();
            LogItemsControl.ItemsSource = _logs;
            ResultItemsControl.ItemsSource = _uiItems;

            // 订阅日志
            Logger.Instance.OnLog += AppendLog;

            // 订阅扫描
            _scanner.OnProgress += (msg) => Dispatcher.Invoke(() => AppendLog(msg));
            _scanner.OnComplete += OnScanComplete;

            // 订阅清除
            _cleaner.OnProgress += (msg) => Dispatcher.Invoke(() => AppendLog(msg));
            _cleaner.OnComplete += OnCleanComplete;

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 应用圆角裁剪
            UpdateGlassClip();
            SizeChanged += (s, args) => UpdateGlassClip();

            // 窗口淡入动画
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
            BeginAnimation(OpacityProperty, fadeIn);
        }

        /// <summary>
        /// 给 GlassClipRoot 设置圆角 RectangleGeometry，
        /// 确保玻璃层/极光层/内容层都被圆角裁剪
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
            geo.RadiusX = 16;
            geo.RadiusY = 16;
        }

        #region 状态管理

        private void SetState(AppState state)
        {
            _state = state;
            switch (state)
            {
                case AppState.Idle:
                    StatusIcon.Data = (Geometry)FindResource("IconShield");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
                    StatusTitle.Text = "等待扫描";
                    StatusSubtitle.Text = "点击下方按钮开始检测您的电脑";
                    BtnScan.IsEnabled = true;
                    BtnScan.Content = "开始扫描";
                    BtnClean.IsEnabled = false;
                    break;

                case AppState.Scanning:
                    StatusIcon.Data = (Geometry)FindResource("IconScan");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB));
                    StatusTitle.Text = "正在扫描...";
                    StatusSubtitle.Text = "正在检测服务、进程、端口、文件和注册表";
                    BtnScan.IsEnabled = false;
                    BtnClean.IsEnabled = false;
                    StartScanAnimation();
                    break;

                case AppState.ThreatFound:
                    StatusIcon.Data = (Geometry)FindResource("IconWarning");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x3A));
                    int count = _report?.ActiveCount ?? 0;
                    StatusTitle.Text = $"发现 {count} 项威胁";
                    StatusSubtitle.Text = "建议立即点击「一键清除」";
                    BtnScan.IsEnabled = true;
                    BtnScan.Content = "重新扫描";
                    BtnClean.IsEnabled = true;
                    break;

                case AppState.Cleared:
                    StatusIcon.Data = (Geometry)FindResource("IconCheck");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
                    StatusTitle.Text = "已全部清除";
                    StatusSubtitle.Text = "建议重启电脑以确保完全生效";
                    BtnScan.IsEnabled = true;
                    BtnScan.Content = "重新扫描";
                    BtnClean.IsEnabled = false;
                    break;

                case AppState.Cleaning:
                    StatusIcon.Data = (Geometry)FindResource("IconClean");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x3A));
                    StatusTitle.Text = "正在清除...";
                    StatusSubtitle.Text = "请稍候，正在执行清除流程";
                    BtnScan.IsEnabled = false;
                    BtnClean.IsEnabled = false;
                    BtnClean.Content = "清除中...";
                    break;
            }

            // 状态图标弹入动画
            AnimateStatusIcon();
        }

        private void AnimateStatusIcon()
        {
            var scale = new ScaleTransform(1, 1);
            StatusIcon.RenderTransform = scale;
            StatusIcon.RenderTransformOrigin = new Point(0.5, 0.5);

            var sb = new Storyboard();
            var animX = new DoubleAnimationUsingKeyFrames();
            animX.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, TimeSpan.Zero));
            animX.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, TimeSpan.FromMilliseconds(150)));
            animX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase() });
            Storyboard.SetTarget(animX, StatusIcon);
            Storyboard.SetTargetProperty(animX, new PropertyPath("RenderTransform.ScaleX"));
            sb.Children.Add(animX);

            var animY = animX.Clone();
            Storyboard.SetTarget(animY, StatusIcon);
            Storyboard.SetTargetProperty(animY, new PropertyPath("RenderTransform.ScaleY"));
            sb.Children.Add(animY);

            sb.Begin();
        }

        private void StartScanAnimation()
        {
            // 扫描图标缓慢旋转
            var rotate = new RotateTransform(0);
            StatusIcon.RenderTransform = rotate;
            StatusIcon.RenderTransformOrigin = new Point(0.5, 0.5);

            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        #endregion

        #region 扫描

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            StartScan();
        }

        private void StartScan()
        {
            _itemAddTimer?.Dispose();
            _uiItems.Clear();
            SetState(AppState.Scanning);
            AppendLog("=== 开始扫描 ===");
            _scanner.StartAsync();
        }

        private void OnScanComplete(ScanReport report)
        {
            Dispatcher.Invoke(() =>
            {
                _report = report;

                if (report.HasThreats)
                {
                    SetState(AppState.ThreatFound);
                    AppendLog($"扫描完成：发现 {report.TotalCount} 项威胁");
                    // 逐项添加，每项延迟60ms，配合 Loaded 动画形成依次出现效果
                    int index = 0;
                    var items = report.Items;
                    _itemAddTimer = new Timer(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (index < items.Count)
                            {
                                _uiItems.Add(items[index]);
                                index++;
                            }
                            else
                            {
                                _itemAddTimer?.Dispose();
                                _itemAddTimer = null;
                            }
                        });
                    }, null, 100, 60);
                }
                else
                {
                    SetState(AppState.Cleared);
                    StatusTitle.Text = "未发现威胁";
                    StatusSubtitle.Text = "您的电脑没有被 DolbyVision/DolbyAccess 代理感染";
                    AppendLog("扫描完成：未发现威胁");
                }
            });
        }

        /// <summary>
        /// 结果列表项加载时做淡入+上移动画
        /// </summary>
        private void ThreatItem_Loaded(object sender, RoutedEventArgs e)
        {
            var border = sender as Border;
            if (border == null) return;

            var trans = border.RenderTransform as TranslateTransform;
            if (trans == null)
            {
                trans = new TranslateTransform(10, 10);
                border.RenderTransform = trans;
            }

            var sb = new Storyboard();

            var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase()
            };
            Storyboard.SetTarget(opacityAnim, border);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(opacityAnim);

            var yAnim = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase()
            };
            Storyboard.SetTarget(yAnim, border);
            Storyboard.SetTargetProperty(yAnim, new PropertyPath("RenderTransform.Y"));
            sb.Children.Add(yAnim);

            sb.Begin();
        }

        #endregion

        #region 清除

        private void BtnClean_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null || _report.ActiveCount == 0) return;

            var result = MessageBox.Show(
                $"即将清除 {_report.ActiveCount} 项威胁，包括停止服务、终止进程、删除文件和清理注册表。\n\n确定继续吗？",
                "确认清除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            SetState(AppState.Cleaning);
            AppendLog("=== 开始清除 ===");

            // 清除在后台线程执行
            new Thread(() =>
            {
                try { _cleaner.Start(); }
                catch (Exception ex) { AppendLog($"清除异常: {ex.Message}"); }
            })
            { IsBackground = true }.Start();
        }

        private void OnCleanComplete(bool success, int failures)
        {
            Dispatcher.Invoke(() =>
            {
                BtnClean.Content = "一键清除";
                if (success)
                {
                    SetState(AppState.Cleared);
                    AppendLog("=== 清除完成：全部成功 ===");

                    // 弹出自定义确认框：左是，右取消
                    var dlg = new RestartDialog { Owner = this };
                    dlg.ShowDialog();

                    if (dlg.ShouldRestart)
                    {
                        AppendLog("用户选择重启电脑");
                        try
                        {
                            System.Diagnostics.Process.Start("shutdown.exe", "/r /t 3 /c \"DolbyKiller 清除完成，3秒后重启\"");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"重启命令执行失败: {ex.Message}");
                            MessageBox.Show("重启命令执行失败，请手动重启电脑。", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        AppendLog("用户取消重启");
                        // 重新扫描验证
                        StartScan();
                    }
                }
                else
                {
                    SetState(AppState.ThreatFound);
                    AppendLog($"=== 清除完成：{failures} 项失败/残留 ===");
                    MessageBox.Show($"清除完成，但仍有 {failures} 项残留。\n\n建议重启电脑后再次运行本工具扫描。",
                        "部分清除", MessageBoxButton.OK, MessageBoxImage.Warning);
                    // 重新扫描验证
                    StartScan();
                }
            });
        }

        #endregion

        #region 日志

        private void AppendLog(string msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(msg));
                return;
            }
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logs.Add($"[{timestamp}] {msg}");

            // 限制日志条数
            if (_logs.Count > 500) _logs.RemoveAt(0);

            // 平滑滚动到底
            LogScrollViewer.ScrollToEnd();
        }

        private void LogHeader_Click(object sender, MouseButtonEventArgs e)
        {
            _logExpanded = !_logExpanded;
            LogArrow.Text = _logExpanded ? "▼" : "▶";

            var anim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase()
            };
            if (_logExpanded)
            {
                anim.From = 0;
                anim.To = 120;
                LogBorder.BeginAnimation(MaxHeightProperty, anim);
            }
            else
            {
                anim.From = LogBorder.ActualHeight;
                anim.To = 0;
                LogBorder.BeginAnimation(MaxHeightProperty, anim);
            }
        }

        #endregion

        #region 窗口操作

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return; // 双击不拖动
            DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 有未清除威胁时确认
            if (_state == AppState.ThreatFound && _report != null && _report.ActiveCount > 0)
            {
                var result = MessageBox.Show(
                    $"仍有 {_report.ActiveCount} 项威胁未清除，确定退出吗？",
                    "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        #endregion
    }
}
