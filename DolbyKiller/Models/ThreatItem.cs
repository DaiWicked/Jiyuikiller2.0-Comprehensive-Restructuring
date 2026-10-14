using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DolbyKiller.Models
{
    /// <summary>
    /// 单条威胁项
    /// </summary>
    public class ThreatItem : INotifyPropertyChanged
    {
        private bool _isCleared;

        /// <summary>威胁类型</summary>
        public ThreatType Type { get; set; }

        /// <summary>严重级别</summary>
        public Severity Severity { get; set; }

        /// <summary>显示名称（如 "DolbyVision 服务"、"AudioSrv.exe"）</summary>
        public string Name { get; set; }

        /// <summary>详情（如 PID=1234、端口 9102/TCP、文件路径）</summary>
        public string Detail { get; set; }

        /// <summary>关联路径（文件路径或服务 binPath）</summary>
        public string Path { get; set; }

        /// <summary>关联 PID（进程类威胁）</summary>
        public int? Pid { get; set; }

        /// <summary>是否已清除</summary>
        public bool IsCleared
        {
            get { return _isCleared; }
            set { _isCleared = value; OnPropertyChanged(); }
        }

        /// <summary>类型显示名</summary>
        public string TypeLabel
        {
            get
            {
                switch (Type)
                {
                    case ThreatType.Service: return "服务";
                    case ThreatType.Process: return "进程";
                    case ThreatType.Port: return "端口";
                    case ThreatType.File: return "文件";
                    case ThreatType.Registry: return "注册表";
                    default: return "未知";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
