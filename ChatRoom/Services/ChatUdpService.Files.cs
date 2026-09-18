using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatRoom.Models;

namespace ChatRoom.Services
{
    /// <summary>
    /// ChatUdpService 的**文件分块**扩展（partial，独立成文件，与 Images 同一套做法）。
    ///
    /// ============================ 协议扩展说明 ============================
    /// 新增包类型 <c>CFIL</c>：**不改动** CHAT / GBRD / PMSG / CIMG 一个字节，
    /// 老客户端收到未知魔数会自然忽略 ⇒ 属于"扩展"而不是"修改"。
    ///
    /// 包体 = 4 字节 ASCII 魔数 "CFIL" + UTF8 昵称 + 0x00 + 正文，
    /// 正文 = <c>fileId:seq:total:scope:nameB64:dataB64</c>
    ///   · fileId    该文件的会话号（发送方自增，4 位十六进制）
    ///   · seq/total 当前块序号与总块数
    ///   · scope     G = 群发（逐个单播）/ P = 私聊（单播到目标 IP）
    ///   · nameB64   文件名（UTF8 → base64）。**必须 base64**：文件名里可能有 ':'，直接拼会把字段切错
    ///   · dataB64   该块的文件字节（base64）
    ///   前后共 5 个冒号；因为两个 base64 字段都不可能含 ':'，按"前 5 个冒号"切分是安全的。
    ///
    /// 与图片的差异（都是豆包拍板的）：
    ///   1) **体积上限 50KB**（图片是压到 320×240 后通常 5~20KB）；超限**发送前直接拒绝**，不发分片。
    ///   2) **接收方不自动落盘**：字节留在内存里，等用户点气泡、自己选保存位置（另存为对话框）。
    ///      所以历史记录里只留"文件名 + 大小"，重启后点它只能提示"内容不在本机"。
    ///   3) **可执行文件红色警告**（.exe/.bat/.cmd/.scr）：是否危险由 UI 侧判断并标红，服务层不管，
    ///      保持"服务层只搬字节、不碰 WPF、不做业务判断"的分层。
    ///
    /// 接收侧上限防护（共享局域网里必须防）：块数 ≤96、组装 ≤80K 字符、同一发送者最多 2 个在拼、
    /// 15 秒收不齐就丢、收完的记录保留 30 秒用于忽略重复块。
    /// =====================================================================
    public partial class ChatUdpService
    {
        /// <summary>每块 base64 长度（与图片一致，留在 MTU 内）</summary>
        public const int FileChunkSize = 1200;
        /// <summary>文件体积上限：50KB（豆包需求 #6 的硬限制）</summary>
        public const int FileMaxBytes = 50 * 1024;
        /// <summary>块数上限（50KB → base64 约 68K 字符 → 约 57 块，留足余量）</summary>
        public const int FileMaxChunks = 96;
        /// <summary>组装上限（防炸）</summary>
        public const int FileMaxChars = 80 * 1024;
        /// <summary>每块重复发送次数（文件比图片更重要，固定 2 次）</summary>
        public const int FileRepeat = 2;
        private const int FileTimeoutSeconds = 15;
        private const int FileDoneKeepSeconds = 30;
        private const int FileMaxAssemblies = 32;
        private const int FileMinGapMs = 1500;      // 同一客户端两次发文件至少间隔

        private int _fileSeq = 1;
        private static readonly object _fileSendLock = new object();
        private DateTime _lastFileSentAt = DateTime.MinValue;
        private readonly Dictionary<string, FileAssembly> _files = new Dictionary<string, FileAssembly>();
        private readonly object _fileLock = new object();

        /// <summary>收到一个完整文件：from=发送者，name=文件名，data=文件字节，scope=G 群聊 / P 私聊</summary>
        public event Action<ChatUser, string, byte[], string> OnFileReceived;

        /// <summary>一个文件的分块组装状态</summary>
        private sealed class FileAssembly
        {
            public int Total;
            public string Scope = "G";
            public string Name = "";
            public string[] Parts;
            public int Received;
            public bool Completed;
            public DateTime LastSeen;
        }

        /// <summary>
        /// 发送文件。targetIP 为空/空串 = 群发（逐个单播给在线用户），否则私聊。
        /// 放后台线程发：分块 × 重复会占用几十到几百毫秒，不能阻塞 UI 线程。
        /// </summary>
        public void SendFile(byte[] data, string fileName, string targetIP)
        {
            if (!_running || data == null || data.Length == 0) return;

            if (data.Length > FileMaxBytes)
            {
                Raise(OnLog, "文件超过 " + (FileMaxBytes / 1024) + "KB（" + (data.Length / 1024) + "KB），未发送");
                return;
            }
            if ((DateTime.Now - _lastFileSentAt).TotalMilliseconds < FileMinGapMs)
            {
                Raise(OnLog, "发送太频繁，请稍后再发文件");
                return;
            }
            _lastFileSentAt = DateTime.Now;

            byte[] copy = data;
            string name = fileName ?? "file";
            string target = targetIP;
            Task.Run(() =>
            {
                try { lock (_fileSendLock) { SendFileCore(copy, name, target); } }
                catch (Exception ex) { Raise(OnLog, "文件发送失败: " + ex.Message); }
            });
        }

        private void SendFileCore(byte[] data, string fileName, string targetIP)
        {
            string b64 = Convert.ToBase64String(data);
            string nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName));
            string fileId = (_fileSeq++ & 0xFFFF).ToString("X4");
            string scope = string.IsNullOrEmpty(targetIP) ? "G" : "P";
            int total = (b64.Length + FileChunkSize - 1) / FileChunkSize;

            if (total > FileMaxChunks)
            {
                Raise(OnLog, "文件过大（" + (data.Length / 1024) + "KB，需 " + total + " 块），未发送");
                return;
            }

            var peers = new List<ChatUser>();
            if (string.IsNullOrEmpty(targetIP))
            {
                foreach (var u in SnapshotUsers()) { if (!u.IsMe) peers.Add(u); }
            }

            int okPackets = 0, allPackets = 0;
            for (int seq = 0; seq < total; seq++)
            {
                int off = seq * FileChunkSize;
                int len = Math.Min(FileChunkSize, b64.Length - off);
                string body = fileId + ":" + seq + ":" + total + ":" + scope + ":" + nameB64 + ":" + b64.Substring(off, len);
                byte[] pkt = EncodePacket("CFIL", Nickname, body);

                for (int r = 0; r < FileRepeat; r++)
                {
                    if (string.IsNullOrEmpty(targetIP))
                    {
                        foreach (var u in peers)
                        {
                            bool sentOk = SendTo(u.IP, pkt);
                            allPackets++;
                            if (sentOk) okPackets++;
                        }
                    }
                    else
                    {
                        bool sentOk = SendTo(targetIP, pkt);
                        allPackets++;
                        if (sentOk) okPackets++;
                    }

                    if (total > 1 || FileRepeat > 1) Thread.Sleep(8);
                }
            }

            string who = string.IsNullOrEmpty(targetIP) ? ("单播给 " + peers.Count + " 位在线用户") : "私聊";
            Raise(OnLog, okPackets == allPackets
                ? ("已发送文件「" + fileName + "」" + Math.Max(1, data.Length / 1024) + "KB（" + total + " 块 ×" + FileRepeat + " 次，" + who + "）")
                : ("文件发送部分失败：" + okPackets + "/" + allPackets + " 个数据报送出，对方可能收不完整"));
        }

        /// <summary>
        /// 收一个文件块。收齐后 base64 解码并抛 <see cref="OnFileReceived"/>（在锁外抛，
        /// 只传"文件名 + 字节数组"，服务层不碰任何 WPF 类型）。
        /// </summary>
        private void HandleFileChunk(string ip, string nick, string body)
        {
            if (ip == LocalIP) return;
            CleanupFileAssemblies();

            // 按"前 5 个冒号"切分：fileId:seq:total:scope:nameB64:dataB64
            int c1 = body.IndexOf(':'); if (c1 <= 0) return;
            int c2 = body.IndexOf(':', c1 + 1); if (c2 < 0) return;
            int c3 = body.IndexOf(':', c2 + 1); if (c3 < 0) return;
            int c4 = body.IndexOf(':', c3 + 1); if (c4 < 0) return;
            int c5 = body.IndexOf(':', c4 + 1); if (c5 < 0) return;

            string fileId = body.Substring(0, c1);
            int seq, total;
            if (!int.TryParse(body.Substring(c1 + 1, c2 - c1 - 1), out seq)) return;
            if (!int.TryParse(body.Substring(c2 + 1, c3 - c2 - 1), out total)) return;
            string scope = body.Substring(c3 + 1, c4 - c3 - 1);
            string nameB64 = body.Substring(c4 + 1, c5 - c4 - 1);
            string data = body.Substring(c5 + 1);

            if (total <= 0 || total > FileMaxChunks) return;
            if (seq < 0 || seq >= total) return;
            if (data.Length == 0 || data.Length > FileChunkSize + 16) return;
            if (nameB64.Length == 0 || nameB64.Length > 512) return;   // 文件名不该有这么长（base64 后）

            bool isNew;
            ChatUser snap = TouchUser(ip, nick, out isNew);
            if (isNew) Raise(OnUserJoined, snap);

            byte[] bytes = null;
            string fileName = null;
            string key = ip + "|" + fileId;

            lock (_fileLock)
            {
                FileAssembly asm;
                if (!_files.TryGetValue(key, out asm))
                {
                    if (_files.Count >= FileMaxAssemblies) return;

                    int pending = 0;
                    foreach (var kv in _files) { if (kv.Key.StartsWith(ip + "|") && !kv.Value.Completed) pending++; }
                    if (pending >= 2) return;   // 同一发送者最多 2 个文件在拼

                    asm = new FileAssembly
                    {
                        Total = total,
                        Scope = scope,
                        Name = DecodeName(nameB64),
                        Parts = new string[total],
                        Received = 0,
                        LastSeen = DateTime.Now
                    };
                    _files[key] = asm;
                }

                if (asm.Completed || asm.Total != total) return;   // 重复块 / 参数不一致：忽略

                if (asm.Parts[seq] == null)
                {
                    // 只在"新块"时刷新 LastSeen（同图片：否则对端不停重发同一块能让残缺组装永不过期）
                    asm.LastSeen = DateTime.Now;
                    asm.Parts[seq] = data;
                    asm.Received++;

                    if (asm.Received == asm.Total)
                    {
                        int chars = 0;
                        foreach (string p in asm.Parts) chars += (p == null ? 0 : p.Length);

                        if (chars > FileMaxChars)
                        {
                            _files.Remove(key);
                            Raise(OnLog, "文件超过上限，已丢弃");
                            return;
                        }

                        var sb = new StringBuilder(chars);
                        foreach (string p in asm.Parts) sb.Append(p);
                        asm.Parts = null;
                        asm.Completed = true;
                        fileName = asm.Name;

                        try { bytes = Convert.FromBase64String(sb.ToString()); }
                        catch { bytes = null; }

                        if (bytes == null)
                        {
                            _files.Remove(key);
                            Raise(OnLog, "文件数据损坏，已丢弃");
                            return;
                        }
                        if (bytes.Length > FileMaxBytes)
                        {
                            _files.Remove(key);
                            Raise(OnLog, "对方发来的文件超过 " + (FileMaxBytes / 1024) + "KB，已丢弃");
                            return;
                        }
                    }
                }
            }

            if (bytes != null) Raise(OnFileReceived, snap, fileName, bytes, scope);
        }

        /// <summary>文件名解码失败时给个占位名，不让整条消息丢掉</summary>
        private static string DecodeName(string nameB64)
        {
            try
            {
                string n = Encoding.UTF8.GetString(Convert.FromBase64String(nameB64));
                n = (n ?? "").Trim();
                return n.Length == 0 ? "未命名文件" : n;
            }
            catch { return "未命名文件"; }
        }

        /// <summary>清理过期的组装状态（每次收到文件块时顺带扫一遍，失败不影响接收）</summary>
        private void CleanupFileAssemblies()
        {
            try
            {
                DateTime now = DateTime.Now;
                List<string> dead = null;

                lock (_fileLock)
                {
                    foreach (var kv in _files)
                    {
                        double age = (now - kv.Value.LastSeen).TotalSeconds;
                        double limit = kv.Value.Completed ? FileDoneKeepSeconds : FileTimeoutSeconds;
                        if (!kv.Value.Completed && kv.Value.Received > 0 && age > limit)
                            Raise(OnLog, "文件接收不完整（" + kv.Value.Received + "/" + kv.Value.Total + " 块），已丢弃");
                        if (age > limit)
                        {
                            if (dead == null) dead = new List<string>();
                            dead.Add(kv.Key);
                        }
                    }

                    if (dead != null) foreach (string k in dead) _files.Remove(k);
                }
            }
            catch { }
        }

        /// <summary>四参数事件抛出（主文件里的 Raise 只有 0/1/2 参数版本，Images 里是 3 参数）</summary>
        private static void Raise<T1, T2, T3, T4>(Action<T1, T2, T3, T4> a, T1 x, T2 y, T3 z, T4 w)
        {
            if (a == null) return;
            try { a(x, y, z, w); } catch { }
        }

        // Raise(...) / EncodePacket(...) / SendBroadcast(...) / SendTo(...) / TouchUser(...) / SnapshotUsers(...)
        // 都在主文件或 Images 里，partial 同一类型可直接调用。
    }
}
