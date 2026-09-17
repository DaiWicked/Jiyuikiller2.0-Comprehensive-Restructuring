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

        /// <summary>读文件并按上限等比缩放、编码为 JPEG（质量 50）</summary>
        public static byte[] EncodeFile(string path)
        {
            // OnLoad：读完即放，不锁文件
            var src = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.StreamSource = fs;
                src.EndInit();
            }
            src.Freeze();

            double scale = Math.Min(1.0, Math.Min((double)MaxWidth / src.PixelWidth, (double)MaxHeight / src.PixelHeight));
            BitmapSource output = src;
            if (scale < 1.0)
            {
                var tb = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                output = tb;
            }

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
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(jpeg))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
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
