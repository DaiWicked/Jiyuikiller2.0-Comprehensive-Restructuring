using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DolbyKiller.Effects
{
    /// <summary>
    /// Win11 OOBE 风格流动渐变背景
    /// 4个大尺寸柔和光斑，低饱和蓝紫粉色调，缓慢流动融合
    /// </summary>
    public class AuroraLayer : Grid
    {
        private const double AuroraOpacity = 0.65;
        private const int CycleSeconds = 16;

        private readonly Border _spotA; // 深蓝
        private readonly Border _spotB; // 紫
        private readonly Border _spotC; // 粉
        private readonly Border _spotD; // 青
        private readonly TranslateTransform _transA;
        private readonly TranslateTransform _transB;
        private readonly TranslateTransform _transC;
        private readonly TranslateTransform _transD;
        private Storyboard _storyboard;
        private bool _isRunning;

        public AuroraLayer()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;

            _transA = new TranslateTransform();
            _transB = new TranslateTransform();
            _transC = new TranslateTransform();
            _transD = new TranslateTransform();

            // Win11 OOBE 风格：大尺寸、低饱和、柔和过渡
            _spotA = CreateSpot(
                Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0x2D, 0x5A, 0x8A),
                new Point(0.15, 0.2), 1.2, 0.9, _transA);
            _spotB = CreateSpot(
                Color.FromRgb(0x4A, 0x2E, 0x6B), Color.FromRgb(0x6B, 0x3E, 0x8A),
                new Point(0.85, 0.3), 1.0, 1.1, _transB);
            _spotC = CreateSpot(
                Color.FromRgb(0x7B, 0x3B, 0x6B), Color.FromRgb(0x9B, 0x4B, 0x7B),
                new Point(0.5, 0.85), 1.1, 0.8, _transC);
            _spotD = CreateSpot(
                Color.FromRgb(0x1E, 0x5A, 0x6B), Color.FromRgb(0x2E, 0x7A, 0x8A),
                new Point(0.7, 0.65), 0.9, 1.0, _transD);

            Children.Add(_spotA);
            Children.Add(_spotB);
            Children.Add(_spotC);
            Children.Add(_spotD);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static Border CreateSpot(Color color1, Color color2, Point center,
            double radiusX, double radiusY, TranslateTransform trans)
        {
            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = radiusX,
                RadiusY = radiusY,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(color1, 0.0),
                    new GradientStop(color2, 0.4),
                    new GradientStop(Colors.Transparent, 1.0)
                }
            };
            // 注意：不能 Freeze 画刷，因为后续要对 GradientStop.Color 做颜色动画
            // brush.Freeze() 会导致 Win10 下抛出"无法对不可变对象进行动画处理"异常

            return new Border
            {
                Background = brush,
                Opacity = AuroraOpacity,
                RenderTransform = trans
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 渲染层级不足时降级为静态
            if (RenderCapability.Tier >> 16 < 2)
            {
                return;
            }
            Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Stop();
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            if (_storyboard == null)
            {
                _storyboard = BuildStoryboard();
            }
            _storyboard.Begin(this, true);
        }

        public void Stop()
        {
            _isRunning = false;
            _storyboard?.Stop(this);
        }

        private Storyboard BuildStoryboard()
        {
            var sb = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var duration = new Duration(TimeSpan.FromSeconds(CycleSeconds));
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // 光斑A：深蓝 → 蓝紫 → 深青，左上→右下大弧线
            AddColorAnimation(sb, _spotA,
                Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0x3A, 0x2E, 0x6B), Color.FromRgb(0x1E, 0x5A, 0x6B),
                duration, ease);
            AddTranslateAnimation(sb, _transA, -80, -50, 100, 70, duration, ease);

            // 光斑B：紫 → 深紫 → 蓝紫，右上→左下
            AddColorAnimation(sb, _spotB,
                Color.FromRgb(0x4A, 0x2E, 0x6B), Color.FromRgb(0x2E, 0x3A, 0x6B), Color.FromRgb(0x5A, 0x2E, 0x7B),
                duration, ease);
            AddTranslateAnimation(sb, _transB, 90, -30, -90, 60, duration, ease);

            // 光斑C：粉 → 紫红 → 暗粉，底部横向流动
            AddColorAnimation(sb, _spotC,
                Color.FromRgb(0x7B, 0x3B, 0x6B), Color.FromRgb(0x6B, 0x2E, 0x5B), Color.FromRgb(0x8B, 0x3B, 0x5B),
                duration, ease);
            AddTranslateAnimation(sb, _transC, -100, 40, 100, -50, duration, ease);

            // 光斑D：青 → 蓝青 → 深青，右中→左中
            AddColorAnimation(sb, _spotD,
                Color.FromRgb(0x1E, 0x5A, 0x6B), Color.FromRgb(0x2E, 0x4A, 0x7B), Color.FromRgb(0x1E, 0x6A, 0x5B),
                duration, ease);
            AddTranslateAnimation(sb, _transD, 70, 50, -70, -60, duration, ease);

            return sb;
        }

        private static void AddColorAnimation(Storyboard sb, Border spot,
            Color c1, Color c2, Color c3, Duration duration, IEasingFunction ease)
        {
            var brush = (RadialGradientBrush)spot.Background;
            var stop0 = brush.GradientStops[0];
            var stop1 = brush.GradientStops[1];

            var anim0 = new ColorAnimationUsingKeyFrames
            {
                Duration = duration,
                KeyFrames = new ColorKeyFrameCollection
                {
                    new EasingColorKeyFrame(c1, KeyTime.FromPercent(0.0), ease),
                    new EasingColorKeyFrame(c2, KeyTime.FromPercent(0.5), ease),
                    new EasingColorKeyFrame(c3, KeyTime.FromPercent(1.0), ease)
                }
            };
            Storyboard.SetTarget(anim0, stop0);
            Storyboard.SetTargetProperty(anim0, new PropertyPath(GradientStop.ColorProperty));
            sb.Children.Add(anim0);

            var anim1 = new ColorAnimationUsingKeyFrames
            {
                Duration = duration,
                KeyFrames = new ColorKeyFrameCollection
                {
                    new EasingColorKeyFrame(c2, KeyTime.FromPercent(0.0), ease),
                    new EasingColorKeyFrame(c3, KeyTime.FromPercent(0.5), ease),
                    new EasingColorKeyFrame(c1, KeyTime.FromPercent(1.0), ease)
                }
            };
            Storyboard.SetTarget(anim1, stop1);
            Storyboard.SetTargetProperty(anim1, new PropertyPath(GradientStop.ColorProperty));
            sb.Children.Add(anim1);
        }

        private static void AddTranslateAnimation(Storyboard sb, TranslateTransform trans,
            double fromX, double fromY, double toX, double toY, Duration duration, IEasingFunction ease)
        {
            var animX = new DoubleAnimationUsingKeyFrames
            {
                Duration = duration,
                KeyFrames = new DoubleKeyFrameCollection
                {
                    new EasingDoubleKeyFrame(fromX, KeyTime.FromPercent(0.0), ease),
                    new EasingDoubleKeyFrame(toX, KeyTime.FromPercent(1.0), ease)
                }
            };
            Storyboard.SetTarget(animX, trans);
            Storyboard.SetTargetProperty(animX, new PropertyPath(TranslateTransform.XProperty));
            sb.Children.Add(animX);

            var animY = new DoubleAnimationUsingKeyFrames
            {
                Duration = duration,
                KeyFrames = new DoubleKeyFrameCollection
                {
                    new EasingDoubleKeyFrame(fromY, KeyTime.FromPercent(0.0), ease),
                    new EasingDoubleKeyFrame(toY, KeyTime.FromPercent(1.0), ease)
                }
            };
            Storyboard.SetTarget(animY, trans);
            Storyboard.SetTargetProperty(animY, new PropertyPath(TranslateTransform.YProperty));
            sb.Children.Add(animY);
        }
    }
}
