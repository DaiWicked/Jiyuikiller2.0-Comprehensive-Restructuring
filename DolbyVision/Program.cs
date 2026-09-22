using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DolbyVision
{
    internal static class Program
    {
        private const int BroadcastPort = 9100;
        private const int TcpPort = 9101;
        private const int Fps = 8;
        private const int JpegQuality = 60;

        private static TcpListener _tcpListener;
        private static Thread _broadcastThread;
        private static Thread _listenThread;
        private static volatile bool _running = true;
        private static string _machineName;
        private static string _localIp;

        [STAThread]
        static void Main()
        {
            // 完全后台运行，无窗口无托盘
            _machineName = Environment.MachineName;
            _localIp = GetLocalIP();

            _broadcastThread = new Thread(BroadcastLoop) { IsBackground = true };
            _broadcastThread.Start();

            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();

            // 保持进程运行
            while (_running) { Thread.Sleep(1000); }
        }

        private static string GetLocalIP()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    return (socket.LocalEndPoint as IPEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        private static void BroadcastLoop()
        {
            while (_running)
            {
                try
                {
                    using (var client = new UdpClient())
                    {
                        client.EnableBroadcast = true;
                        string msg = $"DV|{_machineName}|{_localIp}|{TcpPort}";
                        byte[] data = Encoding.UTF8.GetBytes(msg);
                        client.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));
                    }
                }
                catch { }
                Thread.Sleep(3000);
            }
        }

        private static void ListenLoop()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, TcpPort);
                _tcpListener.Start();
                while (_running)
                {
                    try
                    {
                        TcpClient client = _tcpListener.AcceptTcpClient();
                        var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                        thread.Start();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        Bitmap screenshot = CaptureScreen();
                        if (screenshot != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                var jpegCodec = GetJpegCodec();
                                var encoderParams = new EncoderParameters(1);
                                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
                                screenshot.Save(ms, jpegCodec, encoderParams);
                                byte[] jpegData = ms.ToArray();

                                byte[] header = Encoding.ASCII.GetBytes($"--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegData.Length}\r\n\r\n");
                                stream.Write(header, 0, header.Length);
                                stream.Write(jpegData, 0, jpegData.Length);
                                stream.Write(Encoding.ASCII.GetBytes("\r\n"), 0, 2);
                                stream.Flush();
                            }
                            screenshot.Dispose();
                        }
                        Thread.Sleep(1000 / Fps);
                    }
                }
            }
            catch { }
        }

        private static Bitmap CaptureScreen()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bounds.Size);
                }
                return bitmap;
            }
            catch { return null; }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.MimeType == "image/jpeg") return codec;
            }
            return null;
        }
    }
}
