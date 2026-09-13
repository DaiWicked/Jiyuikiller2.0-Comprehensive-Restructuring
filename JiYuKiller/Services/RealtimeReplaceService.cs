using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 实时屏幕替换服务
    /// 通过配置文件+YUV数据文件与DLL通信
    /// DLL在Encode时读取配置并替换YUV输入帧
    /// </summary>
    public class RealtimeReplaceService
    {
        private const string ConfigDir = @"C:\Users\Public\JiYuKiller";
        private const string ConfigPath = @"C:\Users\Public\JiYuKiller\realtime_replace.ini";
        private const string YuvPath = @"C:\Users\Public\JiYuKiller\fake_screen.yuv";

        // 默认编码尺寸（最常见）
        public const int DefaultWidth = 1024;
        public const int DefaultHeight = 768;

        public string CurrentImagePath { get; private set; } = "";
        public bool IsEnabled { get; private set; } = false;

        public event Action<string> OnLog;
        public event Action<string> OnStatusChanged;

        private void Log(string msg) => OnLog?.Invoke(msg);
        private void Status(string msg) => OnStatusChanged?.Invoke(msg);

        /// <summary>
        /// 确保配置目录存在
        /// </summary>
        private void EnsureDir()
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);
        }

        /// <summary>
        /// 选择图片
        /// </summary>
        public bool ChooseImage(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                Status("图片文件不存在");
                return false;
            }
            CurrentImagePath = imagePath;
            Status("已选择图片: " + Path.GetFileName(imagePath));
            Log("选择图片: " + imagePath);
            return true;
        }

        /// <summary>
        /// 应用实时替换：图片转YUV420并保存，写配置文件
        /// </summary>
        public bool Apply(int width = DefaultWidth, int height = DefaultHeight)
        {
            if (string.IsNullOrEmpty(CurrentImagePath) || !File.Exists(CurrentImagePath))
            {
                Status("请先选择图片");
                return false;
            }

            try
            {
                EnsureDir();
                Log($"开始转换图片为YUV420: {width}x{height}");

                // 加载并缩放图片
                using (var src = new Bitmap(CurrentImagePath))
                using (var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, 0, 0, width, height);

                    // 锁定位图数据
                    var rect = new Rectangle(0, 0, width, height);
                    var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                    int stride = bmpData.Stride;
                    IntPtr ptr = bmpData.Scan0;
                    int bytes = stride * height;
                    byte[] rgbData = new byte[bytes];
                    Marshal.Copy(ptr, rgbData, 0, bytes);
                    bmp.UnlockBits(bmpData);

                    // 转YUV420 (I420)
                    byte[] yuv = ConvertToYUV420(rgbData, width, height, stride);

                    // 保存YUV文件
                    File.WriteAllBytes(YuvPath, yuv);
                    Log($"YUV文件已保存: {YuvPath} ({yuv.Length} bytes)");
                }

                // 写配置文件
                WriteIni("realtime", "enabled", "1");
                WriteIni("realtime", "width", width.ToString());
                WriteIni("realtime", "height", height.ToString());
                WriteIni("realtime", "image", CurrentImagePath);

                IsEnabled = true;
                Status("实时替换已启用，教师端观看时生效");
                Log("实时替换配置已写入: " + ConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                Log("应用失败: " + ex.Message);
                Status("应用失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 取消实时替换
        /// </summary>
        public void Disable()
        {
            try
            {
                EnsureDir();
                WriteIni("realtime", "enabled", "0");
                IsEnabled = false;
                Status("实时替换已关闭");
                Log("实时替换已关闭");
            }
            catch (Exception ex)
            {
                Log("关闭失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 清除选择
        /// </summary>
        public void Clear()
        {
            Disable();
            CurrentImagePath = "";
            try { if (File.Exists(YuvPath)) File.Delete(YuvPath); } catch { }
            Status("已清除");
        }

        /// <summary>
        /// 加载当前状态
        /// </summary>
        public void LoadCurrent()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string enabled = ReadIni("realtime", "enabled", "0");
                    IsEnabled = enabled == "1";
                    CurrentImagePath = ReadIni("realtime", "image", "");
                }
            }
            catch { }
        }

        /// <summary>
        /// RGB24转YUV420 (YV12格式: Y + V + U)
        /// 极域编码器使用YV12（V平面在前，U平面在后）
        /// 标准I420是Y+U+V，直接输出会导致颜色负片
        /// </summary>
        private byte[] ConvertToYUV420(byte[] rgb, int width, int height, int stride)
        {
            int ySize = width * height;
            int uvSize = width * height / 4;
            byte[] yuv = new byte[ySize + uvSize * 2];

            // Y平面
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * stride + x * 3;
                    byte b = rgb[idx];
                    byte g = rgb[idx + 1];
                    byte r = rgb[idx + 2];
                    // BT.601
                    int yVal = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                    yuv[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }

            // V平面 (Cr) 在前 - YV12格式
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double vSum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = Math.Min(x + dx, width - 1);
                            int py = Math.Min(y + dy, height - 1);
                            int idx = py * stride + px * 3;
                            byte b = rgb[idx];
                            byte g = rgb[idx + 1];
                            byte r = rgb[idx + 2];
                            vSum += 0.5 * r - 0.419 * g - 0.081 * b + 128;
                        }
                    }
                    int vVal = (int)(vSum / 4);
                    yuv[ySize + (y / 2) * (width / 2) + (x / 2)] = (byte)Math.Max(0, Math.Min(255, vVal));
                }
            }

            // U平面 (Cb) 在后 - YV12格式
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double uSum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = Math.Min(x + dx, width - 1);
                            int py = Math.Min(y + dy, height - 1);
                            int idx = py * stride + px * 3;
                            byte b = rgb[idx];
                            byte g = rgb[idx + 1];
                            byte r = rgb[idx + 2];
                            uSum += -0.169 * r - 0.331 * g + 0.5 * b + 128;
                        }
                    }
                    int uVal = (int)(uSum / 4);
                    yuv[ySize + uvSize + (y / 2) * (width / 2) + (x / 2)] = (byte)Math.Max(0, Math.Min(255, uVal));
                }
            }

            return yuv;
        }

        #region INI读写

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string lpAppName, string lpKeyName,
            string lpDefault, StringBuilder lpReturnedString, uint nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string lpAppName, string lpKeyName,
            string lpString, string lpFileName);

        private string ReadIni(string section, string key, string def)
        {
            var sb = new StringBuilder(256);
            GetPrivateProfileString(section, key, def, sb, 256, ConfigPath);
            return sb.ToString();
        }

        private void WriteIni(string section, string key, string val)
        {
            WritePrivateProfileString(section, key, val, ConfigPath);
        }

        #endregion
    }
}
