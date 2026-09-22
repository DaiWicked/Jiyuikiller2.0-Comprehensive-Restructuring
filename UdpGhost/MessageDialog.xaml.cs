using System.Windows;
using System.Windows.Input;

namespace UdpGhost
{
    public partial class MessageDialog : Window
    {
        public string Message { get; private set; }

        public MessageDialog()
        {
            InitializeComponent();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Message = MsgBox.Text;
            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void MsgBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Ctrl+回车：插入换行
                    e.Handled = false;
                }
                else
                {
                    // 回车：发送
                    e.Handled = true;
                    Send_Click(sender, e);
                }
            }
        }
    }
}
