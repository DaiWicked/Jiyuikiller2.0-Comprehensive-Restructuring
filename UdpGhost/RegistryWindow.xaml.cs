using System;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace UdpGhost
{
    public partial class RegistryWindow : Window
    {
        private readonly string _ip;
        private readonly int _cmdPort;

        public RegistryWindow(string ip, int cmdPort, string machineName)
        {
            InitializeComponent();
            _ip = ip;
            _cmdPort = cmdPort;
            Title = "远程注册表 - " + machineName + " (" + ip + ")";
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
                    var ms = new System.IO.MemoryStream();
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

        private void BtnRead_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string resp = SendCommand("REG_READ:" + PathBox.Text);
                if (resp.StartsWith("OK:"))
                {
                    string body = resp.Substring(3);
                    int idx = body.IndexOf('|');
                    if (idx > 0)
                    {
                        string type = body.Substring(0, idx);
                        string b64 = body.Substring(idx + 1);
                        byte[] raw = Convert.FromBase64String(b64);
                        string val;
                        if (type == "DWORD" && raw.Length >= 4)
                            val = BitConverter.ToInt32(raw, 0).ToString();
                        else if (type == "QWORD" && raw.Length >= 8)
                            val = BitConverter.ToInt64(raw, 0).ToString();
                        else if (type == "BINARY")
                            val = BitConverter.ToString(raw);
                        else
                            val = Encoding.UTF8.GetString(raw);
                        ResultBox.Text = "类型: " + type + "\n值: " + val;
                    }
                    else ResultBox.Text = resp;
                }
                else ResultBox.Text = resp;
            }
            catch (Exception ex) { ResultBox.Text = "错误: " + ex.Message; }
        }

        private void BtnWrite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string type = WriteTypeBox.Text.Trim().ToUpper();
                string input = WriteDataBox.Text;
                string b64;
                if (type == "DWORD")
                    b64 = Convert.ToBase64String(BitConverter.GetBytes(int.Parse(input)));
                else if (type == "QWORD")
                    b64 = Convert.ToBase64String(BitConverter.GetBytes(long.Parse(input)));
                else if (type == "BINARY")
                    b64 = input; // 直接输入Base64
                else
                    b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
                string resp = SendCommand("REG_WRITE:" + PathBox.Text + "|" + type + "|" + b64);
                ResultBox.Text = resp;
            }
            catch (Exception ex) { ResultBox.Text = "错误: " + ex.Message; }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认删除值 " + PathBox.Text + " ?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                string resp = SendCommand("REG_DELETE:" + PathBox.Text);
                ResultBox.Text = resp;
            }
            catch (Exception ex) { ResultBox.Text = "错误: " + ex.Message; }
        }
    }
}
