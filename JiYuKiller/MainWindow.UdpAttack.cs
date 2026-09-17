using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region UDP攻击

        private void UpdateUdpLocalInfo()
        {
            try
            {
                var svc = Services.UdpAttackService.Instance;
                string ip = svc.GetLocalIP();
                Services.Logger.Instance.Info($"UDP攻击-本机IP: {ip}");
                if (string.IsNullOrEmpty(ip))
                {
                    TextUdpLocalInfo.Text = "本机: 无网络";
                    return;
                }
                // 先显示IP
                TextUdpLocalInfo.Text = $"本机: ... - {ip}";
                // 异步获取MAC
                Task.Run(() =>
                {
                    string mac = "未知";
                    try
                    {
                        // 遍历网卡找匹配IP的MAC
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                            var props = ni.GetIPProperties();
                            foreach (var ua in props.UnicastAddresses)
                            {
                                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    Services.Logger.Instance.Debug($"网卡: {ni.Name} IP={ua.Address} MAC={ni.GetPhysicalAddress()}");
                                    if (ua.Address.ToString() == ip)
                                    {
                                        byte[] macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                                        if (macBytes.Length >= 6)
                                        {
                                            mac = BitConverter.ToString(macBytes, 0, 6);
                                        }
                                        break;
                                    }
                                }
                            }
                            if (mac != "未知") break;
                        }
                        // 如果没找到，用SendARP
                        if (mac == "未知")
                        {
                            mac = svc.GetMacAddress(ip);
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.Logger.Instance.Error("获取本机MAC异常: " + ex.Message);
                    }
                    Services.Logger.Instance.Info($"UDP攻击-本机MAC: {mac}");
                    Dispatcher.Invoke(() =>
                    {
                        TextUdpLocalInfo.Text = $"本机: {mac} - {ip}";
                    });
                });
            }
            catch (Exception ex)
            {
                TextUdpLocalInfo.Text = "本机: 获取失败";
                Services.Logger.Instance.Error("获取本机信息失败: " + ex.Message);
            }
        }

        private bool _udpLogRegistered = false;
        private void RegisterUdpLog()
        {
            if (!_udpLogRegistered)
            {
                var svc = Services.UdpAttackService.Instance;
                svc.OnLog += (msg) => Dispatcher.Invoke(() => TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\n"));
                svc.OnSendResult += (success, msg) => Dispatcher.Invoke(() =>
                {
                    if (success)
                    {
                        System.Windows.MessageBox.Show(msg, "发送成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(msg, "发送失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    }
                });
                _udpLogRegistered = true;
            }
        }

        private void BtnBackFromUdpAttack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-返回", "BtnBackFromUdpAttack");
            ShowPage("quick");
        }

        private void BtnScanLan_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-扫描局域网", "BtnScanLan");
            RegisterUdpLog();
            var svc = Services.UdpAttackService.Instance;
            svc.OnScanComplete += (hosts) => Dispatcher.Invoke(() =>
            {
                ListUdpScanResult.Items.Clear();
                foreach (var host in hosts)
                {
                    ListUdpScanResult.Items.Add(host);
                }
                TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + $" 扫描完成，发现 {hosts.Count} 台主机，点击列表选择目标\n");
            });
            svc.ScanNetwork();
        }

        private void ListUdpScanResult_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListUdpScanResult.SelectedItem is Services.NetworkHost host)
            {
                TextUdpTargetIp.Text = host.IP;
                TextUdpLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " 已选择目标: " + host.ToString() + "\n");
                Services.Logger.Instance.ButtonClick("UDP攻击-选择目标", "ListUdpScanResult");
            }
        }

        private void BtnUdpSendMsg_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-发送消息", "BtnUdpSendMsg");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendText(ip, 4705, TextUdpMessage.Text);
        }

        private void BtnUdpSendCmd_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-发送命令", "BtnUdpSendCmd");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendCommand(ip, 4705, TextUdpMessage.Text);
        }

        private void BtnUdpQuickCmd_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            string cmd = btn.Tag.ToString();
            TextUdpMessage.Text = cmd;
            Services.Logger.Instance.ButtonClick("UDP攻击-快捷指令", cmd);
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendCommand(ip, 4705, cmd);
        }
        private void BtnUdpShutdown_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-远程关机", "BtnUdpShutdown");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendShutdown(ip, 4705);
        }

        private void BtnUdpReboot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-远程重启", "BtnUdpReboot");
            RegisterUdpLog();
            string ip = TextUdpTargetIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { TextUdpLog.AppendText("请输入目标IP\n"); return; }
            var svc = Services.UdpAttackService.Instance;
            svc.SendReboot(ip, 4705);
        }


        private void BtnUdpClearLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击-清空日志", "BtnUdpClearLog");
            TextUdpLog.Text = "日志已清空\n";
        }
        #endregion
    }
}