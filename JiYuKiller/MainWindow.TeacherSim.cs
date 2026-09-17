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
        #region 小游戏

        private void NavGames_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("小游戏", "NavGames");
            ShowPage("games");
        }

        /// <summary>每次进入小游戏页面都回到菜单，避免残留上一局的状态。</summary>
        private void ResetGameContent()
        {
            try
            {
                GameContent.Content = new Games.GameMenu();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("ResetGameContent 失败: " + ex.Message);
            }
        }

        #endregion

        #region 极域教师端模拟

        private void NavTeacherSim_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("教师模拟", "NavTeacherSim");
            ShowPage("teachersim");
        }

        private void InitTeacherSim()
        {
            Services.Logger.Instance.FunctionCall("InitTeacherSim");
            // 获取本机IP
            try
            {
                string localIP = Services.UdpAttackService.Instance.GetLocalIP();
                TextTeacherSimInfo.Text = $"本机IP: {localIP}，频道: {TextTeacherSimChannel.Text}";
            }
            catch
            {
                TextTeacherSimInfo.Text = "本机IP: 获取失败";
            }
            UpdateTeacherSimState();
        }

        private void UpdateTeacherSimState()
        {
            if (_teacherSimService != null && _teacherSimService.IsRunning)
            {
                TextTeacherSimStatus.Text = "运行中";
                TextTeacherSimStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                TeacherSimStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextTeacherSimStatus.Text = "未启动";
                TextTeacherSimStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                TeacherSimStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
        }

        private bool _parsingStudentList = false;

        private void TeacherSim_OnLogOutput(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TextTeacherSimConsole.AppendText(message + Environment.NewLine);
                TextTeacherSimConsole.ScrollToEnd();

                // 解析list命令输出，填充学生列表
                if (message.Contains("[命令] 已登录学生"))
                {
                    _parsingStudentList = true;
                    ListTeacherSimStudents.Items.Clear();
                }
                else if (_parsingStudentList)
                {
                    // 格式: "  1. 192.168.3.150  DESKTOP-xxx  用户:xxx  MAC:xx-xx-xx-xx-xx-xx"
                    var match = System.Text.RegularExpressions.Regex.Match(message,
                        @"^\s+\d+\.\s+(\d+\.\d+\.\d+\.\d+)\s+.*MAC:([0-9A-Fa-f\-]+)");
                    if (match.Success)
                    {
                        string ip = match.Groups[1].Value;
                        string mac = match.Groups[2].Value;
                        ListTeacherSimStudents.Items.Add(ip + "  " + mac);
                    }
                    else if (message.StartsWith("teacher>") || message.Contains("[命令]") || string.IsNullOrWhiteSpace(message))
                    {
                        _parsingStudentList = false;
                    }
                }
            }));
        }
        private void TeacherSim_OnLogFileOutput(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TextTeacherSimLog.AppendText(message + Environment.NewLine);
                TextTeacherSimLog.ScrollToEnd();
            }));
        }

        private void BtnTeacherSimClearLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("清空日志", "BtnTeacherSimClearLog");
            TextTeacherSimLog.Clear();
        }

        private void BtnTeacherSimRefreshList_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新学生列表", "BtnTeacherSimRefreshList");
            if (_teacherSimService != null && _teacherSimService.IsRunning)
            {
                _teacherSimService.SendCommand("list");
            }
        }

        private void ListTeacherSimStudents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListTeacherSimStudents.SelectedItem != null)
            {
                string item = ListTeacherSimStudents.SelectedItem.ToString();
                string[] parts = item.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    TextTeacherSimTargetIP.Text = parts[0];
                }
            }
        }


        private void TeacherSim_OnStateChanged(bool isRunning)
        {
            // 必须用 BeginInvoke：这个事件是在 TeacherSimService **持锁状态下**触发的
            // （服务内 第 122/148/153 行，lock 从 47 行罩到方法结束）。
            // 若这里用同步 Invoke，而 UI 线程此刻正卡在 BtnTeacherSimStop_Click -> Stop() 里等同一把锁，
            // 就会形成"UI 等锁、服务线程等 UI"的必然死锁（窗口永久无响应、连关机都退不出去）。
            Dispatcher.BeginInvoke(new Action(() => UpdateTeacherSimState()));
        }

        private void BtnTeacherSimStart_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("启动模拟", "BtnTeacherSimStart");
            if (int.TryParse(TextTeacherSimChannel.Text, out int channel))
            {
                _teacherSimService.Channel = channel;
            }
            // ⚠ 这里必须留在 UI 线程调用！
            // 曾尝试挪到 Task.Run 以消除"最多 15 秒的界面冻结"，但那会造成**永久死锁**：
            //   · TeacherSimService.Start() 全程持 lock(_lock)，且成功路径会在锁内 sleep 满 15 秒；
            //   · 它的 OnLogOutput/OnStateChanged 回调在**持锁状态下**同步 Dispatcher.Invoke；
            //   · 而本窗口的"停止模拟"按钮仍在 UI 线程同步调 Stop() → 抢同一把锁；
            //   ⇒ UI 线程等锁、后台线程等 UI 线程，互相等待。
            // 真正的修法是改服务本身（别在锁内 sleep、回调改 BeginInvoke、Stop 也放后台），
            // 那属于 TeacherSimService.cs（由豆包负责），已写入报告请她处理。
            _teacherSimService.Start();
        }

        private void BtnTeacherSimStop_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("停止模拟", "BtnTeacherSimStop");
            _teacherSimService.Stop();
            UpdateTeacherSimState();
        }

        private void BtnTeacherSimSend_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("发送命令", "BtnTeacherSimSend");
            string cmd = TextTeacherSimCommand.Text.Trim();
            if (!string.IsNullOrEmpty(cmd))
            {
                _teacherSimService.SendCommand(cmd);
                TextTeacherSimCommand.Clear();
            }
        }

        private void TextTeacherSimCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnTeacherSimSend_Click(sender, e);
            }
        }

        private void BtnTeacherSimClear_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("清空控制台", "BtnTeacherSimClear");
            TextTeacherSimConsole.Clear();
        }

        private void BtnTeacherSimBack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回", "BtnTeacherSimBack");
            ShowPage("quick");
        }

        private void BtnTeacherSimQuickList_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("list");
        }

        private void BtnTeacherSimQuickAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("all");
        }

        private void BtnTeacherSimQuickBlackAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("bsall");
        }

        private void BtnTeacherSimQuickUnlockAll_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("unlock_all");
        }

        private void BtnTeacherSimQuickShutdownAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要关闭所有已登录学生机吗？", "确认全体关机", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand("sdall");
            }
        }

        private void BtnTeacherSimQuickRebootAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要重启所有已登录学生机吗？", "确认全体重启", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand("rball");
            }
        }

        private void BtnTeacherSimQuickHelp_Click(object sender, RoutedEventArgs e)
        {
            _teacherSimService.SendCommand("help");
        }


        private void BtnTeacherSimQuickMsg_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            TextTeacherSimCommand.Text = $"msg {ip} ";
            TextTeacherSimCommand.Focus();
        }

        private void BtnTeacherSimQuickMsgAll_Click(object sender, RoutedEventArgs e)
        {
            string msg = Microsoft.VisualBasic.Interaction.InputBox("请输入要群发的消息内容：", "群发消息", "", -1, -1);
            if (string.IsNullOrWhiteSpace(msg)) return;
            _teacherSimService.SendCommand("msgall " + msg);
        }
        private void BtnTeacherSimQuickBlack_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"bs {ip}");
        }

        private void BtnTeacherSimQuickUnlock_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"unlock {ip}");
        }

        private void BtnTeacherSimQuickShutdown_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            if (MessageBox.Show($"确定要关闭 {ip} 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand($"shutdown {ip}");
            }
        }

        private void BtnTeacherSimQuickReboot_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            if (MessageBox.Show($"确定要重启 {ip} 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _teacherSimService.SendCommand($"reboot {ip}");
            }
        }

        private void BtnTeacherSimQuickPreview_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"preview {ip}");
        }

        private void BtnTeacherSimQuickView_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"view {ip}");
        }

        private void BtnTeacherSimQuickViewStop_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"view_stop {ip}");
        }

        private void BtnTeacherSimQuickInfo_Click(object sender, RoutedEventArgs e)
        {
            string ip = TextTeacherSimTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("请先输入目标IP", "提示"); return; }
            _teacherSimService.SendCommand($"info {ip}");
        }
        #endregion
    }
}