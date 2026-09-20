using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Linq;

namespace GameRoom
{
    public partial class MainWindow : Window
    {
        private GameUdpService _service;
        private string _nick;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 从设置读取昵称，默认"玩家"
            _nick = "玩家" + GameUdpService.GetLocalIP().Split('.').Last();

            try
            {
                _service = new GameUdpService();
                _service.OnLog += Log;
                _service.OnUserListChanged += RefreshUserList;
                _service.Start(_nick);
                TitleStatus.Text = "已连接 · " + _nick;
                Log("GameRoom启动成功");
            }
            catch (Exception ex)
            {
                TitleStatus.Text = "启动失败";
                MessageBox.Show("GameRoom启动失败：" + ex.Message + "\n\n可能已有另一个GameRoom在运行。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            try { _service?.Stop(); } catch { }
        }

        private void RefreshUserList(List<GameUser> users)
        {
            Dispatcher.Invoke(() =>
            {
                UserList.Items.Clear();
                foreach (var u in users)
                {
                    UserList.Items.Add(u.Nick + "  [" + u.Status + "]");
                }
            });
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text = s;
            });
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _service.Broadcast("HELLO|" + _nick + "|");
                Log("已刷新玩家列表");
            }
            catch { }
        }
    }
}
