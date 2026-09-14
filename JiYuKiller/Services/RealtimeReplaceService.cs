using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 实时屏幕替换服务
    /// 支持静态图片和视频（用ffmpeg实时解码循环播放）
    /// 通过配置文件+YUV数据文件与DLL通信
    /// </summary>
    public class RealtimeReplaceService
    {
        private const string ConfigDir = @"C:\Users\Public\JiYuKiller";
        private const string ConfigPath = @"C:\Users\Public\JiYuKiller\realtime_replace.ini";
        private const string YuvPath = @"C:\Users\Public\JiYuKiller\fake_screen.yuv";

        public const int DefaultWidth = 1024;
        public const int DefaultHeight = 768;
        private const int MaxVideoSizeMB = 100; // 视频大小限制

        public string CurrentImagePath { get; private set; } = "";
        public string CurrentVideoPath { get; private set; } = "";
        public bool IsEnabled { get; private set; } = false;
        public bool IsVideoMode { get; private set; } = false;

        public event Action<string> OnLog;
        public event Action<string> OnStatusChanged;

        private Thread _videoThread;
        private volatile bool _videoRunning;
        private Process _ffmpegProcess;

        private void Log(string msg) => OnLog?.Invoke(msg);
        private void Status(string msg) => OnStatusChanged?.Invoke(msg);

        private void EnsureDir()
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);
        }

        public bool ChooseImage(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                Status("图片文件不存在");
                return false;
            }
            StopVideoThread();
            CurrentImagePath = imagePath;
            CurrentVideoPath = "";
            IsVideoMode = false;
            Log("已选择图片: " + imagePath);
            Status("已选择图片，点击启用后生效");
            return true;
        }

        public bool ChooseVideo(string videoPath)
        {
            if (!File.Exists(videoPath))
            {
                Status("视频文件不存在");
                return false;
            }
            FileInfo fi = new FileInfo(videoPath);
            if (fi.Length > MaxVideoSizeMB * 1024 * 1024)
            {
                Status($"视频文件过大（{fi.Length / 1024 / 1024}MB），请选择小于{MaxVideoSizeMB}MB的视频");
                return false;
            }
            StopVideoThread();
            CurrentVideoPath = videoPath;
            CurrentImagePath = "";
            IsVideoMode = true;
            Log("已选择视频: " + videoPath + $" ({fi.Length / 1024 / 1024}MB)");
            Status("已选择视频，点击启用后循环播放");
            return true;
        }

        private string FindFFmpeg()
        {
            bool is64Bit = Environment.Is64BitOperatingSystem;
            Log($"系统位数: {(is64Bit ? "64位" : "32位")}");

            // 64位系统优先使用64位ffmpeg（性能更好），其次32位
            string[] candidates;
            if (is64Bit)
            {
                candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg64.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "ffmpeg64.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "ffmpeg.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "teacher_sim", "ffmpeg.exe"),
                };
            }
            else
            {
                candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "ffmpeg.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "teacher_sim", "ffmpeg.exe"),
                };
            }
            foreach (var p in candidates)
            {
                if (File.Exists(p))
                {
                    Log($"使用ffmpeg: {p}");
                    return p;
                }
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("where", "ffmpeg")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    string line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (File.Exists(line)) return line;
                }
            }
            catch { }
            return "";
        }

        public bool Apply()
        {
            try
            {
                EnsureDir();
                if (IsVideoMode)
                    return ApplyVideo();
                else
                    return ApplyImage();
            }
            catch (Exception ex)
            {
                Log("启用失败: " + ex.Message);
                Status("启用失败: " + ex.Message);
                return false;
            }
        }

        private bool ApplyImage()
        {
            if (string.IsNullOrEmpty(CurrentImagePath) || !File.Exists(CurrentImagePath))
            {
                Status("请先选择图片");
                return false;
            }
            Log("开始转换图片为YUV420(YV12): " + DefaultWidth + "x" + DefaultHeight);

            using (var src = new Bitmap(CurrentImagePath))
            using (var bmp = new Bitmap(DefaultWidth, DefaultHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, DefaultWidth, DefaultHeight);

                var rect = new Rectangle(0, 0, DefaultWidth, DefaultHeight);
                var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                int stride = bmpData.Stride;
                IntPtr ptr = bmpData.Scan0;
                int bytes = stride * DefaultHeight;
                byte[] rgbData = new byte[bytes];
                Marshal.Copy(ptr, rgbData, 0, bytes);
                bmp.UnlockBits(bmpData);

                byte[] yuv = ConvertToYUV420(rgbData, DefaultWidth, DefaultHeight, stride);
                File.WriteAllBytes(YuvPath, yuv);
            }

            WriteIni("realtime", "enabled", "1");
            WriteIni("realtime", "width", DefaultWidth.ToString());
            WriteIni("realtime", "height", DefaultHeight.ToString());
            WriteIni("realtime", "image", CurrentImagePath);
            WriteIni("realtime", "video", "");

            IsEnabled = true;
            IsVideoMode = false;
            Status("实时替换已启用（图片）");
            Log("实时替换已启用，YUV文件: " + YuvPath);
            return true;
        }

        private bool ApplyVideo()
        {
            if (string.IsNullOrEmpty(CurrentVideoPath) || !File.Exists(CurrentVideoPath))
            {
                Status("请先选择视频");
                return false;
            }
            string ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Status("未找到ffmpeg.exe，无法播放视频");
                Log("错误: 未找到ffmpeg.exe");
                return false;
            }

            Log("使用ffmpeg: " + ffmpeg);
            Log("开始视频解码循环: " + CurrentVideoPath);

            // 先删除旧的YUV文件，确保视频解码线程从头开始写
            try { if (File.Exists(YuvPath)) File.Delete(YuvPath); } catch { }

            WriteIni("realtime", "enabled", "1");
            WriteIni("realtime", "width", DefaultWidth.ToString());
            WriteIni("realtime", "height", DefaultHeight.ToString());
            WriteIni("realtime", "image", "");
            WriteIni("realtime", "video", CurrentVideoPath);

            StopVideoThread();
            _videoRunning = true;
            _videoThread = new Thread(() => VideoDecodeLoop(ffmpeg, CurrentVideoPath))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _videoThread.Start();

            IsEnabled = true;
            IsVideoMode = true;
            Status("实时替换已启用（视频循环播放）");
            return true;
        }

        private const int DecodeWidth = 640;
        private const int DecodeHeight = 480;

        private void VideoDecodeLoop(string ffmpegPath, string videoPath)
        {
            int srcFrameSize = DecodeWidth * DecodeHeight * 3 / 2;
            byte[] srcYuv = new byte[srcFrameSize];
            byte[] dstYuv = new byte[DefaultWidth * DefaultHeight * 3 / 2];
            int frameCount = 0;

            while (_videoRunning)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        // rawvideo YUV420P 320x240，无编码开销，性能最好
                        Arguments = $"-stream_loop -1 -i \"{videoPath}\" -s {DecodeWidth}x{DecodeHeight} -pix_fmt yuv420p -r 15 -f rawvideo -",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    _ffmpegProcess = Process.Start(psi);
                    _ffmpegProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data) && e.Data.Contains("frame=") && frameCount % 30 == 0)
                            Log("[ffmpeg] " + e.Data);
                    };
                    _ffmpegProcess.BeginErrorReadLine();

                    var stdout = _ffmpegProcess.StandardOutput.BaseStream;
                    while (_videoRunning && !_ffmpegProcess.HasExited)
                    {
                        int read = 0;
                        while (read < srcFrameSize && _videoRunning)
                        {
                            int r = stdout.Read(srcYuv, read, srcFrameSize - read);
                            if (r <= 0) break;
                            read += r;
                        }

                        if (read >= srcFrameSize && _videoRunning)
                        {
                            // YUV420P最近邻缩放到1024x768，并交换U/V(I420->YV12)
                            ScaleYUV420ToYV12(srcYuv, DecodeWidth, DecodeHeight, dstYuv, DefaultWidth, DefaultHeight);
                            try
                            {
                                File.WriteAllBytes(YuvPath, dstYuv);
                                frameCount++;
                                if (frameCount % 30 == 0)
                                    Log($"视频已播放 {frameCount} 帧");
                            }
                            catch (Exception ex)
                            {
                                Log("写入YUV失败: " + ex.Message);
                            }
                            // 不Sleep，由ffmpeg的-r 15控制帧率
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (!_ffmpegProcess.HasExited)
                    {
                        try { _ffmpegProcess.Kill(); } catch { }
                    }
                    _ffmpegProcess.WaitForExit(1000);
                }
                catch (Exception ex)
                {
                    Log("视频解码异常: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
            Log("视频解码线程已退出，共播放 " + frameCount + " 帧");
        }

        /// <summary>
        /// YUV420P(I420)最近邻缩放到目标尺寸，并输出YV12(V在前U在后)
        /// </summary>
        private static void ScaleYUV420ToYV12(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
        {
            int srcYSize = sw * sh;
            int srcUVSize = srcYSize / 4;
            int dstYSize = dw * dh;
            int dstUVSize = dstYSize / 4;

            // Y平面缩放
            for (int y = 0; y < dh; y++)
            {
                int sy = y * sh / dh;
                for (int x = 0; x < dw; x++)
                {
                    int sx = x * sw / dw;
                    dst[y * dw + x] = src[sy * sw + sx];
                }
            }
            // V平面 (YV12: V在前，源I420中U在srcYSize, V在srcYSize+srcUVSize)
            int srcUOff = srcYSize;
            int srcVOff = srcYSize + srcUVSize;
            int dstVOff = dstYSize;
            int dstUOff = dstYSize + dstUVSize;
            for (int y = 0; y < dh / 2; y++)
            {
                int sy = y * (sh / 2) / (dh / 2);
                for (int x = 0; x < dw / 2; x++)
                {
                    int sx = x * (sw / 2) / (dw / 2);
                    dst[dstVOff + y * (dw / 2) + x] = src[srcVOff + sy * (sw / 2) + sx];
                    dst[dstUOff + y * (dw / 2) + x] = src[srcUOff + sy * (sw / 2) + sx];
                }
            }
        }

        /// <summary>
        /// Bitmap转YV12 (Y+V+U)
        /// </summary>
        private static byte[] BitmapToYV12(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int ySize = w * h;
            int uvSize = ySize / 4;
            byte[] yuv = new byte[ySize * 3 / 2];

            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            IntPtr ptr = data.Scan0;
            int stride = data.Stride;
            byte[] rgb = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgb, 0, rgb.Length);
            bmp.UnlockBits(data);

            // Y平面
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * stride + x * 3;
                    byte r = rgb[idx + 2], gr = rgb[idx + 1], b = rgb[idx];
                    // BT.601
                    int Y = ((66 * r + 129 * gr + 25 * b + 128) >> 8) + 16;
                    yuv[y * w + x] = (byte)(Y < 0 ? 0 : (Y > 255 ? 255 : Y));
                }
            }
            // V平面 (YV12: V在前)
            for (int y = 0; y < h / 2; y++)
            {
                for (int x = 0; x < w / 2; x++)
                {
                    int idx = (y * 2) * stride + (x * 2) * 3;
                    byte r = rgb[idx + 2], gr = rgb[idx + 1], b = rgb[idx];
                    int V = ((112 * r - 94 * gr - 18 * b + 128) >> 8) + 128;
                    yuv[ySize + y * (w / 2) + x] = (byte)(V < 0 ? 0 : (V > 255 ? 255 : V));
                }
            }
            // U平面
            for (int y = 0; y < h / 2; y++)
            {
                for (int x = 0; x < w / 2; x++)
                {
                    int idx = (y * 2) * stride + (x * 2) * 3;
                    byte r = rgb[idx + 2], gr = rgb[idx + 1], b = rgb[idx];
                    int U = ((-38 * r - 74 * gr + 112 * b + 128) >> 8) + 128;
                    yuv[ySize + uvSize + y * (w / 2) + x] = (byte)(U < 0 ? 0 : (U > 255 ? 255 : U));
                }
            }
            return yuv;
        }

        private static void SwapUVPlanes(byte[] yuv, int width, int height)
        {
            int ySize = width * height;
            int uvSize = ySize / 4;
            byte[] temp = new byte[uvSize];
            Buffer.BlockCopy(yuv, ySize, temp, 0, uvSize);
            Buffer.BlockCopy(yuv, ySize + uvSize, yuv, ySize, uvSize);
            Buffer.BlockCopy(temp, 0, yuv, ySize + uvSize, uvSize);
        }

        private void StopVideoThread()
        {
            _videoRunning = false;
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(); } catch { }
            }
            if (_videoThread != null && _videoThread.IsAlive)
            {
                _videoThread.Join(2000);
            }
            _videoThread = null;
            _ffmpegProcess = null;
        }

        public void Disable()
        {
            try
            {
                StopVideoThread();
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

        public void Clear()
        {
            Disable();
            CurrentImagePath = "";
            CurrentVideoPath = "";
            IsVideoMode = false;
            try { if (File.Exists(YuvPath)) File.Delete(YuvPath); } catch { }
            Status("已清除");
        }

        public void LoadCurrent()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string enabled = ReadIni("realtime", "enabled", "0");
                    IsEnabled = enabled == "1";
                    CurrentImagePath = ReadIni("realtime", "image", "");
                    CurrentVideoPath = ReadIni("realtime", "video", "");
                    IsVideoMode = !string.IsNullOrEmpty(CurrentVideoPath);
                }
            }
            catch { }
        }

        public BitmapImage LoadPreviewImage()
        {
            if (IsVideoMode && !string.IsNullOrEmpty(CurrentVideoPath) && File.Exists(CurrentVideoPath))
            {
                string ffmpeg = FindFFmpeg();
                if (!string.IsNullOrEmpty(ffmpeg))
                {
                    string tempImg = Path.Combine(Path.GetTempPath(), "video_preview_" + Guid.NewGuid().ToString("N") + ".jpg");
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = ffmpeg,
                            Arguments = $"-i \"{CurrentVideoPath}\" -vframes 1 -s 320x240 \"{tempImg}\" -y",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        };
                        Process p = Process.Start(psi);
                        p.WaitForExit(5000);
                        if (File.Exists(tempImg))
                        {
                            BitmapImage img = new BitmapImage();
                            img.BeginInit();
                            img.CacheOption = BitmapCacheOption.OnLoad;
                            img.UriSource = new Uri(tempImg);
                            img.EndInit();
                            img.Freeze();
                            try { File.Delete(tempImg); } catch { }
                            return img;
                        }
                    }
                    catch { }
                }
                return null;
            }
            else if (!string.IsNullOrEmpty(CurrentImagePath) && File.Exists(CurrentImagePath))
            {
                try
                {
                    BitmapImage img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(CurrentImagePath);
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
                catch { return null; }
            }
            return null;
        }

        private byte[] ConvertToYUV420(byte[] rgb, int width, int height, int stride)
        {
            int ySize = width * height;
            int uvSize = width * height / 4;
            byte[] yuv = new byte[ySize + uvSize * 2];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * stride + x * 3;
                    byte b = rgb[idx], g = rgb[idx + 1], r = rgb[idx + 2];
                    int yVal = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                    yuv[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }

            // V平面在前 (YV12)
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double vSum = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = Math.Min(x + dx, width - 1), py = Math.Min(y + dy, height - 1);
                            int idx = py * stride + px * 3;
                            vSum += 0.5 * rgb[idx + 2] - 0.419 * rgb[idx + 1] - 0.081 * rgb[idx] + 128;
                        }
                    yuv[ySize + (y / 2) * (width / 2) + (x / 2)] = (byte)Math.Max(0, Math.Min(255, (int)(vSum / 4)));
                }
            }

            // U平面在后 (YV12)
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double uSum = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = Math.Min(x + dx, width - 1), py = Math.Min(y + dy, height - 1);
                            int idx = py * stride + px * 3;
                            uSum += -0.169 * rgb[idx + 2] - 0.331 * rgb[idx + 1] + 0.5 * rgb[idx] + 128;
                        }
                    yuv[ySize + uvSize + (y / 2) * (width / 2) + (x / 2)] = (byte)Math.Max(0, Math.Min(255, (int)(uSum / 4)));
                }
            }
            return yuv;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        private string ReadIni(string section, string key, string def)
        {
            StringBuilder sb = new StringBuilder(1024);
            GetPrivateProfileString(section, key, def, sb, 1024, ConfigPath);
            return sb.ToString();
        }
        private void WriteIni(string section, string key, string val)
        {
            WritePrivateProfileString(section, key, val, ConfigPath);
        }
    }
}
