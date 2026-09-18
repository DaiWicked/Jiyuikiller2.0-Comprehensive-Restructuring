using System.Windows;
using ChatRoom.Models;

namespace ChatRoom
{
    public partial class SettingsWindow : Window
    {
        public ChatSettings Settings { get; private set; }

        public SettingsWindow(ChatSettings settings)
        {
            InitializeComponent();
            Settings = settings;
            LoadToUI();
        }

        private void LoadToUI()
        {
            InputNickname.Text = Settings.Nickname;
            RadioEnter.IsChecked = Settings.SendWithEnter;
            RadioCtrlEnter.IsChecked = !Settings.SendWithEnter;

            if (Settings.FontSize <= 11) RadioSmall.IsChecked = true;
            else if (Settings.FontSize >= 15) RadioLarge.IsChecked = true;
            else RadioMedium.IsChecked = true;

            CheckTopMost.IsChecked = Settings.TopMost;
            CheckClearOnExit.IsChecked = Settings.ClearOnExit;
            SliderWallpaper.Value = Settings.WallpaperOpacity;
            TextWallpaper.Text = string.IsNullOrEmpty(Settings.WallpaperPath) ? "未设置" : System.IO.Path.GetFileName(Settings.WallpaperPath);
            TextWallpaperOpacity.Text = (int)System.Math.Round(Settings.WallpaperOpacity * 100) + "%";
        }

        /// <summary>选择壁纸（需求#7）：复制进数据目录并立即应用</summary>
        private void BtnPickWallpaper_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择聊天区壁纸",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
            };
            if (dlg.ShowDialog() != true) return;
            if (Settings.SetWallpaper(dlg.FileName))
            {
                Settings.Save();
                TextWallpaper.Text = System.IO.Path.GetFileName(Settings.WallpaperPath);
                var mw = Owner as MainWindow;
                if (mw != null) mw.ApplyWallpaper();
            }
            else System.Windows.MessageBox.Show("这张图片无法读取，换一张试试。", "提示");
        }

        private void BtnClearWallpaper_Click(object sender, RoutedEventArgs e)
        {
            Settings.ClearWallpaper();
            Settings.Save();
            TextWallpaper.Text = "未设置";
            var mw = Owner as MainWindow;
            if (mw != null) mw.ApplyWallpaper();
        }

        /// <summary>透明度滑杆：实时生效（0~100%）</summary>
        private void SliderWallpaper_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Settings == null || TextWallpaperOpacity == null) return;
            Settings.WallpaperOpacity = e.NewValue;
            TextWallpaperOpacity.Text = (int)System.Math.Round(e.NewValue * 100) + "%";
            var mw = Owner as MainWindow;
            if (mw != null) mw.ApplyWallpaper();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string nick = InputNickname.Text.Trim();
            if (string.IsNullOrEmpty(nick)) nick = "神秘人";

            Settings.Nickname = nick;
            Settings.SendWithEnter = RadioEnter.IsChecked == true;
            Settings.FontSize = RadioSmall.IsChecked == true ? 11 : (RadioLarge.IsChecked == true ? 15 : 13);
            Settings.TopMost = CheckTopMost.IsChecked == true;
            Settings.ClearOnExit = CheckClearOnExit.IsChecked == true;
            Settings.Save();

            (Owner as MainWindow)?.ApplyWallpaper();
            DialogResult = true;
            Close();
        }

        /// <summary>重置昵称：清掉注册标记，下次启动会重新弹注册页（豆包需求 #2）</summary>
        private void BtnResetNick_Click(object sender, RoutedEventArgs e)
        {
            var r = System.Windows.MessageBox.Show("重置后将清空昵称，并在下次启动时重新弹出注册界面。继续？",
                "重置昵称", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            // ★ 必须改"主窗口那份 Settings 实例"，不能 Load() 一份新的：
            //   主窗口持有的是启动时读进内存的对象，关闭时 OnClosed 会把它 Save() 回磁盘；
            //   若只改磁盘，关闭时就被内存里的旧值（神秘人 + Registered=true）覆盖回去 ——
            //   这正是"重置后下次启动没重新注册、直接用神秘人登录"的根因。
            Settings.Nickname = "";
            Settings.Registered = false;
            Settings.Save();
            InputNickname.Text = "";
            System.Windows.MessageBox.Show("已重置，重启程序后生效。", "提示");
        }

        /// <summary>无边框窗口拖动</summary>
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
            try { DragMove(); } catch (System.InvalidOperationException) { }
        }

        /// <summary>关于（豆包需求 #4）</summary>
        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        /// <summary>使用教程（豆包需求 #3）—— 内容页随后补上</summary>
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show(
                "基础操作：\n  输入文字后回车发送；点 📎 发图片/文件；点 😀 选表情。\n\n" +
                "快捷键：\n  Ctrl+F 搜索聊天记录\n  Ctrl+E 表情面板\n  Esc 逐级关闭（表情 → 搜索 → 清空输入）\n\n" +
                "常见问题：\n  收不到消息：确认在同一网段、且两端 ChatRoom 版本一致。",
                "使用教程", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
