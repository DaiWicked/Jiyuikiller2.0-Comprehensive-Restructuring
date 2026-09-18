using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ChatRoom.Models;

namespace ChatRoom
{
    /// <summary>
    /// 首次使用注册页（豆包需求 #2）。
    /// 必须精确输入 yes 才能点"进入聊天室"（她的要求）；完成后窗口淡出、昵称与 Registered 落盘，
    /// 以后启动不再弹出（设置里可重置）。
    /// </summary>
    public partial class RegisterWindow : Window
    {
        public RegisterWindow()
        {
            InitializeComponent();
            InputNickname.TextChanged += Validate;
            InputVerify.TextChanged += Validate;
            Loaded += (s, e) => InputNickname.Focus();
        }

        private void Validate(object sender, TextChangedEventArgs e)
        {
            string nick = (InputNickname.Text ?? "").Trim();
            string v = (InputVerify.Text ?? "").Trim();
            bool ok = nick.Length > 0 && string.Equals(v, "yes", StringComparison.Ordinal);
            BtnEnter.IsEnabled = ok;
            TextTip.Text = ok
                ? "验证通过，随时可以进入"
                : (nick.Length == 0 ? "请输入昵称，并在验证框里输入 yes" : "验证框里请精确输入 yes（小写）");
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void BtnEnter_Click(object sender, RoutedEventArgs e)
        {
            ChatSettings s = ChatSettings.Load();
            s.Nickname = (InputNickname.Text ?? "").Trim();
            s.Registered = true;
            s.Save();
            ChatSettings.ClearNeedRegister();   // 注册完成，清掉"需要重新注册"标记

            // 完成后窗口淡出（她要求"完成后窗口淡出切换到主界面"）
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (a, b) => { DialogResult = true; };
            BeginAnimation(OpacityProperty, fade);
        }

        private void BtnQuit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}