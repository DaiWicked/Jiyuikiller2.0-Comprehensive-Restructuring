using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
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
            _nick = "玩家" + GameUdpService.GetLocalIP().Split('.').Last();
            TitleNick.Text = " · " + _nick;

            try
            {
                _service = new GameUdpService();
                _service.OnLog += Log;
                _service.OnUserListChanged += RefreshUserList;
                _service.Start(_nick);
                Log("GameRoom启动成功，端口 " + GameUdpService.Port);
            }
            catch (Exception ex)
            {
                MessageBox.Show("GameRoom启动失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    UserList.Items.Add("● " + u.Nick + "  [" + u.Status + "]");
                }
                UserCount.Text = users.Count + "人在线";
            });
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() => { LogText.Text = s; });
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try { _service.Broadcast("HELLO|" + _nick + "|"); Log("已刷新玩家列表"); } catch { }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
