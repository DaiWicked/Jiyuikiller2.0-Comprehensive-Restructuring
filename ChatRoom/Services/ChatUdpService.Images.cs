using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatRoom.Models;

namespace ChatRoom.Services
{
    /// <summary>
    /// ChatUdpService 的**图片分块**扩展（partial，独立成文件，避免动主文件）。
    ///
    /// ============================ 协议扩展说明 ============================
    /// 新增一种包类型 <c>CIMG</c>：**不改动** CHAT / GBRD / PMSG 一个字节，
    /// 老客户端收到未知魔数会自然忽略 ⇒ 属于"扩展"而不是"修改"。
    ///
    /// 包体 = 4 字节 ASCII 魔数 "CIMG" + UTF8 昵称 + 0x00 + 正文，
    /// 正文 = <c>imgId:seq:total:scope:base64分片</c>
    ///   · imgId  该图的会话号（发送方自增，4 位十六进制）
    ///   · seq    当前块序号 0..total-1
    ///   · total  总块数（1 = 单包，即小图整张一个数据报）
    ///   · scope  G = 群发（广播）/ P = 私聊（单播到目标 IP）
    ///   · base64 分片（base64 里不可能出现 ':'，所以按前 4 个冒号切分是安全的）
    ///
    /// 为什么这样设计（评审需求时改的三处）：
    ///   1) **独立魔数**：需求原文说"新增 IMG 魔数"，又写"用 GBRD/PMSG + 正文前缀 CHUNK:" —— 两套自相矛盾；
    ///      带内前缀还会跟用户手打的一句 "CHUNK:..." 撞车。独立魔数两者都避免。
    ///   2) **小图优先单包**：320×240 JPEG q50 通常 5~20KB。切成 1000 字节 = 11~27 个包，
    ///      每个包都是一次独立失败机会，任一丢整张就废。base64 后 ≤ ~1200 字符时直接一个数据报发完。
    ///   3) **每块重复 3 次**：UDP 没有重传，与其加一套 ACK/重发控制包（协议面更大），
    ///      不如用"重复发送 + 收方按 seq 去重"换取成功率，代码只有几行。
    ///
    /// 接收侧的上限防护（共享局域网里必须防）：块数 ≤64、组装 ≤128K 字符、
    /// 同一发送者最多 2 张在拼、10 秒收不齐就丢、收完的记录保留 30 秒用于忽略重复块。
    /// =====================================================================
    /// </summary>
    public partial class ChatUdpService
    {
        /// <summary>每块 base64 长度（留在 MTU 内，避免 IP 分片）</summary>
        public const int ImageChunkSize = 1200;
        /// <summary>块数上限（防炸）</summary>
        public const int ImageMaxChunks = 64;
        /// <summary>组装上限（防炸）</summary>
        public const int ImageMaxChars = 128 * 1024;
        /// <summary>每块重复发送次数</summary>
        public const int ImageRepeat = 3;
        private const int ImageTimeoutSeconds = 10;
        private const int ImageDoneKeepSeconds = 30;
        private const int ImageMaxAssemblies = 64;

        private int _imgSeq = 1;
        private readonly Dictionary<string, ImageAssembly> _images = new Dictionary<string, ImageAssembly>();
        private readonly object _imgLock = new object();

        /// <summary>收到一张完整图片：from=发送者，jpeg=图片字节，scope=G 群聊 / P 私聊</summary>
        public event Action<ChatUser, byte[], string> OnImageReceived;

        /// <summary>一张图的分块组装状态</summary>
        private sealed class ImageAssembly
        {
            public int Total;
            public string Scope = "G";
            public string[] Parts;
            public int Received;
            public bool Completed;
            public DateTime LastSeen;
        }

        /// <summary>
        /// 发送图片。targetIP 为空/空串 = 群发（广播），否则私聊（单播）。
        /// 放后台线程发：分块 × 重复会占用几十到几百毫秒，不能阻塞 UI 线程。
        /// </summary>
        public void SendImage(byte[] jpeg, string targetIP)
        {
            if (!_running || jpeg == null || jpeg.Length == 0) return;

            byte[] copy = jpeg;
            string target = targetIP;
            Task.Run(() =>
            {
                try { SendImageCore(copy, target); }
                catch (Exception ex) { Raise(OnLog, "图片发送失败: " + ex.Message); }
            });
        }

        private void SendImageCore(byte[] jpeg, string targetIP)
        {
            string b64 = Convert.ToBase64String(jpeg);
            string imgId = (_imgSeq++ & 0xFFFF).ToString("X4");
            string scope = string.IsNullOrEmpty(targetIP) ? "G" : "P";
            int total = (b64.Length + ImageChunkSize - 1) / ImageChunkSize;

            if (total > ImageMaxChunks)
            {
                Raise(OnLog, "图片过大（" + (jpeg.Length / 1024) + "KB，需 " + total + " 块），未发送");
                return;
            }

            for (int seq = 0; seq < total; seq++)
            {
                int off = seq * ImageChunkSize;
                int len = Math.Min(ImageChunkSize, b64.Length - off);
                string body = imgId + ":" + seq + ":" + total + ":" + scope + ":" + b64.Substring(off, len);
                byte[] pkt = EncodePacket("CIMG", Nickname, body);

                for (int r = 0; r < ImageRepeat; r++)
                {
                    if (string.IsNullOrEmpty(targetIP)) SendBroadcast(pkt);
                    else SendTo(targetIP, pkt);

                    if (total > 1 || ImageRepeat > 1) Thread.Sleep(8);   // 轻微错开，别把接收方缓冲打爆
                }
            }

            Raise(OnLog, "已发送图片 " + Math.Max(1, jpeg.Length / 1024) + "KB（" + total + " 块 ×" + ImageRepeat + " 次）");
        }

        /// <summary>
        /// 收一个图片块。收齐后 base64 解码并抛 <see cref="OnImageReceived"/>（在锁外抛、只传字节数组，
        /// 服务层不碰任何 WPF 类型，解码/显示交给 UI 侧）。
        /// </summary>
        private void HandleImageChunk(string ip, string nick, string body)
        {
            if (ip == LocalIP) return;
            CleanupImageAssemblies();

            int c1 = body.IndexOf(':');
            if (c1 <= 0) return;
            int c2 = body.IndexOf(':', c1 + 1); if (c2 < 0) return;
            int c3 = body.IndexOf(':', c2 + 1); if (c3 < 0) return;
            int c4 = body.IndexOf(':', c3 + 1); if (c4 < 0) return;

            string imgId = body.Substring(0, c1);
            int seq, total;
            if (!int.TryParse(body.Substring(c1 + 1, c2 - c1 - 1), out seq)) return;
            if (!int.TryParse(body.Substring(c2 + 1, c3 - c2 - 1), out total)) return;
            string scope = body.Substring(c3 + 1, c4 - c3 - 1);
            string data = body.Substring(c4 + 1);

            if (total <= 0 || total > ImageMaxChunks) return;
            if (seq < 0 || seq >= total) return;
            if (data.Length == 0 || data.Length > ImageChunkSize + 16) return;

            bool isNew;
            ChatUser snap = TouchUser(ip, nick, out isNew);
            if (isNew) Raise(OnUserJoined, snap);

            byte[] jpeg = null;
            string key = ip + "|" + imgId;

            lock (_imgLock)
            {
                ImageAssembly asm;
                if (!_images.TryGetValue(key, out asm))
                {
                    if (_images.Count >= ImageMaxAssemblies) return;

                    int pending = 0;
                    foreach (var kv in _images) { if (kv.Key.StartsWith(ip + "|") && !kv.Value.Completed) pending++; }
                    if (pending >= 2) return;   // 同一发送者最多 2 张在拼

                    asm = new ImageAssembly
                    {
                        Total = total,
                        Scope = scope,
                        Parts = new string[total],
                        Received = 0,
                        LastSeen = DateTime.Now
                    };
                    _images[key] = asm;
                }

                if (asm.Completed || asm.Total != total) return;   // 重复块 / 参数不一致：忽略
                asm.LastSeen = DateTime.Now;

                if (asm.Parts[seq] == null)
                {
                    asm.Parts[seq] = data;
                    asm.Received++;

                    if (asm.Received == asm.Total)
                    {
                        int chars = 0;
                        foreach (string p in asm.Parts) chars += (p == null ? 0 : p.Length);

                        if (chars > ImageMaxChars)
                        {
                            _images.Remove(key);
                            Raise(OnLog, "图片超过上限，已丢弃");
                            return;
                        }

                        var sb = new StringBuilder(chars);
                        foreach (string p in asm.Parts) sb.Append(p);
                        asm.Parts = null;
                        asm.Completed = true;

                        try { jpeg = Convert.FromBase64String(sb.ToString()); }
                        catch { jpeg = null; }

                        if (jpeg == null)
                        {
                            _images.Remove(key);
                            Raise(OnLog, "图片数据损坏，已丢弃");
                            return;
                        }
                    }
                }
            }

            if (jpeg != null) Raise(OnImageReceived, snap, jpeg, scope);
        }

        /// <summary>清理过期的组装状态（每次收到图片块时顺带扫一遍，失败不影响接收）</summary>
        private void CleanupImageAssemblies()
        {
            try
            {
                DateTime now = DateTime.Now;
                List<string> dead = null;

                lock (_imgLock)
                {
                    foreach (var kv in _images)
                    {
                        double age = (now - kv.Value.LastSeen).TotalSeconds;
                        double limit = kv.Value.Completed ? ImageDoneKeepSeconds : ImageTimeoutSeconds;
                        if (age > limit)
                        {
                            if (dead == null) dead = new List<string>();
                            dead.Add(kv.Key);
                        }
                    }

                    if (dead != null) foreach (string k in dead) _images.Remove(k);
                }
            }
            catch { }
        }

        /// <summary>三参数事件抛出（主文件里的 Raise 只有 0/1/2 参数版本）</summary>
        private static void Raise<T1, T2, T3>(Action<T1, T2, T3> a, T1 x, T2 y, T3 z)
        {
            if (a == null) return;
            try { a(x, y, z); } catch { }
        }

        // Raise(...) / EncodePacket(...) / SendBroadcast(...) / SendTo(...) / TouchUser(...)
        // 都在主文件 ChatUdpService.cs 里，partial 同一类型可直接调用。
    }
}
