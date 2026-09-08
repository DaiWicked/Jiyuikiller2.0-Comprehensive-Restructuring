using System;
using System.IO;
using System.Reflection;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 嵌入式资源释放服务
    /// 将嵌入的驱动和DLL释放到临时目录
    /// </summary>
    public static class EmbeddedResourceService
    {
        private static string _tempDir;
        private static readonly object _lock = new object();

        /// <summary>
        /// 获取临时目录路径
        /// </summary>
        public static string TempDir
        {
            get
            {
                if (string.IsNullOrEmpty(_tempDir))
                {
                    _tempDir = Path.Combine(Path.GetTempPath(), "学习不通");
                }
                return _tempDir;
            }
        }

        /// <summary>
        /// 释放所有嵌入资源到临时目录
        /// </summary>
        public static void ExtractAll()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(TempDir);
                    Logger.Instance.Info($"[EmbeddedResource] 临时目录: {TempDir}");

                    // 释放驱动和DLL
                    ExtractResource("JiYuKiller.Drivers.JiYuTrainerDriver.sys",
                        Path.Combine(TempDir, "JiYuTrainerDriver.sys"));
                    ExtractResource("JiYuKiller.Drivers.JiYuTrainerHooks.dll",
                        Path.Combine(TempDir, "JiYuTrainerHooks.dll"));

                    Logger.Instance.Info("[EmbeddedResource] 所有嵌入资源释放完成");
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("[EmbeddedResource] 释放资源失败", ex);
                }
            }
        }

        /// <summary>
        /// 释放单个嵌入资源
        /// </summary>
        private static void ExtractResource(string resourceName, string outputPath)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Logger.Instance.Warn($"[EmbeddedResource] 资源不存在: {resourceName}");
                        return;
                    }

                    // 如果文件已存在且大小相同，跳过
                    if (File.Exists(outputPath))
                    {
                        FileInfo fi = new FileInfo(outputPath);
                        if (fi.Length == stream.Length)
                        {
                            Logger.Instance.Debug($"[EmbeddedResource] 文件已存在，跳过: {Path.GetFileName(outputPath)}");
                            return;
                        }
                    }

                    using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                    }
                    Logger.Instance.Info($"[EmbeddedResource] 已释放: {Path.GetFileName(outputPath)} ({stream.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"[EmbeddedResource] 释放失败: {resourceName}", ex);
            }
        }

        /// <summary>
        /// 获取驱动文件路径（优先临时目录，回退到exe目录Drivers）
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
        /// 清理临时目录
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, true);
                    Logger.Instance.Info("[EmbeddedResource] 临时目录已清理");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn($"[EmbeddedResource] 清理临时目录失败: {ex.Message}");
            }
        }
    }
}
