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
        #region 小小私聊

        private bool _chatInitialized = false;

        private void InitChat()
        {
            if (!_chatInitialized)
            {
                _chatService.OnLog += (msg) => Dispatcher.Invoke(() => { TextChatLocalInfo.Text = msg; });
                _chatService.OnChatRecord += (msg) => Dispatcher.Invoke(() => { TextChatLog.AppendText(msg + "\n"); TextChatLog.ScrollToEnd(); });
                _chatService.OnSendResult += (success, msg) => Dispatcher.Invoke(() => { System.Windows.MessageBox.Show(msg, success ? "发送成功" : "发送失败"); });
                _chatInitialized = true;
                Services.Logger.Instance.Info("[Chat] 小小私聊事件注册完成");
            }
            TextChatLog.Clear();
            TextChatLog.AppendText("~~ 欢迎使用小小私聊 ~~\n");
            TextChatLog.AppendText("原理：极域学生端不对UDP包做身份验证，可构造数据包发送消息。\n");
            TextChatLog.AppendText("提示：座位号换算算法移植自jiyu_chat，按6人一排布局推算。\n");
            _chatService.InitLocalInfo();
        }

        private async void BtnChatFind_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Chat] 点击查找同学");
            if (!int.TryParse(TextChatTargetSeat.Text, out int seatID) || seatID <= 0)
            {
                TextChatTargetStatus.Text = "请输入有效的座位号！";
                TextChatLog.AppendText("请输入有效的座位号！\n");
                return;
            }
            TextChatTargetStatus.Text = "查找中...";
            var result = await _chatService.FindClassmate(seatID);
            TextChatTargetStatus.Text = result.Item3;
            if (result.Item1)
            {
                _chatTargetIP = result.Item2;
                _chatTargetSeat = seatID;
            }
        }

        private async void BtnChatSend_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Chat] 点击发送消息");
            await _chatService.SendMessage(_chatTargetIP, TextChatMessage.Text, _chatTargetSeat);
            TextChatMessage.Clear();
        }

        private void BtnChatClear_Click(object sender, RoutedEventArgs e)
        {
            TextChatLog.Clear();
            Services.Logger.Instance.Info("[Chat] 清空聊天记录");
        }

        private void BtnChatBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion
    }
}