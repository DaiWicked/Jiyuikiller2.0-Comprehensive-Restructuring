using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 捕获全屏桌面
                Services.ScreenCaptureHelper.CaptureFullScreen();
                var fullScreen = Services.ScreenCaptureHelper.FullScreenSnapshot;
                if (fullScreen == null)
                {
                    Services.Logger.Instance.Warn("[协议] 桌面截图为空, 使用纯色背景");
                    return;
                }

                // 计算窗口在虚拟屏幕中的位置
                double dpiX = fullScreen.DpiX;
                double dpiY = fullScreen.DpiY;
                double scaleX = dpiX / 96.0;
                double scaleY = dpiY / 96.0;

                int winX = (int)((this.Left - Services.ScreenCaptureHelper.VirtualScreenX) * scaleX);
                int winY = (int)((this.Top - Services.ScreenCaptureHelper.VirtualScreenY) * scaleY);
                int winW = (int)(this.Width * scaleX);
                int winH = (int)(this.Height * scaleY);

                // 边界检查
                if (winX < 0) winX = 0;
                if (winY < 0) winY = 0;
                if (winX + winW > fullScreen.PixelWidth) winW = fullScreen.PixelWidth - winX;
                if (winY + winH > fullScreen.PixelHeight) winH = fullScreen.PixelHeight - winY;

                if (winW <= 0 || winH <= 0)
                {
                    Services.Logger.Instance.Warn("[协议] 窗口区域计算无效, 使用纯色背景");
                    return;
                }

                // 裁剪窗口区域
                var cropped = new CroppedBitmap(fullScreen, new Int32Rect(winX, winY, winW, winH));
                BackdropImage.Source = cropped;

                Services.Logger.Instance.Info($"[协议] 玻璃背景已设置, 区域=({winX},{winY},{winW},{winH}), 模糊半径=20");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error($"[协议] 设置玻璃背景失败: {ex.Message}");
            }
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
