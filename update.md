# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.1_JiYuRebuild_Liquid-Glass (2026-09-09) - 教师端模拟与单文件打包

### 极域教师端模拟 (TeacherSimService)
- 新增TeacherSimService服务，管理teacher_sim.exe进程
- 通过重定向stdin发送命令，通过日志文件获取输出
- 模拟控制台UI：黑色终端风格，支持命令输入和实时日志显示
- 快捷命令按钮：列出学生、全部预览、黑屏全体、解锁全体、帮助
- 状态指示灯：未启动(灰)/运行中(绿)
- 频道配置：支持1-32频道
- 底栏新增"教师模拟"入口
- teacher_sim.exe放在Drivers目录，随程序分发

### 单文件打包 (EmbeddedResourceService)
- JiYuTrainerDriver.sys和JiYuTrainerHooks.dll嵌入为程序资源
- 程序启动时自动释放到%TEMP%\学习不通\目录
- 驱动和DLL从临时目录加载，不再需要外部Drivers文件夹
- exe体积从228KB增至808KB（含驱动+DLL）
- teacher_sim.exe(52MB)保持外部文件，不嵌入
- 文件已存在且大小相同时跳过释放，提升启动速度

## QD_V2.1_JiYuRebuild_Liquid-Glass (2026-09-09) - 错误报告与调试模式

### 错误报告服务 (CrashReportService)
- 程序异常时自动生成错误报告文件到exe目录
- 报告文件名: i.chaoxing_Crash_时间戳.txt (致命错误) / i.chaoxing_Error_时间戳.txt (功能异常)
- 报告内容: 异常位置(模块/功能)、异常信息、堆栈跟踪、内部异常链、系统信息、进程信息、加载的程序集
- 弹窗显示错误信息和报告路径
- 不需要在线提交
- UI线程异常 → 功能异常报告, 尝试继续运行
- 非UI线程异常 → 致命错误报告, 可能导致程序终止

### 调试模式控制日志
- DebugMode默认开启(true)
- 开启时: 生成i.chaoxing.log, 记录所有日志
- 关闭时: 不生成日志文件, 删除已有日志
- Logger新增Enable()/Disable()方法
- 启动时根据DebugMode决定是否启用日志
- 切换调试模式时实时启用/禁用并保存设置

### 修复: 初始化时设置保存错误
- 新增_isInitializing标志
- ApplySettingsToUI期间跳过SaveSettingsFromUI
- 避免初始化过程中CheckBox事件触发导致DebugMode被错误保存为false

---

## QD_V2.1_JiYuRebuild_Liquid-Glass (2026-09-09)

### 程序重命名
- exe文件名: i.chaoxing.exe → 学习不通.exe
- 进程名: i.chaoxing → 学习不通
- 窗口标题: 学习不通 → 学习不通2.0
- 界面显示文字统一为"学习不通2.0"
- GlassyEffect pack URI同步修改(关键,否则毛玻璃失效)
- 日志/设置/INI文件名保持 i.chaoxing.* 不变

### 文件命名统一
- 日志文件: JiYuKiller.log → i.chaoxing.log
- 设置文件: JiYuKillerSettings.xml → i.chaoxing.xml
- 与INI文件(i.chaoxing.ini)统一命名

### 截图替换功能增强
- 新增状态监测: 状态指示灯(圆点)+状态文字(已替换/未替换)
- 已替换: 绿色圆点+绿色文字+显示当前图片路径
- 未替换: 灰色圆点+灰色文字+提示尚未设置
- 新增"取消替换"按钮: 清除图片并立即应用,弹窗反馈
- 原"取消"按钮改名为"放弃选择"
- 按钮布局: 选择图片(蓝)→应用替换(绿)→取消替换(红)→放弃选择(灰)

### 底栏按钮排序与页面逻辑修复
- 底栏按钮重新排序: 快捷栏/控制台和设置/自定义设置/UDP攻击/小小私聊/截图替换/帮助文档/调试/关于
- "关于"按钮移到最后面
- 修复ShowPage()缺少PageChat/PageScreenshot隐藏导致页面不隐藏的bug
- 修复ShowPage()缺少NavChat/NavScreenshot样式重置导致按钮状态异常的bug

### 极域UDP攻击模块
- 完整移植JyUdpAttack协议(4个硬编码DMOC包)
- 支持发送消息/命令/关机/重启
- 局域网扫描: Ping并行+SendARP获取MAC,结果格式[MAC]-IP
- 本机信息显示: MAC+IP
- 发送成功/失败弹窗反馈
- 操作日志实时显示

### 小小私聊模块
- 座位号↔IP换算(6人一排布局,算法保持原样)
- 本机IP/座位号自动获取
- Ping检测同学在线状态
- UDP消息发送(格式「X号同学:消息」,UTF-16LE,端口4705)
- 聊天记录显示

### 核心功能完整移植(驱动 + DLL注入 + 控制器)

#### 内核驱动交互(DriverService.cs)
- 驱动加载/卸载: SCM服务管理,自动创建/删除服务
- 全部IOCTL控制码: 杀进程/终止/挂起/恢复/关机/重启/自我保护/初始化参数
- 驱动文件: JiYuTrainerDriver.sys(复用原项目)

#### DLL注入服务(DllInjectService.cs)
- CreateRemoteThread + LoadLibraryW注入/卸载
- WM_COPYDATA与DLL通信(消息格式hk:xxx,与原JiYuTrainerHooks.dll兼容)
- DLL回调消息处理: hkb:succ/jyk/gbmfull
- DLL文件: JiYuTrainerHooks.dll(复用原项目)

#### JiYuController完整重构
- 进程监控: 定时检测StudentMain.exe,CKInterval可配置
- DLL自动注入: 检测到极域后自动注入Hooks DLL
- 窗口处理: 广播窗口化(移除TOPMOST,调整为屏幕3/4大小居中)、黑屏处理
- 驱动管理: LoadDriver/UnloadDriver,自动发送系统版本参数
- 内核级杀进程: 驱动IOCTL + NtTerminateProcess原生API(3种模式)
- 解除网络控制: 卸载TDNetFilter驱动(sc stop/delete)
- 极域定位: 注册表+6种常见安装路径
- 停止时自动卸载注入的DLL

#### DLL设置传递机制(关键修复)
- 设置通过INI配置文件传递,非DLL消息
- 主程序写入i.chaoxing.ini的[JTSettings]节
- 发送hk:inipath:路径让DLL调用VInitSettings()重新读取
- 支持11个DLL设置项: AutoForceKill/AllowAllRunOp/BandAllRunOp/ProhibitKillProcess/ProhibitCloseWindow/DoNotShowVirusWindow/ForceDisableWatchDog/AllowGbTop/AllowMonitor/AllowControl/FakeScreenImage

### 软件高级设置(17项)
- 监控极域运行进程
- 禁止极域运行进程
- 允许屏幕广播窗口置顶
- 禁止极域结束进程
- 允许教师监视你的电脑
- 禁止极域关闭窗口
- 允许老师控制你的电脑
- 解除网络控制
- 选择极域主进程位置
- 检查间隔(CKInterval)
- 结束进程模式(KillProcess/TaskKill/NtTerminateProcess)
- 禁用内核驱动
- 驱动自我保护
- DLL注入模式(InjectMasterHelper)
- 禁止所有运行操作
- 强制关闭看门狗
- 不显示病毒窗口

### UI功能
- 快捷栏页面: 当前状态/极域控制/窗口控制/电源控制
- 本窗口置顶、杀死极域、重启极域
- 帮助文档页面: 介绍/快捷键/其他/免责声明
- 调试命令执行
- 关于页面 + 关于我页面
- 全局快捷键: Ctrl+Alt+F紧急全屏, Ctrl+Alt+H显示/隐藏
- 系统托盘: 关闭隐藏到托盘,双击显示/隐藏

### 液态玻璃悬浮底栏
- 悬浮式设计,7层液态玻璃结构
- 9个导航按钮: 快捷栏/控制台和设置/自定义设置/UDP攻击/小小私聊/截图替换/帮助文档/调试/关于
- iOS风格滑动指示器(CubicEase 350ms)
- 鼠标滚轮横向滚动,所有滚动条隐藏
- 每个按钮独立玻璃效果,悬停/按下动效

### 真正毛玻璃效果(移植自WPF-Liquid-Glass-Effect)
- 桌面截图背景 + HLSL像素着色器折射
- 自动刷新背景(窗口移动/大小变化/激活失活)
- 光感效果: 渐变边框 + 顶部高光
- 外层/中层光晕,圆角窗口
- 自定义设置: 模糊强度/内容透明度/光晕透明度

### 代码质量修复
- 修复_studentControlled永远为false
- 修复KillProcessMode设置未生效
- 修复UpdateSettings只发hk:reset
- 实现MasterHelper注入功能
- 实现NtTerminateProcess原生API调用(ntdll.dll P/Invoke)
- 清除已删除的自动更新功能残留代码
- 修复XAML中不存在的GlassCard样式导致启动崩溃

## 移植进度统计

### 已移植功能 (36/39 = 92.3%)
- 核心控制: 12/12 ✅
- 驱动/DLL: 8/8 ✅
- 增强功能: 3/4 (UDP攻击✅, 小小私聊✅, 截图替换✅, 教师端模拟❌)
- UI功能: 9/9 ✅
- 系统功能: 4/4 ✅

### 未移植/已删除功能 (3/39)
1. 教师端模拟(IMTeacher) - 原Python项目,需外部exe调用,复杂度高
2. 错误报告(BugReport) - 原项目有BugReportWindow,优先级低
3. 自动更新(Updater) - 用户要求删除

### 新增功能 (非原项目)
- 关于我页面
- 悬浮液态玻璃底栏
- 真正毛玻璃效果(HLSL着色器)
- 截图替换状态监测
- 统一日志系统(详细到行号/方法名)

## 程序信息
- 程序显示名称: 学习不通2.0
- exe文件名: 学习不通.exe
- 进程名: 学习不通
- 程序图标: JiYuTrainerLogo.ico
- 版本号: QD_V2.1_JiYuRebuild_Liquid-Glass
- 目标框架: .NET Framework 4.7.2
- 代码规模: C# 4402行(16文件) + XAML 997行(2文件)
- 日志文件: i.chaoxing.log(exe同目录)
- 设置文件: i.chaoxing.xml(exe同目录)
- INI文件: i.chaoxing.ini(exe同目录,DLL设置)

## 历史版本

### QD_V1.1_TrayIcon (2026-09-07)
- 系统托盘支持: 关闭/最小化隐藏到托盘
- 托盘双击显示/隐藏,右键菜单
- 窗口不可最大化

### QD_V1.0_WPF-Rebuild (2026-09-07)
- 从C++/Sciter迁移至C# .NET 4.7.2 + WPF
- 统一日志系统、设置持久化、进程监控、禁止运行、广播置顶控制
