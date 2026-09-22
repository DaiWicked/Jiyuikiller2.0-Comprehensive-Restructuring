using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace UdpGhost
{
    public partial class MonitorWindow : Window
    {
        private const int BroadcastPort = 9100;
        private List<SenderInfo> _senders = new List<SenderInfo>();
        private volatile bool _watching = false;
        private Thread _watchThread;

        public MonitorWindow()
        {
            InitializeComponent();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) this.DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _watching = false;
            this.Close();
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            _senders.Clear();
            SenderList.Items.Clear();
            CountText.Text = "0";

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var client = new UdpClient(BroadcastPort))
                    {
                        client.Client.ReceiveTimeout = 3000;
                        var endPoint = new IPEndPoint(IPAddress.Any, BroadcastPort);
                        DateTime start = DateTime.Now;
                        while ((DateTime.Now - start).TotalSeconds < 3)
                        {
                            try
                            {
                                byte[] data = client.Receive(ref endPoint);
                                string msg = Encoding.UTF8.GetString(data);
                                string[] parts = msg.Split('|');
                                if (parts.Length >= 4 && parts[0] == "DV")
                                {
                                    var info = new SenderInfo
                                    {
                                        MachineName = parts[1],
                                        IP = parts[2],
                                        Port = int.Parse(parts[3])
                                    };
                                    if (!_senders.Exists(s => s.IP == info.IP))
                                    {
                                        _senders.Add(info);
                                        Dispatcher.Invoke(() =>
                                        {
                                            SenderList.Items.Add($"{info.MachineName} ({info.IP})");
                                            CountText.Text = _senders.Count.ToString();
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            });
        }

        private void SenderList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SenderList.SelectedIndex < 0) return;
            var info = _senders[SenderList.SelectedIndex];
            StartWatching(info);
        }

        private void StartWatching(SenderInfo info)
        {
            _watching = false;
            if (_watchThread != null && _watchThread.IsAlive) _watchThread.Join(500);
            _watching = true;
            _watchThread = new Thread(() => WatchLoop(info)) { IsBackground = true };
            _watchThread.Start();
        }

        private void WatchLoop(SenderInfo info)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(info.IP, info.Port);
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[65536];
                        MemoryStream jpegStream = new MemoryStream();
                        bool inJpeg = false;

                        while (_watching && client.Connected)
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;

                            for (int i = 0; i < read; i++)
                            {
                                if (!inJpeg)
                                {
                                    if (buffer[i] == 0xFF && i + 1 < read && buffer[i + 1] == 0xD8)
                                    {
                                        inJpeg = true;
                                        jpegStream = new MemoryStream();
                                        jpegStream.WriteByte(buffer[i]);
                                    }
                                }
                                else
                                {
                                    jpegStream.WriteByte(buffer[i]);
                                    if (buffer[i] == 0xD9 && i >= 1 && buffer[i - 1] == 0xFF)
                                    {
                                        inJpeg = false;
                                        byte[] jpegData = jpegStream.ToArray();
                                        Dispatcher.Invoke(() =>
                                        {
                                            try
                                            {
                                                var img = new BitmapImage();
                                                img.BeginInit();
                                                img.StreamSource = new MemoryStream(jpegData);
                                                img.CacheOption = BitmapCacheOption.OnLoad;
                                                img.EndInit();
                                                img.Freeze();
                                                VideoImage.Source = img;
                                            }
                                            catch { }
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    internal class SenderInfo
    {
        public string MachineName { get; set; }
        public string IP { get; set; }
        public int Port { get; set; }
    }
}
