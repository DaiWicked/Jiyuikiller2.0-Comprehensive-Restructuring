using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ChatRoom.Services
{
    /// <summary>
    /// 图片编解码（只在 UI 侧使用，不碰网络）。
    ///
    /// · 发送前：读文件 → 等比缩放到 ≤320×240 → JPEG 质量 50 → 字节数组（通常 5~20KB）
    /// · 收到后：字节数组 → BitmapImage（OnLoad + Freeze，避免流被释放后图片失效）
    ///
    /// 为什么线上必须 base64：现有帧格式把"正文"按 UTF-8 解码（Encoding.UTF8.GetString），
    /// 裸二进制会被替换字符破坏 ⇒ 只能 base64（代价是 +33% 体积，换来协议零改动）。
    /// </summary>
    public static class ChatImageCodec
    {
        public const int MaxWidth = 320;
        public const int MaxHeight = 240;
        public const int JpegQuality = 50;

        /// <summary>像素上限：超过就直接拒绝解码（x86 下 4000 万像素约 160MB 位图，足以吃爆进程）</summary>
        public const int MaxPixels = 40000000;
        /// <summary>接收侧单张图字节上限（协议侧正文上限 128K 字符 ≈ 96KB 图）</summary>
        public const int MaxDecodeBytes = 256 * 1024;

        /// <summary>读文件并按上限等比缩放、编码为 JPEG（质量 50）</summary>
        public static byte[] EncodeFile(string path)
        {
            // OnLoad：读完即放，不锁文件
            // 先只读元数据取原始尺寸：直接整幅解码超大图会把 x86 进程吃爆（解码炸弹）
            int pw, ph;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var dec = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                pw = dec.Frames[0].PixelWidth;
                ph = dec.Frames[0].PixelHeight;
            }
            if (pw <= 0 || ph <= 0) return null;
            // 本地文件不设像素硬上限（手机原图也要能发）：按目标尺寸解码即可把内存压住

            // 按目标尺寸解码（只设长边，保持比例）；OnLoad：读完即放，不锁文件
            var src = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.StreamSource = fs;
                if (pw > MaxWidth || ph > MaxHeight)
                {
                    // 同时设宽高（按比例算），保证结果不超过 320x240：只设长边时方图会变成 320x320
                    double s = Math.Min((double)MaxWidth / pw, (double)MaxHeight / ph);
                    if (s < 1.0)
                    {
                        src.DecodePixelWidth = Math.Max(1, (int)Math.Round(pw * s));
                        src.DecodePixelHeight = Math.Max(1, (int)Math.Round(ph * s));
                    }
                }
                src.EndInit();
            }
            src.Freeze();

            BitmapSource output = src;

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(output));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>JPEG 字节 → 可跨线程使用的位图（失败返回 null，不抛）</summary>
        public static BitmapImage Decode(byte[] jpeg)
        {
            try
            {
                if (jpeg == null || jpeg.Length == 0 || jpeg.Length > MaxDecodeBytes) return null;

                // 远端可控内容：解码前先按元数据判像素数，避免"小文件大位图"的内存炸弹
                int pw, ph;
                using (var probe = new MemoryStream(jpeg))
                {
                    var dec = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    pw = dec.Frames[0].PixelWidth;
                    ph = dec.Frames[0].PixelHeight;
                }
                if (pw <= 0 || ph <= 0 || (long)pw * ph > MaxPixels) return null;

                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(jpeg))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    // 只在需要"缩小"时设：无条件设会把远端小图放大（实测 8x8 的 659 字节 JPEG 会被放大成 320x320
                    // = 40 万字节，等于给攻击者一个内存放大杠杆）。同时设宽高可保证不超 320x240。
                    if (pw > MaxWidth || ph > MaxHeight)
                    {
                        double s = Math.Min((double)MaxWidth / pw, (double)MaxHeight / ph);
                        if (s < 1.0)
                        {
                            bmp.DecodePixelWidth = Math.Max(1, (int)Math.Round(pw * s));
                            bmp.DecodePixelHeight = Math.Max(1, (int)Math.Round(ph * s));
                        }
                    }
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>把收到的图片落盘（历史里只留 [图片] 占位，图片本体存这里），返回路径；失败返回空串</summary>
        public static string SaveTo(string dir, string baseName, byte[] jpeg)
        {
            try
            {
                Directory.CreateDirectory(dir);
                foreach (char bad in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(bad, '_');
                string path = Path.Combine(dir, baseName + ".jpg");
                File.WriteAllBytes(path, jpeg);
                return path;
            }
            catch { return ""; }
        }
    }
}
