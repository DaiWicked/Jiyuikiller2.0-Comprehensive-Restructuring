using System.Windows;

namespace JiYuKiller
{
    public partial class AgreementWindow : Window
    {
        public bool IsAgreed { get; private set; } = false;

        public AgreementWindow()
        {
            InitializeComponent();
            Services.Logger.Instance.Info("[协议] 用户许可协议窗口已打开");
        }

        private void BtnAgree_Click(object sender, RoutedEventArgs e)
        {
            IsAgreed = true;
            Services.Logger.Instance.Info("[协议] 用户点击'极域给我爬', 同意协议");
            this.Close();
        }

        private void BtnDisagree_Click(object sender, RoutedEventArgs e)
        {
            IsAgreed = false;
            Services.Logger.Instance.Info("[协议] 用户点击'杰哥不要', 拒绝协议");
            this.Close();
        }
    }
}
