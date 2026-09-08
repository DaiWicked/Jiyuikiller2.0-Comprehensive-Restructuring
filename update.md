# JiYuKiller 2.0 全面重构 - 更新日志

## QD_V2.1_JiYuRebuild_Liquid-Glass (2026-09-08)

### 液态玻璃悬浮底栏（7层结构）

#### 底栏设计
- 悬浮式设计，从窗口内容区独立出来
- 7层液态玻璃结构：外阴影/外层光晕/边缘折射边框/玻璃主体/顶部高光/底部暗化/内阴影边缘
- 参考 liquid-glass-arc 参数：外阴影 `0 4px 24px rgba(0,0,0,0.18)`、内阴影 `inset 0 0 20px -5px rgba(255,255,255,0.45)`
- 顶部高光 + 底部暗化形成强烈光影对比，增强立体感
- 外阴影加深让底栏从背景中"浮出来"

#### 导航按钮
- 7个按钮：快捷栏/控制台和设置/自定义设置/极客工具/帮助文档/调试/关于
- 每个按钮独立高斯模糊玻璃效果（分层：模糊背景层+边缘高光层+文字层）
- 文字纯黑 #000000 SemiBold 13px，保证可读性
- 悬停：背景变亮 + 上移2px + 边框变亮
- 按下：背景更亮 + 缩放0.95

#### 滑动指示器
- 微妙背景高亮，在按钮下方不遮挡文字
- iOS风格平滑切换动画（CubicEase 350ms）
- 切换页面时自动滚动到可见区域

#### 交互
- 鼠标滚轮横向滚动（PreviewMouseWheel 转换为 HorizontalOffset）
- 所有滚动条隐藏（Hidden），滚动功能保留
- 底栏固定高度66px，行高90px防止裁剪

### 极客工具页面
- 预留增强功能入口：极域UDP攻击/小小私聊/截图替换/教师端模拟

### 核心功能完整移植（驱动 + DLL注入 + 控制器）

#### 内核驱动交互（DriverService.cs）
- 驱动加载/卸载：SCM 服务管理，自动创建/删除服务
- 全部 IOCTL 控制码移植：杀进程/终止/挂起/恢复/关机/重启/自我保护/初始化参数
- 驱动打开/关闭句柄管理
- 驱动文件：JiYuTrainerDriver.sys（复用原项目）

#### DLL注入服务（DllInjectService.cs）
- CreateRemoteThread + LoadLibraryW 注入/卸载
- WM_COPYDATA 与 DLL 通信（消息格式 `hk:xxx`，与原 JiYuTrainerHooks.dll 兼容）
- 目标窗口标题 "JiYu Trainer Virus Window"
- DLL文件：JiYuTrainerHooks.dll（复用原项目）

#### JiYuController 完整重构
- 进程监控：定时检测 StudentMain.exe，CKInterval 可配置
- DLL自动注入：检测到极域后自动注入 Hooks DLL
- 窗口处理：广播窗口化（移除TOPMOST，调整为屏幕3/4大小居中）、黑屏处理
- 驱动管理：LoadDriver/UnloadDriver，自动发送系统版本参数
- 内核级杀进程：驱动 IOCTL CTL_KILL_PROCESS
- 解除网络控制：卸载 TDNetFilter 驱动（sc stop/delete）
- 极域定位：注册表 + 6种常见安装路径
- 停止时自动卸载注入的DLL

### UI 功能链接修复
- 复选框实时更新设置并通知 Controller（之前只记日志）
- 保存设置后通知 Controller
- 杀死/重启极域改用 Controller 方法（之前直接用 Process）
- 新增加载/卸载内核驱动按钮
- 新增驱动状态显示（已加载/未加载）
- 状态定时更新同时刷新极域状态和驱动状态

### 软件高级设置功能启用
- 驱动设置：禁用内核驱动/自我保护 从禁用改为可用
- 结束进程模式：KernelMode 从禁用改为可用
- DLL注入模式：启用 InjectMasterHelper，移除 InjectProcHelper64（32位专用）
- 移除所有"开发中/暂不可用"提示文字
- Controller UpdateSettings 增强：CKInterval更新、驱动禁用自动卸载、自我保护自动启用

### UI 视觉优化
- 全局文字颜色加深：主要 #0A0A0A，分组 #2A2A2A，次要 #3A3A3A
- 内容层透明度默认 50%
- 光晕调节修复：Margin 问题导致光晕被主窗口遮挡
- 窗口四角黑色空隙修复：移除 AllowsTransparency 窗口上的 DropShadowEffect

### 真正毛玻璃效果（移植自 WPF-Liquid-Glass-Effect-main）
- 桌面截图背景 + HLSL 像素着色器折射
- 自动刷新背景（窗口移动/大小变化/激活失活）
- 光感效果：渐变边框 + 顶部高光
- 外层光晕虚化：双层光晕
- 圆角窗口

### 自定义设置页面
- 模糊强度调节（0-100%）
- 内容层透明度调节（5%-100%）
- 外层/中层光晕透明度调节
- 刷新玻璃背景按钮

### 新增功能
- 快捷栏页面：当前状态/极域控制/窗口控制/电源控制
- 本窗口置顶、杀死极域、重启极域
- 帮助文档页面：介绍/快捷键/其他/免责声明
- 软件高级参数配置：17项设置
- 调试命令执行
- 关于我页面

### 程序信息
- 程序显示名称：学习不通
- 进程名：i.chaoxing
- 程序图标：JiYuTrainerLogo.ico
- 版本号：QD_V2.1_JiYuRebuild_Liquid-Glass
- 目标框架：.NET Framework 4.7.2

## QD_V1.1_TrayIcon (2026-09-07)

### 新增功能
- 系统托盘支持：关闭/最小化隐藏到托盘
- 托盘双击显示/隐藏，右键菜单
- 窗口不可最大化

## QD_V1.0_WPF-Rebuild (2026-09-07)

### 项目重构
- 从 C++/Sciter 迁移至 C# .NET 4.7.2 + WPF
- 统一日志系统、设置持久化、进程监控、禁止运行、广播置顶控制
