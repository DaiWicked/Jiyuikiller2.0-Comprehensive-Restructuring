# 版本号同步：从主程序 AppSettings.cs 读出版本号，写进 ChatRoom\AppInfo.cs
# 用法（主程序改版本后跑一次）：powershell -ExecutionPolicy Bypass -File tools\sync-version.ps1
# 说明：脚本刻意只用 Windows PowerShell 5.1 就有的语法，避免依赖 pwsh
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'JiYuKiller\Models\AppSettings.cs'
$dst  = Join-Path $root 'ChatRoom\AppInfo.cs'

if (-not (Test-Path $src)) { throw "找不到 $src" }

$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
# 匹配:  public string Version { get; set; } = "QD_V3.0_...";
$m = [regex]::Match($text, 'Version\s*\{\s*get;\s*set;\s*\}\s*=\s*"([^"]+)"')
if (-not $m.Success) { throw '在 AppSettings.cs 里没找到 Version 默认值' }
$ver = $m.Groups[1].Value

$content = @"
// 本文件由 tools\sync-version.ps1 自动生成，请不要手工修改。
// 版本号与主程序 JiYuKiller\Models\AppSettings.cs 保持一致。
namespace ChatRoom
{
    public static class AppInfo
    {
        public const string Version = "$ver";
        public const string ProductName = "聊天室";
        public const string Tagline = "学习不通的增强模块，可单独使用";
    }
}
"@
[System.IO.File]::WriteAllText($dst, $content, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "版本号已同步: $ver  ->  ChatRoom\AppInfo.cs"