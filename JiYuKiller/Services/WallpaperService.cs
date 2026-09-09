using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 自定义壁纸服务
    /// 壁纸放在白色覆盖层上方，桌面截图下方
    /// 壁纸和桌面截图都被玻璃效果模糊
    /// </summary>
    public class WallpaperService
    {
        public const long MaxFileSize = 10 * 1024 * 1024;
        public const int TargetWidth = 450;
        public const int TargetHeight = 600;
        public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        private BitmapSource _cachedWallpaper;
        private string _cachedPath;

        public bool IsValid { get; private set; }
        public string LastError { get; private set; }

        public bool ValidateWallpaper(string path)
        {
            LastError = "";
            if (string.IsNullOrEmpty(path)) { LastError = "壁纸路径为空"; IsValid = false; return false; }
            if (!File.Exists(path)) { LastError = "壁纸文件不存在"; IsValid = false; return false; }
            string ext = Path.GetExtension(path).ToLower();
            bool supported = false;
            foreach (string s in SupportedExtensions) { if (ext == s) { supported = true; break; } }
            if (!supported) { LastError = "不支持的格式: " + ext; IsValid = false; return false; }
            FileInfo fi = new FileInfo(path);
            if (fi.Length > MaxFileSize) { LastError = "图片过大(最大10MB)"; IsValid = false; return false; }
            IsValid = true;
            return true;
        }

        public BitmapSource LoadWallpaper(string path)
        {
            if (!ValidateWallpaper(path)) return null;
            if (_cachedWallpaper != null && _cachedPath == path) return _cachedWallpaper;

            try
            {
                Logger.Instance.FunctionCall("WallpaperService.LoadWallpaper");
                BitmapImage original = new BitmapImage();
                original.BeginInit();
                original.UriSource = new Uri(path, UriKind.Absolute);
                original.CacheOption = BitmapCacheOption.OnLoad;
                original.EndInit();
                original.Freeze();

                double scaleX = (double)TargetWidth / original.PixelWidth;
                double scaleY = (double)TargetHeight / original.PixelHeight;
                double scale = Math.Max(scaleX, scaleY);
                int scaledW = (int)(original.PixelWidth * scale);
                int scaledH = (int)(original.PixelHeight * scale);
                int cropX = (scaledW - TargetWidth) / 2;
                int cropY = (scaledH - TargetHeight) / 2;
                if (cropX < 0) cropX = 0;
                if (cropY < 0) cropY = 0;

                TransformedBitmap scaled = new TransformedBitmap(original, new ScaleTransform(scale, scale));
                CroppedBitmap cropped = new CroppedBitmap(scaled, new Int32Rect(cropX, cropY, Math.Min(scaledW, TargetWidth), Math.Min(scaledH, TargetHeight)));
                cropped.Freeze();

                _cachedWallpaper = cropped;
                _cachedPath = path;
                return cropped;
            }
            catch (Exception ex)
            {
                LastError = "加载失败: " + ex.Message;
                IsValid = false;
                return null;
            }
        }

        public void ClearCache()
        {
            _cachedWallpaper = null;
            _cachedPath = null;
            IsValid = false;
        }

        public BitmapSource GetThumbnail(string path, int maxWidth = 150, int maxHeight = 100)
        {
            if (!ValidateWallpaper(path)) return null;
            try
            {
                BitmapImage thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.UriSource = new Uri(path, UriKind.Absolute);
                thumb.DecodePixelWidth = maxWidth;
                thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.EndInit();
                thumb.Freeze();
                return thumb;
            }
            catch { return null; }
        }
    }
}
