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
                                        VideoPort = int.Parse(parts[3]),
                                        CmdPort = parts.Length > 4 ? int.Parse(parts[4]) : 9102,
                                        TerminalPort = parts.Length > 5 ? int.Parse(parts[5]) : 9103
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

        private SenderInfo GetSelected()
        {
            if (SenderList.SelectedIndex < 0) return null;
            return _senders[SenderList.SelectedIndex];
        }

        // ========== 远程控制 ==========
        private void RemoteShutdown_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            if (MessageBox.Show($"确认远程关机 {info.MachineName} ({info.IP})？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string result = SendCommand(info, "SHUTDOWN");
            MessageBox.Show(result, "远程关机");
        }

        private void RemoteReboot_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            if (MessageBox.Show($"确认远程重启 {info.MachineName} ({info.IP})？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string result = SendCommand(info, "REBOOT");
            MessageBox.Show(result, "远程重启");
        }

        private void RemoteCmd_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var dlg = new MessageDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Message))
            {
                string result = SendCommand(info, "EXEC:" + dlg.Message);
                MessageBox.Show(result, "远程命令结果");
            }
        }

        private void RemoteWatch_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            StartWatching(info);
        }

        private void RemoteCmdTerminal_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var term = new TerminalWindow(info.IP, info.TerminalPort, info.MachineName, "CMD");
            term.Owner = this;
            term.Show();
        }

        private void RemotePsTerminal_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var term = new TerminalWindow(info.IP, info.TerminalPort, info.MachineName, "PS");
            term.Owner = this;
            term.Show();
        }

        private string SendCommand(SenderInfo info, string cmd)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.ReceiveTimeout = 5000;
                    client.Connect(info.IP, info.CmdPort);
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes(cmd);
                        stream.Write(data, 0, data.Length);
                        stream.Flush();

                        byte[] buffer = new byte[8192];
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read > 0)
                            return Encoding.UTF8.GetString(buffer, 0, read);
                        return "无响应";
                    }
                }
            }
            catch (Exception ex)
            {
                return "错误: " + ex.Message;
            }
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
                    client.Connect(info.IP, info.VideoPort);
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
        public int VideoPort { get; set; }
        public int CmdPort { get; set; }
        public int TerminalPort { get; set; }
    }
}
