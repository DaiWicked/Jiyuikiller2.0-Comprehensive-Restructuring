using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ChatRoom
{
    /// <summary>
    /// 独立消息提醒窗（后台/最小化/隐藏到托盘时用）。
    ///
    /// 为什么需要它：主窗口内的浮动卡片只在"主窗口可见且在前台"时能被看到；
    /// 一旦用户把窗口最小化、隐藏到托盘、或被别的窗口盖住，卡片就跟着看不见了 ——
    /// 于是"后台收不到提醒"（用户实测反馈）。
    ///
    /// 按豆包需求 #5 的约束：不抢焦点（ShowActivated=False）、不闪任务栏（ShowInTaskbar=False）、
    /// 无声音（不含任何提示音）、3.5 秒自动收起、点击唤回主窗口并跳到对应会话。
    /// 同一个会话连续来消息只更新内容（合并成"N 条新消息"），不会叠出一堆窗口。
    /// </summary>
    public partial class ToastWindow : Window
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _timer;
        private string _convKey;

        public ToastWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
            _timer.Tick += (s, e) => HideAnimated();
        }

        /// <summary>显示/更新提醒（不激活窗口）</summary>
        public void ShowMessage(string title, string text, string convKey)
        {
            _convKey = convKey;
            TextToastTitle.Text = title;
            TextToastText.Text = text;

            // 屏幕右下角（工作区，避开任务栏）
            Rect wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Bottom - Height - 16;

            if (!IsVisible) Show();
            // ★ 这里**不能**调 Activate()：Activate 会把本窗设为活动窗口，等于抢走用户正在打字的焦点，
            //   与需求 #5「不抢焦点」直接冲突（ShowActivated=False 只管首次显示不激活）。
            //   置前交给 Topmost=True，不需要激活。
            if (Models.ChatSettings.AnimationsOn)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
                var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Card.BeginAnimation(OpacityProperty, fade);
                CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            }
            else
            {
                Card.BeginAnimation(OpacityProperty, null);
                CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                Card.Opacity = 1;
                CardTranslate.Y = 0;
            }

            _timer.Stop();
            _timer.Start();
        }

        private void HideAnimated()
        {
            _timer.Stop();
            if (!Models.ChatSettings.AnimationsOn) { Card.BeginAnimation(OpacityProperty, null); Hide(); return; }
            var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (s, e) => { Hide(); };
            Card.BeginAnimation(OpacityProperty, fade);
        }

        private void Toast_Click(object sender, MouseButtonEventArgs e)
        {
            HideAnimated();
            if (_main != null) _main.RestoreAndOpenConversation(_convKey);
        }
    }
}