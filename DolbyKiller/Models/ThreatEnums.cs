namespace DolbyKiller.Models
{
    /// <summary>
    /// 威胁项类型
    /// </summary>
    public enum ThreatType
    {
        /// <summary>Windows 服务</summary>
        Service,
        /// <summary>运行中进程</summary>
        Process,
        /// <summary>端口占用</summary>
        Port,
        /// <summary>文件残留</summary>
        File,
        /// <summary>注册表启动项</summary>
        Registry
    }

    /// <summary>
    /// 严重级别
    /// </summary>
    public enum Severity
    {
        /// <summary>信息（残留文件等）</summary>
        Info,
        /// <summary>警告（端口占用、注册表项）</summary>
        Warning,
        /// <summary>危险（运行中进程、服务）</summary>
        Danger
    }
}
