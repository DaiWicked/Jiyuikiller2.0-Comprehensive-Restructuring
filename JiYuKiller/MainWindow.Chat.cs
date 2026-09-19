using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region 小小聊天 (独立进程ChatRoom.exe)

        private Process _chatRoomProcess = null;

        private void InitChat()
        {
            // 检查是否已有ChatRoom进程在跑
            UpdateChatStatus();
        }

        private void BtnStartChat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string chatPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "ChatRoom.exe");
                if (!File.Exists(chatPath))
                {
                    // 也可能在根目录
                    chatPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatRoom.exe");
                }
                if (!File.Exists(chatPath))
                {
                    System.Windows.MessageBox.Show("找不到 ChatRoom.exe，请确认文件完整。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 不再传 --nick 参数：ChatRoom 有自己的注册/设置系统，主程序传参会覆盖用户注册的昵称
                var psi = new ProcessStartInfo
                {
                    FileName = chatPath,
                    UseShellExecute = false
                };
                _chatRoomProcess = Process.Start(psi);
                Services.Logger.Instance.Info($"[Chat] ChatRoom.exe 已启动, PID={_chatRoomProcess.Id}");

                BtnStartChat.Visibility = Visibility.Collapsed;
                BtnStopChat.Visibility = Visibility.Visible;
                ChatStatus.Text = "状态: 运行中";
                ChatStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45));
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[Chat] 启动ChatRoom失败: " + ex.Message);
                System.Windows.MessageBox.Show("启动失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStopChat_Click(object sender, RoutedEventArgs e)
        {
            StopChatRoom();
        }

        private void StopChatRoom()
        {
            try
            {
                if (_chatRoomProcess != null && !_chatRoomProcess.HasExited)
                {
                    _chatRoomProcess.CloseMainWindow();
                    if (!_chatRoomProcess.WaitForExit(2000))
                    {
                        _chatRoomProcess.Kill();
                    }
                    Services.Logger.Instance.Info("[Chat] ChatRoom.exe 已关闭");
                }
                _chatRoomProcess = null;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[Chat] 关闭ChatRoom失败: " + ex.Message);
            }

            BtnStartChat.Visibility = Visibility.Visible;
            BtnStopChat.Visibility = Visibility.Collapsed;
            ChatStatus.Text = "状态: 未启动";
            ChatStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99));
        }

        private void UpdateChatStatus()
        {
            // 检查ChatRoom是否在运行
            var procs = Process.GetProcessesByName("ChatRoom");
            if (procs.Length > 0)
            {
                _chatRoomProcess = procs[0];
                BtnStartChat.Visibility = Visibility.Collapsed;
                BtnStopChat.Visibility = Visibility.Visible;
                ChatStatus.Text = "状态: 运行中";
                ChatStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                BtnStartChat.Visibility = Visibility.Visible;
                BtnStopChat.Visibility = Visibility.Collapsed;
                ChatStatus.Text = "状态: 未启动";
                ChatStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99));
            }
        }

        private void BtnChatBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion
    }
}
