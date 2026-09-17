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

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
