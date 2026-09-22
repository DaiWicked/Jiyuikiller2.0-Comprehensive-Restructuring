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
        #region 窗口控制

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 仅允许拖动窗口，不响应双击最大化（窗口固定大小不可最大化）
            // 左键已释放（快速点击/双击/触控）时 DragMove 会抛 InvalidOperationException，别让它弹全局错误框
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }


        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关闭(隐藏到托盘)", "BtnClose");
            // 参考原项目逻辑：关闭按钮不退出程序，而是隐藏到托盘
            HideToTray();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 参考原项目 WM_CLOSE: return TRUE 的逻辑
            // 除非是真正的退出操作，否则取消关闭，改为隐藏到托盘
            if (!_isExiting)
            {
                Services.Logger.Instance.WindowEvent("MainWindow", "OnClosing - 取消关闭，隐藏到托盘");
                e.Cancel = true;
                HideToTray();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Services.Logger.Instance.WindowEvent("MainWindow", "OnClosed");
            if (_isExiting)
            {
                // 统一清理：所有退出路径都会经过这里
                try { _controller.Stop(); } catch { }
                try
                {
                    if (_teacherSimService != null && _teacherSimService.IsRunning)
                        _teacherSimService.Stop();
                } catch { }
                try { StopChatRoom(); } catch { }
                try { StopUdpGhost(); } catch { }
                try { _glassyManager?.Dispose(); } catch { }
                try
                {
                    if (_trayIcon != null)
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                } catch { }
                try { Services.Logger.Instance.Close(); } catch { }
            }
            base.OnClosed(e);
        }

        #endregion
        #region 电源控制

        private void NavQuick_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("快捷栏", "NavQuick");
            UpdateJiYuStatus();
            ShowPage("quick");
        }

        private void BtnTopMost_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("本窗口置顶", "BtnTopMost");
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hWnd = helper.Handle;

            if (_isTopMost)
            {
                _isTopMost = false;
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
                BtnTopMost.Content = "本窗口置顶";
                BtnTopMost.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                Services.Logger.Instance.Info("已取消窗口置顶");
            }
            else
            {
                _isTopMost = true;
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
                BtnTopMost.Content = "取消置顶";
                BtnTopMost.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                BtnTopMost.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                Services.Logger.Instance.Info("已设置窗口置顶");
            }
        }

        private void BtnKillJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("杀死极域", "BtnKillJiYu");
            bool result = _controller.KillJiYu();
            if (result)
            {
                System.Windows.MessageBox.Show("已成功结束极域电子教室", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("未找到极域进程或杀死失败", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateJiYuStatus();
        }

        private void BtnRestartJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("重启极域", "BtnRestartJiYu");
            bool result = _controller.RestartJiYu();
            if (result)
            {
                System.Windows.MessageBox.Show("已重启极域电子教室", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            UpdateJiYuStatus();
        }

        private void UpdateJiYuStatus()
        {
            try
            {
                var processes = Process.GetProcessesByName("StudentMain");
                try
                {
                    if (processes.Length > 0)
                    {
                        TextJiYuStatus.Text = $"极域状态: 运行中 (PID={processes[0].Id})";
                        TextJiYuStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                    }
                    else
                    {
                        TextJiYuStatus.Text = "极域状态: 未运行";
                        TextJiYuStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                    }
                }
                finally
                {
                    // 该函数在每次状态刷新时都会调用, 必须释放 Process 句柄
                    foreach (var p in processes) p.Dispose();
                }
            }
            catch
            {
                TextJiYuStatus.Text = "极域状态: 检测失败";
            }
        }

        private void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关闭计算机", "BtnShutdown");
            var result = System.Windows.MessageBox.Show("你是否真的要关闭电脑？\n\n关机前会自动停止极域控制。", "电源控制 - 警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Services.Logger.Instance.Info("用户确认关机，正在停止控制器...");
                _controller.Stop();
                Services.Logger.Instance.Info("执行关机命令: shutdown /s /t 0");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    Services.Logger.Instance.Info("关机命令已发送，正在退出软件");
                    ForceExit();
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("执行关机命令失败", ex);
                    System.Windows.MessageBox.Show("关机失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("重新启动", "BtnReboot");
            var result = System.Windows.MessageBox.Show("你是否真的要重启电脑？\n\n重启前会自动停止极域控制。", "电源控制 - 警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Services.Logger.Instance.Info("用户确认重启，正在停止控制器...");
                _controller.Stop();
                Services.Logger.Instance.Info("执行重启命令: shutdown /r /t 0");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    Services.Logger.Instance.Info("重启命令已发送，正在退出软件");
                    ForceExit();
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("执行重启命令失败", ex);
                    System.Windows.MessageBox.Show("重启失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("退出软件", "BtnExitApp");
            ExitApplication();
        }

        private void ForceExit()
        {
            Services.Logger.Instance.Info("强制退出应用程序");
            _isExiting = true;
            // 所有清理统一在 OnClosed 里做
            Close();
        }

        #endregion
        #region 状态更新

        private void UpdateStatus()
        {
            StatusText.Text = _controller.GetStatusText();
            UpdateJiYuStatus();
            UpdateDriverStatus();
            // 同步极域路径（LocateJiYuPosition可能在后台保存了路径）
            if (!string.IsNullOrEmpty(_settings.JiYuMainPath) && JiYuPathText.Text != $"极域路径: {_settings.JiYuMainPath}")
            {
                JiYuPathText.Text = $"极域路径: {_settings.JiYuMainPath}";
            }
        }

        #endregion
    }
}