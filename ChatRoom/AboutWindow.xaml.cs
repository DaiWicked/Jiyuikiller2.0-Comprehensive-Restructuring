using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ChatRoom
{
    /// <summary>
    /// 关于页（豆包需求 #4）。
    /// 版式与动效按她给的补充方案：logo 从模糊到清晰、文字逐行淡入（间隔 0.1s）、分隔线用渐变线、
    /// 背景玻璃拟态（85% 不透明 + 主色 20% 微光边框 + 圆角 12，不截取桌面、Win7 可用）。
    /// 版本号取自 AppInfo.Version（由 tools\sync-version.ps1 从主程序同步，永不失配）。
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            TextVersion.Text = AppInfo.Version;
            Loaded += (s, e) => { WindowPlacement.ClampToWorkArea(this); Anim.ApplyTo(this); PlayIntro(); };
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>入场动效：logo 去模糊 + 光晕呼吸 + 文字逐行淡入</summary>
        private void PlayIntro()
        {
            // logo：模糊 -> 清晰
            var blur = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            LogoBlur.BeginAnimation(BlurEffect.RadiusProperty, blur);

            // 光晕呼吸（缓慢、低幅度，不刺眼）
            var halo = new DoubleAnimation(0.35, 0.7, TimeSpan.FromSeconds(2.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            LogeHalo.BeginAnimation(OpacityProperty, halo);

            // 文字逐行淡入，间隔 0.1s
            UIElement[] lines = { Line1, Line2, Line3, Line4, Line5, Line6, TextVersion, Line7, Line8, Line9 };
            for (int i = 0; i < lines.Length; i++)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    BeginTime = TimeSpan.FromMilliseconds(120 + i * 100),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                lines[i].BeginAnimation(OpacityProperty, fade);
            }
        }
    }
}