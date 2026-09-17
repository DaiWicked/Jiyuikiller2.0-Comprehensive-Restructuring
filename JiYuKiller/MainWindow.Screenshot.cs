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
        #region 截图替换

        private bool _screenshotInitialized = false;
        private string _screenshotTempPath = "";

        private void InitScreenshot()
        {
            if (!_screenshotInitialized)
            {
                _screenshotService.OnLog += (msg) => Services.Logger.Instance.Info("[Screenshot] " + msg);
                _screenshotService.OnStatusChanged += (msg) => Dispatcher.Invoke(() => { TextScreenshotState.Text = msg; });
                _screenshotInitialized = true;
                Services.Logger.Instance.Info("[Screenshot] 截图替换事件注册完成");
            }
            _screenshotService.LoadCurrent();
            UpdateScreenshotPreview();
            InitRealtimeReplace();
        }

        private void UpdateScreenshotPreview()
        {
            var img = _screenshotService.LoadPreviewImage();
            ImgScreenshotPreview.Source = img;
            TextScreenshotPath.Text = string.IsNullOrEmpty(_screenshotService.CurrentImagePath) ? "（未设置）" : _screenshotService.CurrentImagePath;
            UpdateScreenshotState();
        }

        /// <summary>
        /// 更新截图替换状态显示
        /// </summary>
        private void UpdateScreenshotState()
        {
            if (!string.IsNullOrEmpty(_screenshotService.CurrentImagePath) && System.IO.File.Exists(_screenshotService.CurrentImagePath))
            {
                TextScreenshotState.Text = "已替换";
                TextScreenshotState.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                ScreenshotStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextScreenshotState.Text = "未替换";
                TextScreenshotState.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                ScreenshotStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
        }

        private void BtnScreenshotChoose_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击选择图片");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于替换屏幕截图的图片",
                Filter = "图片文件(*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _screenshotTempPath = dlg.FileName;
                _screenshotService.ChooseImage(dlg.FileName);
                UpdateScreenshotPreview();
            }
        }

        private void BtnScreenshotClear_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击清除图片");
            _screenshotTempPath = "";
            _screenshotService.ClearImage();
            UpdateScreenshotPreview();
        }

        private void BtnScreenshotApply_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击应用");
            bool ok = _screenshotService.Apply(_controller);
            UpdateScreenshotPreview();
            System.Windows.MessageBox.Show(ok ? "应用成功" : "应用失败", "截图替换");
        }

        private void BtnScreenshotCancel_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击取消");
            _screenshotService.LoadCurrent();
            UpdateScreenshotPreview();
        }

        private void BtnScreenshotCancelReplace_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("[Screenshot] 点击取消替换");
            _screenshotService.ClearImage();
            bool ok = _screenshotService.Apply(_controller);
            UpdateScreenshotPreview();
            System.Windows.MessageBox.Show(ok ? "已取消截图替换" : "取消失败", "截图替换");
        }

        private void BtnScreenshotBack_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("quick");
        }

        #endregion

        #region 实时屏幕替换

        private bool _realtimeInitialized = false;

        private void InitRealtimeReplace()
        {
            if (!_realtimeInitialized)
            {
                _realtimeService.OnLog += (msg) => Services.Logger.Instance.Info("[Realtime] " + msg);
                _realtimeService.OnStatusChanged += (msg) => Dispatcher.Invoke(() => { TextRealtimeStatus.Text = msg; });
                _realtimeInitialized = true;
                Services.Logger.Instance.Info("[Realtime] 实时屏幕替换事件注册完成");
            }
            _realtimeService.LoadCurrent();
            UpdateRealtimeState();
        }

        private void UpdateRealtimeState()
        {
            if (_realtimeService.IsEnabled)
            {
                TextRealtimeStatus.Text = _realtimeService.IsVideoMode
                    ? "已启用（视频循环播放）"
                    : "已启用 - 教师端观看时生效";
                TextRealtimeStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
                RealtimeStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                TextRealtimeStatus.Text = "未启用";
                TextRealtimeStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                RealtimeStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
            // 更新路径显示
            if (_realtimeService.IsVideoMode)
            {
                TextRealtimePath.Text = "[视频] " + (_realtimeService.CurrentVideoPath ?? "");
            }
            else
            {
                TextRealtimePath.Text = string.IsNullOrEmpty(_realtimeService.CurrentImagePath)
                    ? "（未设置）"
                    : _realtimeService.CurrentImagePath;
            }
            // 图片模式立即更新预览，视频模式由选择视频时的后台线程处理
            if (!_realtimeService.IsVideoMode)
            {
                try
                {
                    ImgRealtimePreview.Source = _realtimeService.LoadPreviewImage();
                }
                catch { }
            }
        }

        private void BtnRealtimeChooseImage_Click(object sender, RoutedEventArgs e)
        {
            // 图片模式：显示Image，隐藏文字提示和视频按钮
            ImgRealtimePreview.Visibility = System.Windows.Visibility.Visible;
            TextVideoPreviewHint.Visibility = System.Windows.Visibility.Collapsed;
            GridVideoControls.Visibility = System.Windows.Visibility.Collapsed;
            InitRealtimeReplace();
            Services.Logger.Instance.Info("[Realtime] 点击选择图片");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于实时屏幕替换的图片",
                Filter = "图片文件(*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _realtimeService.ChooseImage(dlg.FileName);
                UpdateRealtimeState();
            }
        }

        private void BtnRealtimeChooseVideo_Click(object sender, RoutedEventArgs e)
        {
            InitRealtimeReplace();
            Services.Logger.Instance.Info("[Realtime] 点击选择视频");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用于实时屏幕替换的视频（建议小于100MB）",
                Filter = "视频文件(*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv)|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv|所有文件(*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                if (_realtimeService.ChooseVideo(dlg.FileName))
                {
                    UpdateRealtimeState();
                    // 视频模式：显示文字提示，隐藏Image
                    ImgRealtimePreview.Visibility = System.Windows.Visibility.Collapsed;
                    TextVideoPreviewHint.Visibility = System.Windows.Visibility.Visible;
                    GridVideoControls.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    System.Windows.MessageBox.Show("视频文件选择失败，请查看状态提示", "实时屏幕替换");
                }
            }
        }

        private void BtnRealtimeApply_Click(object sender, RoutedEventArgs e)
        {
            // 不调用InitRealtimeReplace()，避免LoadCurrent覆盖用户已选择的视频/图片状态
            Services.Logger.Instance.Info("[Realtime] 点击启用");
            try
            {
                bool ok = _realtimeService.Apply();
                UpdateRealtimeState();
                if (ok)
                {
                    string iniPath = @"C:\Users\Public\JiYuKiller\realtime_replace.ini";
                    string yuvPath = @"C:\Users\Public\JiYuKiller\fake_screen.yuv";
                    string mode = _realtimeService.IsVideoMode ? "视频循环播放" : "静态图片";
                    System.Windows.MessageBox.Show(
                        "实时替换已启用（" + mode + "）\n" +
                        "教师端发起实时观看时生效\n" +
                        "配置存在: " + System.IO.File.Exists(iniPath) + "\n" +
                        "YUV存在: " + System.IO.File.Exists(yuvPath),
                        "实时屏幕替换");
                }
                else
                {
                    System.Windows.MessageBox.Show("启用失败，请先选择图片或视频", "实时屏幕替换");
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[Realtime] 启用异常: " + ex);
                System.Windows.MessageBox.Show("启用异常: " + ex.Message, "实时屏幕替换");
            }
        }

        private void BtnRealtimeDisable_Click(object sender, RoutedEventArgs e)
        {
            // 不调用InitRealtimeReplace()，避免LoadCurrent覆盖状态
            Services.Logger.Instance.Info("[Realtime] 点击关闭");
            _realtimeService.Disable();
            UpdateRealtimeState();
        }

        #endregion
    }
}