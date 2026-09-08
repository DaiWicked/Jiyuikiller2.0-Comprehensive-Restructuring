using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace JiYuKiller.Services
{
    /// <summary>
    /// 截图替换服务
    /// 原理：将图片路径写入INI文件[JTSettings]节的FakeScreenImage键，
    /// DLL的VInitSettings读取后在教师端监控时显示该图片替换真实屏幕
    /// </summary>
    public class ScreenshotService
    {
        public event Action<string> OnLog;
        public event Action<string> OnStatusChanged;

        private string _currentImagePath = "";
        private readonly string _iniPath;

        public string CurrentImagePath => _currentImagePath;

        public ScreenshotService()
        {
            _iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i.chaoxing.ini");
            Logger.Instance.Info("[ScreenshotService] 截图替换服务初始化，INI: " + _iniPath);
        }

        /// <summary>
        /// 加载当前已设置的截图图片
        /// </summary>
        public void LoadCurrent()
        {
            try
            {
                _currentImagePath = GetIniString("JTSettings", "FakeScreenImage", "");
                if (!string.IsNullOrEmpty(_currentImagePath) && File.Exists(_currentImagePath))
                {
                    Logger.Instance.Info("[ScreenshotService] 当前已设置截图: " + _currentImagePath);
                    OnLog?.Invoke("当前已设置截图: " + _currentImagePath);
                    OnStatusChanged?.Invoke("已设置截图替换图片，教师端监控时将显示此图片。");
                }
                else
                {
                    _currentImagePath = "";
                    Logger.Instance.Info("[ScreenshotService] 尚未设置截图替换图片");
                    OnLog?.Invoke("尚未设置截图替换图片");
                    OnStatusChanged?.Invoke("未设置截图替换。");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 加载设置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 选择图片
        /// </summary>
        public void ChooseImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                OnStatusChanged?.Invoke("图片文件不存在。");
                return;
            }

            string ext = Path.GetExtension(imagePath).ToLower();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp")
            {
                OnStatusChanged?.Invoke("不支持的图片格式，请选择png/jpg/jpeg/bmp。");
                return;
            }

            _currentImagePath = imagePath;
            Logger.Instance.Info("[ScreenshotService] 已选择图片: " + imagePath);
            OnLog?.Invoke("已选择图片: " + imagePath);
            OnStatusChanged?.Invoke("已选择图片，点击「应用」后生效。");
        }

        /// <summary>
        /// 清除图片
        /// </summary>
        public void ClearImage()
        {
            _currentImagePath = "";
            Logger.Instance.Info("[ScreenshotService] 已清除截图替换图片");
            OnLog?.Invoke("已清除截图替换图片");
            OnStatusChanged?.Invoke("已清除截图替换，点击「应用」后生效。");
        }

        /// <summary>
        /// 应用设置（写入INI并通知DLL重新读取）
        /// </summary>
        public bool Apply(JiYuController controller)
        {
            try
            {
                // 写入INI文件
                WritePrivateProfileString("JTSettings", "FakeScreenImage", _currentImagePath, _iniPath);
                Logger.Instance.Info("[ScreenshotService] 已应用截图替换: " + (_currentImagePath == "" ? "(清除)" : _currentImagePath));
                OnLog?.Invoke("已应用: " + (_currentImagePath == "" ? "清除截图替换" : _currentImagePath));

                // 通知DLL重新读取设置
                if (controller != null && controller.IsVirusInstalled)
                {
                    controller.SendVirusMessage("hk:inipath:" + _iniPath);
                    Logger.Instance.Info("[ScreenshotService] 已通知DLL重新读取设置");
                }

                if (_currentImagePath == "")
                    OnStatusChanged?.Invoke("已清除截图替换。");
                else
                    OnStatusChanged?.Invoke("已应用截图替换设置。");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 应用失败: " + ex.Message);
                OnStatusChanged?.Invoke("应用失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 加载图片为BitmapImage用于预览
        /// </summary>
        public BitmapImage LoadPreviewImage()
        {
            if (string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
                return null;

            try
            {
                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(_currentImagePath);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("[ScreenshotService] 加载预览图片失败: " + ex.Message);
                return null;
            }
        }

        #region Win32 API
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string lpAppName, string lpKeyName, string lpDefault,
            System.Text.StringBuilder lpReturnedString, int nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string lpAppName, string lpKeyName, string lpString, string lpFileName);

        private string GetIniString(string section, string key, string defaultValue)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            GetPrivateProfileString(section, key, defaultValue, sb, 512, _iniPath);
            return sb.ToString();
        }
        #endregion
    }
}
