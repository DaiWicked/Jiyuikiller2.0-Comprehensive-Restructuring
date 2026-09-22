using System;
using System.Windows;
using System.Windows.Input;
using UdpGhost.Services;

namespace UdpGhost
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            try { this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/UdpGhost;component/Assets/udp.ico")); } catch { }
        }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) this.DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e) { this.Close(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void Log(string msg) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            Log("正在扫描局域网...");
            HostList.Items.Clear();
            var hosts = await System.Threading.Tasks.Task.Run(() => GhostService.ScanNetwork());
            foreach (var ip in hosts) HostList.Items.Add(ip);
            Log($"扫描完成，发现 {hosts.Count} 台设备");
        }
        private void HostList_DoubleClick(object sender, RoutedEventArgs e) { if (HostList.SelectedItem != null) IpBox.Text = HostList.SelectedItem.ToString(); }

        private string GetTarget()
        {
            if (!string.IsNullOrWhiteSpace(IpBox.Text)) return IpBox.Text.Trim();
            if (HostList.SelectedItem != null) return HostList.SelectedItem.ToString();
            return null;
        }

        private bool CheckChannel(out int channel)
        {
            channel = 1;
            if (!int.TryParse(ChannelBox.Text, out channel) || channel < 1 || channel > 32)
            { MessageBox.Show("频道号1-32"); return false; }
            return true;
        }

        private void Blackscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckChannel(out int channel)) return;
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            string text = BlackText.Text.Trim();
            if (string.IsNullOrEmpty(text)) text = null;
            Log(GhostService.SendBlackscreen(ip, channel, true, 5, text));
        }
        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckChannel(out int channel)) return;
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            Log(GhostService.SendUnlock(ip, channel));
        }
        private void SendMsg_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            var dlg = new MessageDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Message))
                Log(GhostService.SendText(ip, 4705, dlg.Message));
        }
        private void Shutdown_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            if (MessageBox.Show($"确认向 {ip} 发送关机？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log(GhostService.SendShutdown(ip));
        }
        private void Reboot_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            if (MessageBox.Show($"确认向 {ip} 发送重启？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Log(GhostService.SendReboot(ip));
        }
        private void Monitor_Click(object sender, RoutedEventArgs e)
        {
            var win = new MonitorWindow();
            win.Owner = this;
            win.Show(); // 非模态，不阻塞主窗口
        }
        private async void Deploy_Click(object sender, RoutedEventArgs e)
        {
            string ip = GetTarget();
            if (ip == null) { MessageBox.Show("请输入或选择学生端IP"); return; }
            string senderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DolbyVision.exe");
            if (!System.IO.File.Exists(senderPath))
            {
                MessageBox.Show("未找到 DolbyVision.exe，请放在 UdpGhost 同目录下");
                return;
            }
            Log(GhostService.StartHttpServer(senderPath));
            System.Threading.Thread.Sleep(500);
            // 用certutil下载（Win7兼容），后台运行
            string localIp = GhostService.GetLocalIP();
            string cmd = "cmd /c certutil -urlcache -split -f http://" + localIp + ":8080/DolbyVision.exe %TEMP%\\DolbyVision.exe && start \"\" %TEMP%\\DolbyVision.exe";
            Log(GhostService.SendCommand(ip, 4705, cmd));
            Log("已发送部署命令，HTTP服务器保持运行，等待3秒确认...");
            // 等3秒后扫描确认目标IP上线
            await System.Threading.Tasks.Task.Delay(3000);
            var senders = await System.Threading.Tasks.Task.Run(() => GhostService.ScanSenders());
            bool found = senders.Exists(s => s.IP == ip);
            if (found) Log($"部署成功：{ip} 已上线");
            else Log($"部署失败：未检测到 {ip}，请手动确认（HTTP服务器仍在运行）");
        }
    }
}
