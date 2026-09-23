using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace UdpGhost
{
    public partial class ProcessManagerWindow : Window
    {
        public class ProcessItem
        {
            public int PID { get; set; }
            public string Name { get; set; }
            public string Memory { get; set; }
            public string User { get; set; }
            public string Description { get; set; }
        }

        private readonly string _ip;
        private readonly int _cmdPort;
        private readonly string _machineName;
        private List<ProcessItem> _allProcesses = new List<ProcessItem>();

        public ProcessManagerWindow(string ip, int cmdPort, string machineName)
        {
            InitializeComponent();
            _ip = ip;
            _cmdPort = cmdPort;
            _machineName = machineName;
            TargetInfo.Text = $"目标: {machineName} ({ip}:{cmdPort})";
            Loaded += (s, e) => RefreshProcessList();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcessList();
        }

        private void RefreshProcessList()
        {
            try
            {
                StatusText.Text = "正在获取进程列表...";
                string result = SendCommand("PROCESS_LIST");
                if (result.StartsWith("OK:"))
                {
                    ParseProcessList(result.Substring(3));
                    StatusText.Text = $"共 {_allProcesses.Count} 个进程";
                }
                else
                {
                    StatusText.Text = "获取失败: " + result;
                    MessageBox.Show(result, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "错误: " + ex.Message;
                MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ParseProcessList(string data)
        {
            _allProcesses.Clear();
            var lines = data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool firstLine = true;
            foreach (var line in lines)
            {
                if (firstLine) { firstLine = false; continue; } // 跳过表头
                var parts = line.Split('\t');
                if (parts.Length >= 3 && int.TryParse(parts[0], out int pid))
                {
                    _allProcesses.Add(new ProcessItem
                    {
                        PID = pid,
                        Name = parts[1],
                        Memory = parts[2],
                        User = parts.Length >= 4 ? parts[3] : "",
                        Description = parts.Length >= 5 ? parts[4] : ""
                    });
                }
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string filter = SearchBox.Text.Trim().ToLower();
            var filtered = string.IsNullOrEmpty(filter)
                ? _allProcesses
                : _allProcesses.Where(p => p.Name.ToLower().Contains(filter) || (p.User ?? "").ToLower().Contains(filter) || (p.Description ?? "").ToLower().Contains(filter)).ToList();
            ProcessListView.ItemsSource = filtered;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void KillBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessListView.SelectedItem is ProcessItem item)
            {
                if (MessageBox.Show($"确认终止进程？\n\nPID: {item.PID}\n名称: {item.Name}", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        string result = SendCommand("PROCESS_KILL:" + item.PID);
                        StatusText.Text = result;
                        if (result.StartsWith("OK"))
                        {
                            RefreshProcessList();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("请先选择一个进程", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private string SendCommand(string cmd)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.ReceiveTimeout = 8000;
                    client.Connect(_ip, _cmdPort);
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes(cmd);
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                        byte[] buffer = new byte[65536];
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read > 0) return Encoding.UTF8.GetString(buffer, 0, read);
                        return "无响应";
                    }
                }
            }
            catch (Exception ex) { return "错误: " + ex.Message; }
        }
    }
}
