using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 截图替换服务（编码层替换方案）
    /// 原理：将用户选择的图片缩放到80x60并编码为JPEG，保存到fake_screenshot.jpg，
    /// 将路径写入INI文件[JTSettings]节的FakeJpegPath键，
    /// DLL的hkEncodeToJPEGBuffer在编码完成后用该JPEG替换输出缓冲区
    /// </summary>
    public class ScreenshotService
    {
        public event Action<string> OnLog;
        public event Action<string> OnStatusChanged;

        private string _currentImagePath = "";
        private readonly string _iniPath;

        public string CurrentImagePath => _currentImagePath;

        public ScreenshotService()
        {
            _iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i.chaoxing.ini");
            Logger.Instance.Info("[ScreenshotService] 截图替换服务初始化，INI: " + _iniPath);
        }

        /// <summary>
        /// 加载当前已设置的截图图片
        /// </summary>
        public void LoadCurrent()
        {
            try
            {
                _currentImagePath = GetIniString("JTSettings", "FakeScreenImage", "");
                if (!string.IsNullOrEmpty(_currentImagePath) && File.Exists(_currentImagePath))
                {
                    Logger.Instance.Info("[ScreenshotService] 当前已设置截图: " + _currentImagePath);
                    OnLog?.Invoke("当前已设置截图: " + _currentImagePath);
                    OnStatusChanged?.Invoke("已设置截图替换图片，教师端监控时将显示此图片。");
                }
                else
                {
                    _currentImagePath = "";
                    Logger.Instance.Info("[ScreenshotService] 尚未设置截图替换图片");
                    OnLog?.Invoke("尚未设置截图替换图片");
                    OnStatusChanged?.Invoke("未设置截图替换。");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 加载设置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 选择图片
        /// </summary>
        public void ChooseImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                OnStatusChanged?.Invoke("图片文件不存在。");
                return;
            }

            string ext = Path.GetExtension(imagePath).ToLower();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp")
            {
                OnStatusChanged?.Invoke("不支持的图片格式，请选择png/jpg/jpeg/bmp。");
                return;
            }

            _currentImagePath = imagePath;
            Logger.Instance.Info("[ScreenshotService] 已选择图片: " + imagePath);
            OnLog?.Invoke("已选择图片: " + imagePath);
            OnStatusChanged?.Invoke("已选择图片，点击「应用」后生效。");
        }

        /// <summary>
        /// 清除图片
        /// </summary>
        public void ClearImage()
        {
            _currentImagePath = "";
            Logger.Instance.Info("[ScreenshotService] 已清除截图替换图片");
            OnLog?.Invoke("已清除截图替换图片");
            OnStatusChanged?.Invoke("已清除截图替换，点击「应用」后生效。");
        }

        /// <summary>
        /// 生成80x60假JPEG图片
        /// </summary>
        private string GenerateFakeJpeg()
        {
            if (string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
                return "";

            try
            {
                string fakeJpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fake_screenshot.jpg");
                using (Bitmap src = new Bitmap(_currentImagePath))
                {
                    using (Bitmap dst = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(dst))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                            g.Clear(Color.Black);
                            // 按比例缩放居中
                            float ratio = Math.Min(80f / src.Width, 60f / src.Height);
                            int w = (int)(src.Width * ratio);
                            int h = (int)(src.Height * ratio);
                            int x = (80 - w) / 2;
                            int y = (60 - h) / 2;
                            g.DrawImage(src, x, y, w, h);
                        }
                        ImageCodecInfo jpegCodec = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.FormatID == ImageFormat.Jpeg.Guid);
                        EncoderParameters encParams = new EncoderParameters(1);
                        encParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                        dst.Save(fakeJpegPath, jpegCodec, encParams);
                    }
                }
                FileInfo fi = new FileInfo(fakeJpegPath);
                Logger.Instance.Info("[ScreenshotService] 已生成假JPEG: " + fakeJpegPath + " (" + fi.Length + " bytes)");
                return fakeJpegPath;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 生成假JPEG失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 应用设置（写入INI并通知DLL重新读取）
        /// </summary>
        public bool Apply(JiYuController controller)
        {
            try
            {
                string fakeJpegPath = "";
                if (!string.IsNullOrEmpty(_currentImagePath))
                {
                    fakeJpegPath = GenerateFakeJpeg();
                }

                // 写入INI文件（编码层替换用FakeJpegPath，兼容GDI层保留FakeScreenImage）
                WritePrivateProfileString("JTSettings", "FakeJpegPath", fakeJpegPath, _iniPath);
                WritePrivateProfileString("JTSettings", "FakeScreenImage", _currentImagePath, _iniPath);
                Logger.Instance.Info("[ScreenshotService] 已应用截图替换: FakeJpegPath=" + fakeJpegPath);
                OnLog?.Invoke("已应用: " + (string.IsNullOrEmpty(_currentImagePath) ? "清除截图替换" : _currentImagePath));

                // 通知DLL重新读取设置
                if (controller != null && controller.IsVirusInstalled)
                {
                    controller.SendVirusMessage("hk:inipath:" + _iniPath);
                    Logger.Instance.Info("[ScreenshotService] 已通知DLL重新读取设置, 路径=" + _iniPath);
                }
                else
                {
                    Logger.Instance.Warn("[ScreenshotService] DLL未注入, 跳过通知 (controller=" + (controller != null) + ", installed=" + (controller != null && controller.IsVirusInstalled) + ")");
                }

                if (_currentImagePath == "")
                    OnStatusChanged?.Invoke("已清除截图替换。");
                else
                    OnStatusChanged?.Invoke("已应用截图替换设置。");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 应用失败: " + ex.Message);
                OnStatusChanged?.Invoke("应用失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 加载图片为BitmapImage用于预览
        /// </summary>
        public BitmapImage LoadPreviewImage()
        {
            if (string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
                return null;

            try
            {
                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(_currentImagePath);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 加载预览图片失败: " + ex.Message);
                return null;
            }
        }

        #region Win32 API
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string lpAppName, string lpKeyName, string lpDefault,
            System.Text.StringBuilder lpReturnedString, int nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string lpAppName, string lpKeyName, string lpString, string lpFileName);

        private string GetIniString(string section, string key, string defaultValue)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            GetPrivateProfileString(section, key, defaultValue, sb, 512, _iniPath);
            return sb.ToString();
        }
        #endregion
    }
}
