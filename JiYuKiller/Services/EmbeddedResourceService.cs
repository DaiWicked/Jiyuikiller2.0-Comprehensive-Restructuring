using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 嵌入式资源释放服务
    /// 将嵌入的驱动和DLL释放到本用户目录
    /// </summary>
    public static class EmbeddedResourceService
    {
        private static string _tempDir;
        private static readonly object _lock = new object();

        private static bool _forceInstallInCurrentDir = false;

        /// <summary>
        /// "强制安装在当前目录"（对应上游 ForceInstallInCurrentDir 的语义：程序目录固定不动、
        /// 上游在 U 盘场景下会把本体复制到 %TEMP% 再启动，勾选此项则不复制）。
        /// 打开时把释放目录固定为 exe 所在目录（U 盘便携、路径可预期）；exe 目录不可写时自动回退用户目录。
        /// 由 App.OnStartup 与"保存高级设置"从设置同步进来；赋值会让目录缓存失效。
        /// </summary>
        public static bool ForceInstallInCurrentDir
        {
            get { return _forceInstallInCurrentDir; }
            set
            {
                if (_forceInstallInCurrentDir == value) return;
                _forceInstallInCurrentDir = value;
                _tempDir = null;   // 目录缓存失效，下次重新计算
            }
        }

        /// <summary>
        /// 释放目录。
        /// 优先 %LOCALAPPDATA%\学习不通 (按用户隔离)，取不到时回退 %TEMP%\学习不通。
        /// 不再使用 %TEMP% 作为首选: 该目录全局可写且内容可被其它进程预置,
        /// 配合"仅比较文件长度"的旧逻辑会直接复用被替换过的驱动/DLL。
        /// </summary>
        public static string TempDir
        {
            get
            {
                if (string.IsNullOrEmpty(_tempDir))
                {
                    // "强制安装在当前目录"：直接放 exe 旁边（U 盘便携 / 路径可预期）
                    if (_forceInstallInCurrentDir)
                    {
                        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                        if (!string.IsNullOrEmpty(exeDir) && IsDirectoryWritable(exeDir))
                        {
                            _tempDir = exeDir.TrimEnd(Path.DirectorySeparatorChar);
                            if (string.IsNullOrEmpty(_tempDir)) _tempDir = exeDir;
                            return _tempDir;
                        }
                        Logger.Instance.Warn("[EmbeddedResource] 已勾选强制安装在当前目录, 但 exe 目录不可写, 回退到用户目录");
                    }

                    string baseDir = null;
                    try
                    {
                        baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    }
                    catch
                    {
                        baseDir = null;
                    }

                    if (string.IsNullOrEmpty(baseDir))
                    {
                        baseDir = Path.GetTempPath();
                    }

                    _tempDir = Path.Combine(baseDir, "学习不通");
                }
                return _tempDir;
            }
        }

        /// <summary>探测目录是否可写（建一个临时文件再删掉；不抛异常）</summary>
        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".wtest_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 释放所有嵌入资源到释放目录
        /// </summary>
        /// <returns>驱动与DLL是否都已就绪</returns>
        public static bool ExtractAll()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(TempDir);
                    Logger.Instance.Info($"[EmbeddedResource] 释放目录: {TempDir}");

                    bool driverOk = ExtractResource("JiYuKiller.Drivers.JiYuTrainerDriver.sys",
                        Path.Combine(TempDir, "JiYuTrainerDriver.sys"));
                    bool hooksOk = ExtractResource("JiYuKiller.Drivers.JiYuTrainerHooks.dll",
                        Path.Combine(TempDir, "JiYuTrainerHooks.dll"));

                    if (driverOk && hooksOk)
                    {
                        Logger.Instance.Info("[EmbeddedResource] 所有嵌入资源释放完成");
                    }
                    else
                    {
                        Logger.Instance.Error(string.Format(
                            "[EmbeddedResource] 嵌入资源释放未全部成功: 驱动={0}, DLL={1}",
                            driverOk, hooksOk));
                    }

                    return driverOk && hooksOk;
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[EmbeddedResource] 释放资源失败", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 释放单个嵌入资源。
        /// 通过 SHA-256 比对判断是否可复用已有文件，长度相同但内容不同的文件会被覆盖。
        /// </summary>
        /// <returns>目标文件是否已是最新且存在</returns>
        private static bool ExtractResource(string resourceName, string outputPath)
        {
            string tempPath = outputPath + ".tmp";

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Logger.Instance.Warn($"[EmbeddedResource] 资源不存在: {resourceName}");
                        return false;
                    }

                    string expectedHash = ComputeStreamHash(stream);
                    stream.Position = 0;

                    if (File.Exists(outputPath) &&
                        string.Equals(ComputeFileHash(outputPath), expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Instance.Debug($"[EmbeddedResource] 文件已是最新，跳过: {Path.GetFileName(outputPath)}");
                        return true;
                    }

                    // 先写临时文件再改名, 避免出现"内容写到一半"的目标文件被上层直接加载
                    using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.CopyTo(fs);
                    }

                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                    File.Move(tempPath, outputPath);

                    Logger.Instance.Info($"[EmbeddedResource] 已释放: {Path.GetFileName(outputPath)} ({stream.Length} bytes, SHA256={ShortHash(expectedHash)})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"[EmbeddedResource] 释放失败: {resourceName}", ex);
                TryDelete(tempPath);
                return false;
            }
        }

        private static string ComputeStreamHash(Stream stream)
        {
            long position = stream.CanSeek ? stream.Position : 0;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                if (stream.CanSeek)
                {
                    stream.Position = position;
                }
                return ToHex(hash);
            }
        }

        private static string ComputeFileHash(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    return ToHex(sha.ComputeHash(fs));
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn($"[EmbeddedResource] 计算文件哈希失败: {path} ({ex.Message})");
                return null;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = hex[bytes[i] >> 4];
                chars[i * 2 + 1] = hex[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length <= 12)
            {
                return hash;
            }
            return hash.Substring(0, 12);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理失败无碍
            }
        }

        /// <summary>
        /// 获取驱动文件路径（优先释放目录，回退到exe目录Drivers）
        /// </summary>
        public static string GetDriverPath()
        {
            string tempPath = Path.Combine(TempDir, "JiYuTrainerDriver.sys");
            if (File.Exists(tempPath))
                return tempPath;

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "JiYuTrainerDriver.sys");
            return exePath;
        }

        /// <summary>
        /// 获取DLL文件路径
        /// </summary>
        public static string GetDllPath()
        {
            string tempPath = Path.Combine(TempDir, "JiYuTrainerHooks.dll");
            if (File.Exists(tempPath))
                return tempPath;

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers", "JiYuTrainerHooks.dll");
            return exePath;
        }

        /// <summary>
        /// 清理释放目录
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                if (Directory.Exists(TempDir))
                {
                    // 保护：ForceInstallInCurrentDir=true 时释放目录就是 exe 目录，
                    // 直接递归删除会把程序自己、Drivers、设置和日志一起删掉。
                    if (string.Equals(TempDir.TrimEnd(Path.DirectorySeparatorChar),
                                      AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Instance.Warn("[释放] 释放目录即程序目录，跳过递归清理以免删除自身");
                    }
                    else
                    {
                        Directory.Delete(TempDir, true);
                    }
                    Logger.Instance.Info("[EmbeddedResource] 释放目录已清理");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn($"[EmbeddedResource] 清理释放目录失败: {ex.Message}");
            }
        }
    }
}
