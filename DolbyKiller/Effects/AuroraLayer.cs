using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DolbyKiller.Effects
{
    /// <summary>
    /// 极光色相循环背景层
    /// 3个径向渐变光斑，24秒慢周期色相循环 + 中心点漂移
    /// 位于玻璃折射层之上、内容层之下，Opacity=0.35 色相清晰但不影响文字可读性
    /// </summary>
    public class AuroraLayer : Grid
    {
        private const double AuroraOpacity = 0.35;
        private const int CycleSeconds = 24;

        private readonly Border _spotA; // 青紫→蓝
        private readonly Border _spotB; // 粉橙→金
        private readonly Border _spotC; // 青绿→紫
        private readonly TranslateTransform _transA;
        private readonly TranslateTransform _transB;
        private readonly TranslateTransform _transC;
        private Storyboard _storyboard;
        private bool _isRunning;

        public AuroraLayer()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;

            _transA = new TranslateTransform();
            _transB = new TranslateTransform();
            _transC = new TranslateTransform();

            _spotA = CreateSpot(
                Colors.BlueViolet, Colors.RoyalBlue,
                new Point(0.2, 0.25), _transA);
            _spotB = CreateSpot(
                Color.FromRgb(0xFF, 0x6B, 0x9D), Color.FromRgb(0xFF, 0x8C, 0x42),
                new Point(0.8, 0.75), _transB);
            _spotC = CreateSpot(
                Color.FromRgb(0x00, 0xD4, 0xAA), Color.FromRgb(0x00, 0xB4, 0xD8),
                new Point(0.5, 0.9), _transC);

            Children.Add(_spotA);
            Children.Add(_spotB);
            Children.Add(_spotC);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static Border CreateSpot(Color color1, Color color2, Point center, TranslateTransform trans)
        {
            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = 0.9,
                RadiusY = 0.7,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(color1, 0.0),
                    new GradientStop(color2, 0.5),
                    new GradientStop(Colors.Transparent, 1.0)
                }
            };
            brush.Freeze();

            return new Border
            {
                Background = brush,
                Opacity = AuroraOpacity,
                RenderTransform = trans
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 渲染层级不足时降级为静态渐变（不循环）
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

        /// <summary>
        /// 启动极光动画
        /// </summary>
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

        /// <summary>
        /// 停止极光动画
        /// </summary>
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

            // === 光斑A：青紫 → 蓝 → 青 ===
            AddColorAnimation(sb, _spotA, 0, Colors.BlueViolet, Colors.RoyalBlue, Color.FromRgb(0x00, 0xCE, 0xD1), duration, ease);
            AddTranslateAnimation(sb, _transA, -40, -25, 35, 20, duration, ease);

            // === 光斑B：粉 → 橙 → 金 ===
            AddColorAnimation(sb, _spotB, 0, Color.FromRgb(0xFF, 0x6B, 0x9D), Color.FromRgb(0xFF, 0x8C, 0x42), Color.FromRgb(0xFF, 0xD7, 0x00), duration, ease);
            AddTranslateAnimation(sb, _transB, 35, 25, -35, -20, duration, ease);

            // === 光斑C：青绿 → 蓝绿 → 紫 ===
            AddColorAnimation(sb, _spotC, 0, Color.FromRgb(0x00, 0xD4, 0xAA), Color.FromRgb(0x00, 0xB4, 0xD8), Color.FromRgb(0x7B, 0x68, 0xEE), duration, ease);
            AddTranslateAnimation(sb, _transC, 0, 35, 0, -30, duration, ease);

            return sb;
        }

        /// <summary>
        /// 给 RadialGradientBrush 的第0、第1个 GradientStop 添加色相关键帧动画
        /// </summary>
        private static void AddColorAnimation(Storyboard sb, Border spot, int stopIndex,
            Color c1, Color c2, Color c3, Duration duration, IEasingFunction ease)
        {
            var brush = (RadialGradientBrush)spot.Background;
            var stop0 = brush.GradientStops[0];
            var stop1 = brush.GradientStops[1];

            // 第0个色标：c1 → c2 → c3
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

            // 第1个色标：c2 → c3 → c1（错开相位，避免同步）
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

        /// <summary>
        /// 给 TranslateTransform 添加 X/Y 漂移动画
        /// </summary>
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
