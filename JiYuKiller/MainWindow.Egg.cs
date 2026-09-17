using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region 彩蛋

        /// <summary>
        /// 版本号点击 - 点击3次触发彩蛋视频
        /// </summary>
        private void AboutVersion_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_eggShowing)
            {
                Services.Logger.Instance.Debug("[彩蛋] 彩蛋已显示, 忽略点击");
                return;
            }
            _eggClickCount++;
            Services.Logger.Instance.Debug($"彩蛋点击: {_eggClickCount}/3");
            if (_eggClickCount >= 3)
            {
                _eggClickCount = 0;
                ShowEggVideo();
            }
        }

        /// <summary>
        /// 显示彩蛋视频 - 从嵌入资源释放到临时目录并播放
        /// </summary>
        private void ShowEggVideo()
        {
            try
            {
                _eggShowing = true;

                // 文件只释放一次, 避免重复IO
                if (!_eggExtracted || !System.IO.File.Exists(_eggTempPath))
                {
                    // 与驱动/DLL 使用同一个释放目录, 避免在多处硬编码临时路径
                    string tempDir = Services.EmbeddedResourceService.TempDir;
                    System.IO.Directory.CreateDirectory(tempDir);
                    _eggTempPath = System.IO.Path.Combine(tempDir, "egg.mp4");

                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    string resourceName = null;
                    foreach (var name in assembly.GetManifestResourceNames())
                    {
                        if (name.EndsWith("egg.mp4"))
                        {
                            resourceName = name;
                            break;
                        }
                    }

                    if (resourceName == null)
                    {
                        Services.Logger.Instance.Error("[彩蛋] 未找到嵌入资源 egg.mp4");
                        _eggShowing = false;
                        return;
                    }

                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        using (var file = new System.IO.FileStream(_eggTempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            stream.CopyTo(file);
                        }
                    }
                    _eggExtracted = true;
                    Services.Logger.Instance.Info($"[彩蛋] 视频已释放: {_eggTempPath}");
                }
                else
                {
                    Services.Logger.Instance.Debug("[彩蛋] 视频已存在, 跳过释放");
                }

                EggWindow.Visibility = System.Windows.Visibility.Visible;
                // 上一次播放失败时可能把视频元素隐藏了, 这里恢复
                EggMedia.Visibility = System.Windows.Visibility.Visible;
                // 必须显式 Absolute: 相对 Uri 会让 MediaElement 无法定位到本地文件
                EggMedia.Source = new System.Uri(_eggTempPath, System.UriKind.Absolute);
                EggMedia.Play();
                Services.Logger.Instance.Info("[彩蛋] 视频开始播放");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[彩蛋] 播放失败", ex);
                _eggShowing = false;
            }
        }

        /// <summary>
        /// 彩蛋视频播放失败时的回退处理。
        /// 播放失败时 MediaElement 只留一个黑框, 之前没有任何提示或日志。
        /// </summary>
        private void EggMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string reason = e != null && e.ErrorException != null
                ? e.ErrorException.Message
                : "(未知错误)";

            Services.Logger.Instance.Error(
                "[彩蛋] 视频播放失败, 文件: " + _eggTempPath + ", 原因: " + reason +
                "。常见原因: 目标机器缺少 H.264 解码器(Win7 N/KN 版无 Media Feature Pack)或本窗口为分层窗口(AllowsTransparency=True)导致视频无法合成。");

            try
            {
                // 失败时给出可读提示, 不要留一个纯黑窗口
                EggMedia.Visibility = System.Windows.Visibility.Collapsed;
                // 覆盖层也要收起：只隐藏 Media 会留下一个深色空窗（正是注释说要避免的"纯黑窗口"），
                // 而且会出现"状态已复位但画面还在"的自相矛盾。
                try { EggWindow.Visibility = System.Windows.Visibility.Collapsed; } catch { }
                // 关键：复位状态。原来不复位 ⇒ _eggShowing 永远为 true，彩蛋再也点不出来（除非手动点关闭）
                _eggShowing = false;
                _eggClickCount = 0;
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 关闭彩蛋窗口
        /// </summary>
        private void EggClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EggMedia.Stop();
                EggMedia.Source = null;
                EggWindow.Visibility = System.Windows.Visibility.Collapsed;
                _eggShowing = false;
                _eggClickCount = 0;
                Services.Logger.Instance.Info("[彩蛋] 窗口已关闭, 状态已重置");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[彩蛋] 关闭失败", ex);
                _eggShowing = false;
            }
        }

        #endregion
    }
}