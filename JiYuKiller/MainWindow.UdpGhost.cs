using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region UdpGhost (独立进程UdpGhost.exe)

        private Process _udpGhostProcess = null;

        private void InitUdpGhost()
        {
            UpdateUdpGhostStatus();
        }

        private void BtnStartUdpGhost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string udpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "UdpGhost.exe");
                if (!File.Exists(udpPath))
                {
                    udpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UdpGhost.exe");
                }
                if (!File.Exists(udpPath))
                {
                    System.Windows.MessageBox.Show("找不到 UdpGhost.exe，请确认文件完整。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = udpPath,
                    UseShellExecute = false
                };
                _udpGhostProcess = Process.Start(psi);
                Services.Logger.Instance.Info($"[UdpGhost] UdpGhost.exe 已启动, PID={_udpGhostProcess.Id}");

                BtnStartUdpGhost.Visibility = Visibility.Collapsed;
                BtnStopUdpGhost.Visibility = Visibility.Visible;
                UdpGhostStatus.Text = "状态: 运行中";
                UdpGhostStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45));
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[UdpGhost] 启动UdpGhost失败: " + ex.Message);
                System.Windows.MessageBox.Show("启动失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStopUdpGhost_Click(object sender, RoutedEventArgs e)
        {
            StopUdpGhost();
        }

        private void StopUdpGhost()
        {
            try
            {
                if (_udpGhostProcess != null && !_udpGhostProcess.HasExited)
                {
                    _udpGhostProcess.CloseMainWindow();
                    if (!_udpGhostProcess.WaitForExit(2000))
                    {
                        _udpGhostProcess.Kill();
                    }
                    Services.Logger.Instance.Info("[UdpGhost] UdpGhost.exe 已关闭");
                }
                _udpGhostProcess = null;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[UdpGhost] 关闭UdpGhost失败: " + ex.Message);
            }

            BtnStartUdpGhost.Visibility = Visibility.Visible;
            BtnStopUdpGhost.Visibility = Visibility.Collapsed;
            UdpGhostStatus.Text = "状态: 未启动";
            UdpGhostStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99));
        }

        private void UpdateUdpGhostStatus()
        {
            var procs = Process.GetProcessesByName("UdpGhost");
            if (procs.Length > 0)
            {
                _udpGhostProcess = procs[0];
                BtnStartUdpGhost.Visibility = Visibility.Collapsed;
                BtnStopUdpGhost.Visibility = Visibility.Visible;
                UdpGhostStatus.Text = "状态: 运行中";
                UdpGhostStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                BtnStartUdpGhost.Visibility = Visibility.Visible;
                BtnStopUdpGhost.Visibility = Visibility.Collapsed;
                UdpGhostStatus.Text = "状态: 未启动";
                UdpGhostStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99));
            }
        }

        private void BtnUdpGhostBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion
    }
}
