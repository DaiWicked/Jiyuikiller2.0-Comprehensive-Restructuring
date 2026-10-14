using System;
using System.Windows;
using System.Windows.Input;

namespace DolbyKiller
{
    public partial class RestartDialog : Window
    {
        /// <summary>用户选择了"是"（重启）</summary>
        public bool ShouldRestart { get; private set; }

        public RestartDialog()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                // 窗口淡入
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                BeginAnimation(OpacityProperty, fade);
            };
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            ShouldRestart = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ShouldRestart = false;
            Close();
        }
    }
}
