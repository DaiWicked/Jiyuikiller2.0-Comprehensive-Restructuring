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

            DialogResult = true;
            Close();
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
