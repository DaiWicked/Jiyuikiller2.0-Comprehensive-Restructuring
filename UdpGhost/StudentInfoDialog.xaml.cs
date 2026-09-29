using System.Windows;

namespace UdpGhost
{
    public partial class StudentInfoDialog : Window
    {
        public StudentInfoDialog(string info)
        {
            InitializeComponent();
            InfoBox.Text = info;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
