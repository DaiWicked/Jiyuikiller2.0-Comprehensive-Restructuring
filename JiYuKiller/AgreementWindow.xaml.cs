using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JiYuKiller
{
    public partial class AgreementWindow : Window
    {
        public bool IsAgreed { get; private set; } = false;

        /// <summary>用户是否通过按钮做出了明确选择</summary>
        private bool _choiceMade = false;

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

                // 窗口在虚拟屏幕中的矩形。
                // 必须用 PointToScreen: 它返回的是屏幕物理像素, 与 GetSystemMetrics 的虚拟屏坐标同一坐标系。
                // 原实现用 this.Left/Top/Width/Height(DIP) 乘 dpiX/96 去和物理坐标相减, 属于两种坐标系混算,
                // 且 CroppedBitmap 由 FromWidthAndHeight 构造, DpiX 恒为 96, scale 实际恒等于 1, 在非 100% 缩放下必然错位。
                Point topLeft = this.PointToScreen(new Point(0, 0));
                double w = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
                double h = this.ActualHeight > 0 ? this.ActualHeight : this.Height;
                Point bottomRight = this.PointToScreen(new Point(w, h));

                int winX = (int)Math.Round(topLeft.X) - Services.ScreenCaptureHelper.VirtualScreenX;
                int winY = (int)Math.Round(topLeft.Y) - Services.ScreenCaptureHelper.VirtualScreenY;
                int winW = (int)Math.Round(bottomRight.X - topLeft.X);
                int winH = (int)Math.Round(bottomRight.Y - topLeft.Y);

                // 边界检查
                if (winX < 0)
                {
                    winW += winX;
                    winX = 0;
                }
                if (winY < 0)
                {
                    winH += winY;
                    winY = 0;
                }
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

                Services.Logger.Instance.Info($"[协议] 玻璃背景已设置, 区域=({winX},{winY},{winW}x{winH}), 截图尺寸={fullScreen.PixelWidth}x{fullScreen.PixelHeight}, 模糊半径=20");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error($"[协议] 设置玻璃背景失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 非按钮途径关闭窗口(Alt+F4 / 系统关闭)。
        /// 原实现下这条路径会静默地按"拒绝协议"处理并直接退出程序, 日志里看不出原因。
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_choiceMade)
            {
                IsAgreed = false;
                Services.Logger.Instance.Warn("[协议] 窗口被直接关闭(Alt+F4/系统关闭), 视为拒绝协议");
            }
        }

        private void BtnAgree_Click(object sender, RoutedEventArgs e)
        {
            _choiceMade = true;
            IsAgreed = true;
            Services.Logger.Instance.Info("[协议] 用户点击'极域给我爬', 同意协议");
            this.Close();
        }

        private void BtnDisagree_Click(object sender, RoutedEventArgs e)
        {
            _choiceMade = true;
            IsAgreed = false;
            Services.Logger.Instance.Info("[协议] 用户点击'杰哥不要', 拒绝协议");
            this.Close();
        }
    }
}
