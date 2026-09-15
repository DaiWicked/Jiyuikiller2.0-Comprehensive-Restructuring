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
    /// 姣涚幓鐠冪獥鍙ｇ鐞嗗櫒
    /// 绉绘鑷?WPF-Liquid-Glass-Effect-main 鐨?GlassyWindowBehavior
    /// 璐熻矗妗岄潰鎴浘鎹曡幏銆佺潃鑹插櫒鍙傛暟鏇存柊銆佽儗鏅悓姝?
    /// </summary>
    public class GlassyWindowManager : IDisposable
    {
        /// <summary>妗岄潰鎴浘/鍖哄煙鏇存柊鏃惰Е鍙戯紙鐢ㄤ簬搴曟爮绛夊瓙鍖哄煙鍚屾鑳屾櫙锛?/summary>
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

            // 濡傛灉绐楀彛宸茬粡鍔犺浇锛堝湪Loaded浜嬩欢涔嬪悗鎵嶅垱寤虹鐞嗗櫒锛夛紝鐩存帴鍒濆鍖?
            if (window.IsLoaded)
            {
                Services.Logger.Instance.Info("绐楀彛宸插姞杞斤紝鐩存帴鎵ц姣涚幓鐠冨垵濮嬪寲");
                OnSourceInitialized(window, EventArgs.Empty);
                OnLoaded(window, new RoutedEventArgs());
            }
        }

        /// <summary>
        /// 妯＄硦寮哄害锛?.0 - 1.0锛?
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
                try
                {
                    _glassyEffect = new GlassyEffect();
                    if (_glassyEffect.IsShaderLoaded)
                    {
                        Services.Logger.Instance.Info("GlassyEffect 鍍忕礌鐫€鑹插櫒鍒涘缓鎴愬姛");
                    }
                    else
                    {
                        Services.Logger.Instance.Warn("GlassyEffect 鍍忕礌鐫€鑹插櫒鍔犺浇澶辫触锛屽皢浣跨敤鏅€氬崐閫忔槑鏁堟灉");
                        _glassyEffect = null;
                    }
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Warn($"GlassyEffect 鍒涘缓澶辫触: {ex.Message}锛屽皢浣跨敤鏅€氬崐閫忔槑鏁堟灉");
                    _glassyEffect = null;
                }
            }

            if (_glassyEffect != null && _glassyBorder != null)
            {
                try
                {
                    _glassyBorder.Effect = _glassyEffect;
                    Services.Logger.Instance.Info("GlassyEffect 宸插簲鐢ㄥ埌 GlassyLayer");
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Warn($"GlassyEffect 搴旂敤澶辫触: {ex.Message}");
                    _glassyBorder.Effect = null;
                }
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
            if (_backdropBrush == null || _window.WindowState == WindowState.Minimized || System.Windows.PresentationSource.FromVisual(_window) == null)
            {
                return;
            }

            var snapshot = ScreenCaptureHelper.FullScreenSnapshot;
            if (snapshot == null)
            {
                Services.Logger.Instance.Warn("妗岄潰鎴浘涓虹┖锛屾棤娉曟洿鏂版瘺鐜荤拑鑳屾櫙");
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
            Services.Logger.Instance.Debug($"姣涚幓鐠冭儗鏅凡鏇存柊: 鍖哄煙=({x},{y},{width}x{height}), 鎴浘灏哄={snapshot.PixelWidth}x{snapshot.PixelHeight}");
            BackdropUpdated?.Invoke();
        }

        /// <summary>
        /// 寮哄埗鍒锋柊鑳屾櫙鎴浘
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

