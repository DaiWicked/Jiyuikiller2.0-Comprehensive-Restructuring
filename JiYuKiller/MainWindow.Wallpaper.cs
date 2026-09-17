using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region 自定义壁纸

        private Services.WallpaperService _wallpaperService = new Services.WallpaperService();
        private string _pendingWallpaperPath = "";

        private void BtnWallpaperSelect_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("选择壁纸", "BtnWallpaperSelect");
            try
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Title = "选择壁纸图片";
                dlg.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (dlg.ShowDialog() == true)
                {
                    _pendingWallpaperPath = dlg.FileName;
                    if (_wallpaperService.ValidateWallpaper(dlg.FileName))
                    {
                        BitmapSource thumb = _wallpaperService.GetThumbnail(dlg.FileName);
                        if (thumb != null) ImageWallpaperPreview.Source = thumb;
                        TextWallpaperError.Text = "";
                    }
                    else { TextWallpaperError.Text = _wallpaperService.LastError; }
                }
            }
            catch (Exception ex) { TextWallpaperError.Text = "选择失败: " + ex.Message; }
        }

        private void BtnWallpaperApply_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("应用壁纸", "BtnWallpaperApply");
            string path = !string.IsNullOrEmpty(_pendingWallpaperPath) ? _pendingWallpaperPath : _settings.WallpaperPath;
            if (string.IsNullOrEmpty(path)) { TextWallpaperError.Text = "请先选择壁纸"; return; }
            ApplyWallpaper(path);
        }

        private void BtnWallpaperReset_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("恢复默认", "BtnWallpaperReset");
            _settings.WallpaperPath = "";
            _pendingWallpaperPath = "";
            _settings.Save();
            ImageWallpaperPreview.Source = null;
            TextWallpaperError.Text = "";
            if (WallpaperLayer != null)
            {
                WallpaperLayer.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            }
            Services.CrashReportService.UpdateWallpaperInfo("", false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
        }

        private void ApplyWallpaper(string path)
        {
            try
            {
                BitmapSource wallpaper = _wallpaperService.LoadWallpaper(path);
                if (wallpaper == null)
                {
                    TextWallpaperError.Text = _wallpaperService.LastError;
                    Services.CrashReportService.UpdateWallpaperInfo(path, false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
                    return;
                }
                if (WallpaperLayer != null)
                {
                    WallpaperLayer.Background = new ImageBrush(wallpaper) { Stretch = Stretch.Fill };
                }
                _settings.WallpaperPath = path;
                _settings.Save();
                TextWallpaperError.Text = "";
                Services.CrashReportService.UpdateWallpaperInfo(path, true, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
            }
            catch (Exception ex)
            {
                TextWallpaperError.Text = "应用失败: " + ex.Message;
                Services.CrashReportService.UpdateWallpaperInfo(path, false, WallpaperLayer?.Opacity ?? 1.0, 255, _settings?.GlassOpacity ?? 72);
            }
        }

        /// <summary>
        /// 初始化噪声层（借鉴COUI noiseCoefficient=0.0045, 抗色带）
        /// 程序化生成128x128灰度噪声纹理, ImageBrush平铺
        /// </summary>
        private void InitNoiseLayer()
        {
            try
            {
                const int size = 128;
                var bmp = new System.Windows.Media.Imaging.WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Gray8, null);
                var pixels = new byte[size * size];
                var rand = new Random(42); // 固定种子保证一致
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (byte)rand.Next(0, 256);
                bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size, 0);
                var brush = new System.Windows.Media.ImageBrush(bmp);
                brush.TileMode = System.Windows.Media.TileMode.Tile;
                brush.Viewport = new System.Windows.Rect(0, 0, size, size);
                brush.ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NoiseLayer.Background = brush;
                Services.Logger.Instance.Debug("噪声层初始化成功, 128x128平铺");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("噪声层初始化失败", ex);
            }
        }

        private void InitWallpaper()
        {
            if (!string.IsNullOrEmpty(_settings.WallpaperPath))
            {
                if (_wallpaperService.ValidateWallpaper(_settings.WallpaperPath))
                {
                    BitmapSource thumb = _wallpaperService.GetThumbnail(_settings.WallpaperPath);
                    if (thumb != null) ImageWallpaperPreview.Source = thumb;
                    ApplyWallpaper(_settings.WallpaperPath);
                }
                else { TextWallpaperError.Text = "壁纸文件已丢失，请重新选择或恢复默认"; }
            }
        }

        #endregion
    }
}