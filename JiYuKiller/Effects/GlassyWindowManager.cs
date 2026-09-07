using JiYuKiller.Effects;
using JiYuKiller.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace JiYuKiller.Effects
{
    /// <summary>
    /// 毛玻璃窗口管理器
    /// 移植自 WPF-Liquid-Glass-Effect-main 的 GlassyWindowBehavior
    /// 负责桌面截图捕获、着色器参数更新、背景同步
    /// </summary>
    public class GlassyWindowManager : IDisposable
    {
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;

        private readonly Window _window;
        private readonly Border _backdropBorder;
        private readonly Border _glassyBorder;
        private DispatcherTimer _backdropUpdateTimer;
        private ImageBrush _backdropBrush;
        private bool _isCapturing;
        private bool _isDeactivatedCapture;
        private GlassyEffect _glassyEffect;
        private double _blurIntensity = 0.2;

        public GlassyWindowManager(Window window, Border backdropBorder, Border glassyBorder)
        {
            _window = window;
            _backdropBorder = backdropBorder;
            _glassyBorder = glassyBorder;

            _window.SourceInitialized += OnSourceInitialized;
            _window.Loaded += OnLoaded;
            _window.SizeChanged += OnSizeChanged;
            _window.LocationChanged += OnLocationChanged;
            _window.Activated += OnActivated;
            _window.Deactivated += OnDeactivated;
            _window.Closed += OnClosed;

            // 如果窗口已经加载（在Loaded事件之后才创建管理器），直接初始化
            if (window.IsLoaded)
            {
                Services.Logger.Instance.Info("窗口已加载，直接执行毛玻璃初始化");
                OnSourceInitialized(window, EventArgs.Empty);
                OnLoaded(window, new RoutedEventArgs());
            }
        }

        /// <summary>
        /// 模糊强度（0.0 - 1.0）
        /// </summary>
        public double BlurIntensity
        {
            get { return _blurIntensity; }
            set
            {
                _blurIntensity = value;
                UpdateGlassyEffectParameters();
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            SetupBackdropCapture();
            EnsureGlassyEffect();
            UpdateGlassyEffectParameters();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ScreenCaptureHelper.FullScreenSnapshot == null)
            {
                CaptureBehindWindow();
            }

            SetupBackdropCapture();
            EnsureGlassyEffect();
            UpdateGlassyEffectParameters();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGlassyEffectParameters();
            ScheduleDelayedBackdropUpdate();
        }

        private void OnLocationChanged(object sender, EventArgs e)
        {
            ScheduleDelayedBackdropUpdate();
        }

        private void OnActivated(object sender, EventArgs e)
        {
            ScheduleDelayedBackdropUpdate();
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            if (_isDeactivatedCapture)
            {
                return;
            }

            _isDeactivatedCapture = true;
            try
            {
                CaptureBehindWindow();
            }
            finally
            {
                _isDeactivatedCapture = false;
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void SetupBackdropCapture()
        {
            if (_backdropBorder == null)
            {
                return;
            }

            if (_backdropBrush == null)
            {
                _backdropBrush = new ImageBrush
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
            }

            _backdropBorder.Background = _backdropBrush;
            UpdateBackdropCapture();
        }

        private void EnsureGlassyEffect()
        {
            if (_glassyEffect == null)
            {
                _glassyEffect = new GlassyEffect();
                Services.Logger.Instance.Info("GlassyEffect 像素着色器创建成功");
            }

            if (_glassyBorder != null)
            {
                _glassyBorder.Effect = _glassyEffect;
                Services.Logger.Instance.Info("GlassyEffect 已应用到 GlassyLayer");
            }
        }

        private void UpdateGlassyEffectParameters()
        {
            if (_glassyEffect == null)
            {
                return;
            }

            var width = Math.Max(1.0, _window.ActualWidth);
            var height = Math.Max(1.0, _window.ActualHeight);

            _glassyEffect.TextureSize = new Point(width, height);
            _glassyEffect.GlassCenter = new Point(width * 0.5, height * 0.5);
            _glassyEffect.GlassSize = new Point(width, height);
            _glassyEffect.BlurIntensity = _blurIntensity;
        }

        private void ScheduleDelayedBackdropUpdate()
        {
            if (_backdropUpdateTimer == null)
            {
                _backdropUpdateTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _backdropUpdateTimer.Tick += OnBackdropUpdateTick;
            }

            if (!_backdropUpdateTimer.IsEnabled)
            {
                _backdropUpdateTimer.Start();
            }
        }

        private void OnBackdropUpdateTick(object sender, EventArgs e)
        {
            if (_backdropUpdateTimer != null)
            {
                _backdropUpdateTimer.Stop();
            }
            UpdateBackdropCapture();
        }

        private void CaptureBehindWindow()
        {
            if (_isCapturing)
            {
                return;
            }

            _isCapturing = true;

            try
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SwHide);
                    _window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                }

                ScreenCaptureHelper.CaptureFullScreen();
            }
            finally
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SwShowNoActivate);
                    _window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                }

                _isCapturing = false;
            }
        }

        private void UpdateBackdropCapture()
        {
            if (_backdropBrush == null || _window.WindowState == WindowState.Minimized)
            {
                return;
            }

            var snapshot = ScreenCaptureHelper.FullScreenSnapshot;
            if (snapshot == null)
            {
                Services.Logger.Instance.Warn("桌面截图为空，无法更新毛玻璃背景");
                return;
            }

            var topLeft = _window.PointToScreen(new Point(0, 0));
            var bottomRight = _window.PointToScreen(new Point(_window.ActualWidth, _window.ActualHeight));
            var x = (int)Math.Round(topLeft.X - ScreenCaptureHelper.VirtualScreenX);
            var y = (int)Math.Round(topLeft.Y - ScreenCaptureHelper.VirtualScreenY);
            var width = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
            var height = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));

            if (x < 0)
            {
                width += x;
                x = 0;
            }

            if (y < 0)
            {
                height += y;
                y = 0;
            }

            if (x + width > snapshot.PixelWidth)
            {
                width = snapshot.PixelWidth - x;
            }

            if (y + height > snapshot.PixelHeight)
            {
                height = snapshot.PixelHeight - y;
            }

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (!ReferenceEquals(_backdropBrush.ImageSource, snapshot))
            {
                _backdropBrush.ImageSource = snapshot;
                _backdropBrush.ViewboxUnits = BrushMappingMode.Absolute;
            }

            _backdropBrush.Viewbox = new Rect(x, y, width, height);
            Services.Logger.Instance.Debug($"毛玻璃背景已更新: 区域=({x},{y},{width}x{height}), 截图尺寸={snapshot.PixelWidth}x{snapshot.PixelHeight}");
        }

        /// <summary>
        /// 强制刷新背景截图
        /// </summary>
        public void RefreshBackdrop()
        {
            CaptureBehindWindow();
            UpdateBackdropCapture();
        }

        public void Dispose()
        {
            if (_backdropUpdateTimer != null)
            {
                _backdropUpdateTimer.Stop();
                _backdropUpdateTimer.Tick -= OnBackdropUpdateTick;
                _backdropUpdateTimer = null;
            }

            _window.SourceInitialized -= OnSourceInitialized;
            _window.Loaded -= OnLoaded;
            _window.SizeChanged -= OnSizeChanged;
            _window.LocationChanged -= OnLocationChanged;
            _window.Activated -= OnActivated;
            _window.Deactivated -= OnDeactivated;
            _window.Closed -= OnClosed;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    }
}
