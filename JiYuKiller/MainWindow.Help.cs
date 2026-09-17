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
        #region 帮助文档

        private void NavHelp_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助文档", "NavHelp");
            ShowHelpSubPage("intro");
            ShowPage("help");
        }

        private void HelpNavIntro_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-介绍", "HelpNavIntro");
            ShowHelpSubPage("intro");
        }

        private void HelpNavKey_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-快捷键", "HelpNavKey");
            ShowHelpSubPage("key");
        }

        private void HelpNavAntiMonitor_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-虚假反监视", "HelpNavAntiMonitor");
            ShowHelpSubPage("antimonitor");
        }

        private void HelpNavOthers_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-其他", "HelpNavOthers");
            ShowHelpSubPage("others");
        }

        private void HelpNavDisclaimer_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("帮助-免责声明", "HelpNavDisclaimer");
            ShowHelpSubPage("disclaimer");
        }

        private void ShowHelpSubPage(string page)
        {
            HelpContentIntro.Visibility = Visibility.Collapsed;
            HelpContentKey.Visibility = Visibility.Collapsed;
            HelpContentAntiMonitor.Visibility = Visibility.Collapsed;
            HelpContentOthers.Visibility = Visibility.Collapsed;
            HelpContentDisclaimer.Visibility = Visibility.Collapsed;

            // 重置导航按钮样式
            HelpNavIntro.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavIntro.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavKey.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavKey.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavAntiMonitor.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavAntiMonitor.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavOthers.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavOthers.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            HelpNavDisclaimer.Background = new SolidColorBrush(Colors.Transparent);
            HelpNavDisclaimer.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            switch (page)
            {
                case "intro":
                    HelpContentIntro.Visibility = Visibility.Visible;
                    HelpNavIntro.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavIntro.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "key":
                    HelpContentKey.Visibility = Visibility.Visible;
                    HelpNavKey.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavKey.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "antimonitor":
                    HelpContentAntiMonitor.Visibility = Visibility.Visible;
                    HelpNavAntiMonitor.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavAntiMonitor.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "others":
                    HelpContentOthers.Visibility = Visibility.Visible;
                    HelpNavOthers.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavOthers.Foreground = new SolidColorBrush(Colors.White);
                    break;
                case "disclaimer":
                    HelpContentDisclaimer.Visibility = Visibility.Visible;
                    HelpNavDisclaimer.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7B, 0xFF));
                    HelpNavDisclaimer.Foreground = new SolidColorBrush(Colors.White);
                    break;
            }
        }

        #endregion
    }
}