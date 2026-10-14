using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DolbyKiller.Services;

namespace DolbyKiller.Effects
{
    /// <summary>
    /// 毛玻璃窗口管理器（DolbyKiller 简化版）
    /// 移植自主项目 JiYuKiller，负责桌面截图捕获、着色器参数更新、背景同步
    /// </summary>
    public class GlassyWindowManager : IDisposable
    {
        public event Action BackdropUpdated;

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
        private double _blurIntensity = 0.8;
        private bool _isDisposed;

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

            if (window.IsLoaded)
            {
                OnSourceInitialized(window, EventArgs.Empty);
                OnLoaded(window, new RoutedEventArgs());
            }
        }

        public double BlurIntensity
        {
            get { return _blurIntensity; }
            set { _blurIntensity = value; UpdateGlassyEffectParameters(); }
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
            if (_isDisposed || _isDeactivatedCapture || !_window.IsVisible ||
                _window.WindowState == WindowState.Minimized)
            {
                return;
            }
            _isDeactivatedCapture = true;
            try { CaptureBehindWindow(); }
            finally { _isDeactivatedCapture = false; }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void SetupBackdropCapture()
        {
            if (_backdropBorder == null) return;

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
                try
                {
                    _glassyEffect = new GlassyEffect();
                    if (!_glassyEffect.IsShaderLoaded)
                    {
                        Logger.Instance.Warn("GlassyEffect 像素着色器加载失败，将使用普通半透明效果");
                        _glassyEffect = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Warn($"GlassyEffect 创建失败: {ex.Message}");
                    _glassyEffect = null;
                }
            }

            if (_glassyEffect != null && _glassyBorder != null)
            {
                try { _glassyBorder.Effect = _glassyEffect; }
                catch (Exception ex)
                {
                    Logger.Instance.Warn($"GlassyEffect 应用失败: {ex.Message}");
                    _glassyBorder.Effect = null;
                }
            }
        }

        private void UpdateGlassyEffectParameters()
        {
            if (_glassyEffect == null) return;

            var width = Math.Max(1.0, _window.ActualWidth);
            var height = Math.Max(1.0, _window.ActualHeight);

            _glassyEffect.TextureSize = new Point(width, height);
            _glassyEffect.GlassCenter = new Point(width * 0.5, height * 0.5);
            _glassyEffect.GlassSize = new Point(width, height);
            _glassyEffect.BlurIntensity = _blurIntensity;
        }

        private void ScheduleDelayedBackdropUpdate()
        {
            if (_isDisposed) return;

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
            if (_backdropUpdateTimer != null) _backdropUpdateTimer.Stop();
            UpdateBackdropCapture();
        }

        private void CaptureBehindWindow()
        {
            if (_isCapturing || _isDisposed) return;
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
            if (_backdropBrush == null || _window.WindowState == WindowState.Minimized ||
                PresentationSource.FromVisual(_window) == null)
            {
                return;
            }

            var snapshot = ScreenCaptureHelper.FullScreenSnapshot;
            if (snapshot == null) return;

            var topLeft = _window.PointToScreen(new Point(0, 0));
            var bottomRight = _window.PointToScreen(new Point(_window.ActualWidth, _window.ActualHeight));
            var x = (int)Math.Round(topLeft.X - ScreenCaptureHelper.VirtualScreenX);
            var y = (int)Math.Round(topLeft.Y - ScreenCaptureHelper.VirtualScreenY);
            var width = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
            var height = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));

            if (x < 0) { width += x; x = 0; }
            if (y < 0) { height += y; y = 0; }
            if (x + width > snapshot.PixelWidth) width = snapshot.PixelWidth - x;
            if (y + height > snapshot.PixelHeight) height = snapshot.PixelHeight - y;
            if (width <= 0 || height <= 0) return;

            if (!ReferenceEquals(_backdropBrush.ImageSource, snapshot))
            {
                _backdropBrush.ImageSource = snapshot;
                _backdropBrush.ViewboxUnits = BrushMappingMode.Absolute;
            }
            _backdropBrush.Viewbox = new Rect(x, y, width, height);
            BackdropUpdated?.Invoke();
        }

        public void RefreshBackdrop()
        {
            CaptureBehindWindow();
            UpdateBackdropCapture();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

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

            BackdropUpdated = null;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    }
}
