## QD_V3.2 UdpGhost + DolbyVision 独立工具链（2026-09-23）

### UdpGhost 独立UDP伪造工具
- 新增独立WPF程序`UdpGhost`，不做教师端，纯UDP伪造包控制指定学生端
- 功能：黑屏/解锁（支持自定义文字，约5秒）、发消息（20字，回车发送）、关机、重启
- 屏幕监控：扫描DolbyVision发送端，双击实时观看
- UI：黑客风格，无边框圆角，滚动条隐藏
- 程序图标：udp.ico

### DolbyVision 独立屏幕监控系统
- 新增独立WinForms后台程序`DolbyVision`，完全绕过极域协议
- 发送端无窗口后台运行，UDP广播宣告存在（端口9100，每3秒）
- TCP 9101端口BitBlt抓屏MJPEG编码（8fps，质量60），支持多客户端
- 程序图标：dolby.ico
- 部署方式：手动拷贝到学生机运行（远程部署受协议限制做不到）

### 协议逆向重要发现
- **DMOC包（0x444D4F43）**：offset 100写入的命令会被自动拼接成`C:\Windows\system32\<命令>`，只能执行纯可执行文件名，不支持参数和cmd /c
- **COMD包（0x434F4D44）**：正确的远程命令执行协议，但需要教师端登录握手，纯UDP伪造工具无法使用
- **会话0隔离**：GATESRV.exe在会话0，启动的程序用户看不到，需要CreateProcessAsUser或schtasks /it
- 已验证做不通：黑屏保持、UdpGhost远程部署、UdpGhost实时监控

### 新增文档
- `极域协议逆向避坑指南.md`：记录前作者和现团队踩过的坑，帮助后续开发者


### 主程序集成UdpGhost入口
- 导航栏新增「UdpGhost」按钮（小小私聊后面）
- 新增UdpGhost页面：logo、启动/关闭按钮、状态显示、功能说明
- 新增MainWindow.UdpGhost.cs：进程管理（启动/关闭/状态检测/主程序关闭时自动关闭）
- 仿照ChatRoom的独立进程模式，UdpGhost.exe放入Drivers目录
- logo：Assets/udpghost.png

### UdpGhost版本与修复
- 版本号改为QD_V3.0_JiYuRebuild_JustForYou
- 屏幕监控窗口去掉Owner=this，不再强制在主窗口上面
- DolbyVision启动时自复制到%TEMP%\AudioSrv.exe运行，原文件可删除，任务管理器显示AudioSrv.exe
---
## QD_V3.1 代码清理（2026-09-21）

- 清理GameRoom死代码：GameMenu.xaml.cs删除StartGameRoom方法，GameMenu.xaml删除联机游戏卡片
- 项目编译0错误0警告

---
## QD_V3.1 防刷屏阈值调整（2026-09-21）

- 防刷屏窗口从3秒6条改为**5秒8条**（覆盖慢慢打字刷屏的情况）
- 输入框限制**30字**
- 惩罚机制不变：第1次禁发5秒 → 第2次禁发10秒 → 第3次全屏ban.jpg+重启
- 30秒没发消息自动重置级别

---
## QD_V3.1 GameRoom试做后删除（2026-09-20）

### GameRoom独立游戏程序（已删除）
- 尝试搭建独立WPF局域网联机游戏程序GameRoom（UDP端口47061）
- 实现了广播发现、在线玩家列表、单播/广播消息
- UI做了霓虹紫游戏风格+毛玻璃效果（VisualBrush+BlurEffect方案B）
- 图标问题反复未解决（多尺寸ICO生成+任务栏图标不显示）
- **决定删除GameRoom项目**，后续再考虑

### 修复
- teacher_sim网络碰撞检测逻辑（监听→检测→yes/no确认）
- ChatRoom防刷屏三级惩罚（3秒6条→禁发5秒→禁发10秒→全屏ban.jpg+重启）
- ChatRoom勿扰模式（保留未读计数和红点，只不弹窗）
- 虚假反监视功能完善（图片/视频替换+缩略图替换）

---

## QD_V3.0 ChatRoom 修复迭代（2026-09-19）

### 侧栏闪烁修复
- **问题**：每2秒定时器无条件 `CollectionView.Refresh()` 导致整个侧栏ListBox重绘闪烁
- **修复**：只在在线状态翻转（绿灯↔灰灯）、昵称变化、有人上下线时才Refresh()
- 心跳更新LastSeen静默进行，不触发UI重绘

### 未读计数器修复
- **问题1**：`ClearUnread()`只清全局徽标UI，不清各会话Unread字段，下次UpdateUnreadBadge从旧数据累加回来
- **修复**：遍历所有会话清零Unread，同时调SetPeerUnread清零侧栏红点
- **问题2**：收到私聊消息时`TrackUnread`创建会话传PeerIP为空字符串，后续点用户切会话时EnsureConversation只更新标题不补PeerIP，导致ClearPeerUnread用空IP找不到用户，红点消不掉
- **修复**：EnsureConversation在PeerIP为空时自动补上

### 壁纸模糊度调整
- 模糊范围从0~30改为**0~10**，默认**5**
- 壁纸始终完全不透明（不是调透明度），滑块控制BlurEffect.Radius模糊程度
- 提示文字："0 清晰 → 10 最糊"

### 待办（交给deepseek）
- 动效剩余4项：发送成功光点扩散、对方上线3s淡出、图片模糊→清晰0.2s、2px未读跑马灯
- ChatRoom全面代码审查

---

## QD_V3.0 更新 - ChatRoom独立聊天 + 全面代码审查（2026-09-18）

### ChatRoom独立P2P聊天（全新）
- **独立程序**：ChatRoom.exe，不依赖极域学生端，纯局域网P2P聊天
- **群聊+私聊**：UDP端口47060，CHAT心跳/GBRD群聊/PMSG私聊/CIMG图片
- **图片发送**：320x240 JPEG，分块传输+重复发送，群聊改单播不抢带宽
- **明暗主题**：一键切换，配置持久化
- **消息历史**：chat_history.txt持久化，启动加载最近100条
- **未读徽标**：最小化时收到消息计数，点徽标跳到最新
- **右键菜单**：复制/引用/删除/导出
- **历史搜索**：Ctrl+F实时搜索
- **emoji面板**：Ctrl+E，64个emoji，Win7兼容符号集
- **托盘**：最小化到托盘，双击唤回，右键退出
- **关闭到托盘**：点X不退出，藏到托盘
- **窗口尺寸记忆**：下次打开恢复上次大小位置
- **数据目录**：%APPDATA%\ChatRoom，自动迁移旧数据
- **消息上限**：内存最多500条，超出自动裁剪防OOM

### 代码审查修复（deepseek）
- 私聊图片误判离线后静默群发（隐私修复）
- 图片编解码无像素上限（内存炸弹防护）
- 发送结果如实报告
- 托盘失败时窗口可正常关闭
- UnInjectDll忽略等待结果(首轮误记为已修, 次轮审查发现从未实施, 现已真正修复)修复
- WM_COPYDATA长度校验
- 主程序退出路径统一（ExitApplication/ForceExit清理一致）

### 网络优化
- 心跳广播风暴修复（收到心跳不再无脑回复）
- 群聊图片改单播（不广播，避免抢局域网带宽）
- 多块图片重复次数3->2（省1/3流量）

## QD_V2.9 更新 - teacher_sim网络碰撞检测 + 底栏折射优化（2026-09-17）

### teacher_sim 网络碰撞检测（新增，防网络风暴）
- **背景**：机房多人同时使用teacher_sim导致学生端谁也连不上教师端，造成网络风暴
- **单实例检查**：锁文件+PID验证（`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`），同机器只能运行一个teacher_sim
- **静默监听5秒**：启动前只监听不发包，绑定47050专属端口发TSHB心跳（16字节：魔数+PID+启动时间戳）
- **冲突分级处理**：
  - 47050收到其他TSHB心跳 → 相同程序用户 → PID选举（小的留下，大的直接退出）
  - 4705收到非本机IP包 → 真实教师端 → 控制台输出警告，`input()`等待用户输入yes/no
  - 无冲突 → 直接启动
- **心跳线程全运行期间持续**：不仅等待输入期间，正常运行后也持续发47050心跳，确保第二个实例能检测到第一个实例
- **控制台交互**：用户否决主程序弹窗方案，改为teacher_sim控制台yes/no交互
- **修复历程**：NANC探测包干扰学生端→改静默监听；3秒太短漏检→延长到5秒；exit(0)导致主程序收不到回调→保持运行；输入yes后停止心跳→改为全运行期间持续

### 底栏液态玻璃折射优化（deepseek）
- **折射方向修正**：凹→凸（`offsetUV = -dir`→`+dir`），边缘把外侧背景卷进轮廓，才像凸起的水滴
- **圆角加料**：NavBarLens.hlsl新增c8/c9寄存器，CornerBoostPx/CornerFalloff依赖属性
- **删除无效投影层**：`Background="#01000000"`+`DropShadowEffect`实测差值为0，属死代码；GlassContainer的Margin 8→0，玻璃铺满窗口，消除8px全透明margin
- **底栏多色偏色确认修复**：自写NavBarLens.hlsl（圆角SDF+边缘因子），中心区逐像素差严格为0；NavBarScrim中性遮罩不再叠白；文字配色Freezable冻结问题已修复
- **折射强度默认**：0.5→0.35，彩边收敛成一层薄冷色

### 快捷键可编辑（deepseek）
- 快捷键两个控件TextBlock→TextBox，可直接按组合键修改
- 新增HotKeyBox_PreviewKeyDown/HotKeyToText，免重启重注册
- 热键注册失败改为界面可见红色提示，用`Marshal.GetLastWin32Error()`区分"被占用(1409)"与其他错误

### 内容卡片底色统一（deepseek）
- 新增GlassCardBrush（白渐变`#3AFFFFFF→#1FFFFFFF`）/GlassCardBorderBrush（1px `#33FFFFFF`）
- 30处卡片由`#26000000`/`#20000000`/`#10000000`统一替换
- 次级文字整体压深一档（`#666666→#404040`等），页面内容根节点加白色投影提升可读性

### 注入"假成功"修复（deepseek，严重）
- 远程线程内部异常时`GetExitCodeThread`返回异常码（如0xC0000005），旧判据只拦`==0`，报成注入成功
- 修法：注入成功判据改为目标进程模块枚举（Toolhelp32），只有模块确实出现在模块表里才算成功
- 等待时间5s→15s，注入前先查模块，已加载则跳过

### 清理死代码（deepseek）
- App.xaml删18个死资源（含51行GlassSwitch样式块）
- AppSettings删4项死设置：MonitorJiYuProcess/InjectMasterHelper/InjectProcHelper64/InjectMode
- ForceInstallInCurrentDir由死设置变为真正生效

### LICENSE与第三方声明（deepseek）
- 新增LICENSE（MIT，版权Daitangxin/DaiWicked）
- 新增THIRD-PARTY-NOTICES.md（8个第三方组件：许可/来源/我们改了什么）
- third_party/目录（6个txt许可原文）
- README补致谢/许可/构建说明/免责声明/自检钩子表

### Win7 teacher_sim兼容性（已放弃，回滚）
- 尝试5种方案均失败：Pillow降级、关闭UPX、PyInstaller 4.x、onedir模式、PYTHONUTF8=1
- 根因：`sys.executable`内存被破坏（乱码中可见SYSTEMDRIVE=C:、路径片段），疑似Win7+Python3.9+PyInstaller深层兼容性问题
- 回滚到18e4c54版本（Win10稳定版），Win7暂不支持

### 已知限制
- teacher_sim仅支持极域4.0协议学生端，6.0学生端登录有UNKNOWN包（协议逆向难度高，暂不做）
- Win7上teacher_sim.exe启动后崩溃（0xC0000005），已放弃修复
- 关闭allowMonitor后极域可能崩溃（原作者已知问题，32位Win10尤甚）

---

## QD_V2.9 正式版 - 液态玻璃底栏 + 驱动/注入判据修正

### 版本信息
- **版本号**：QD_V3.0_JiYuRebuild_JustForYou
- **发布日期**：2026-09-16
- **Commit**：b3beb68 ~ 7087418（主项目）
- **交接文档**：`docs/液态玻璃-交接说明.md`

### 新增：底栏液态玻璃（折射）
- **自写像素着色器**：`Effects/NavBarLens.hlsl` → 编译产物 `Effects/NavBarLens.ps`（ps_2_0，1368 字节，用 Windows SDK 的 fxc 编译）
- **边缘向内折射**：以"圆角矩形有符号距离场"为几何，沿半径向内位移，视觉上把背景内容拉进来放大 —— 这就是"水滴缩放"感
- **边缘色散**：R 少偏折、B 多偏折，用绝对像素量（默认 ±1.2px）；比例写法只有 0.14px 差异，肉眼看不见
- **菲涅尔边缘柔光**：窄于折射带的一圈冷色加光，做出玻璃"厚度感"
- **外扩 24px 采样**：真正的边缘折射必须能采到"玻璃形状之外"的背景像素，否则偏移只会夹边、看着很假
- **分层**：`NavBarLens`(挂 shader) → `NavBarGlass`(VisualBrush 采样 + BlurEffect) 嵌套，父元素 shader 的输入即子元素已模糊的结果，避免在 ps_2_0 里写高斯核
- **防整片偏色**：折射量、色散量、柔光量三者全部乘"边缘因子"，该因子在玻璃中心严格为 0（上一版 `GlassyEffect` 按窗口几何算透镜中心、套到子区域才整片偏色）
- **设置项**：启用开关 / 折射强度滑块(0~1) / Tier0 强制开关；界面另有状态提示行，明确显示"已启用 / 未启用（Tier0）/ 着色器加载失败 / 已关闭"

### 新增：底栏液态动态效果
- **指针跟随柔光**：鼠标在底栏移动时柔光团跟随（直接赋 TranslateTransform，不建动画时钟，无 CPU 开销）
- **点击波纹**：按压点扩散一圈波纹，520ms 后自动移除，且有数量上限防止狂点堆积
- 两者都放在独立 `Canvas`（`NavBarFxLayer`）里：不参与布局、`IsHitTestVisible=False`、不设 `e.Handled`，因此不影响按钮点击

### 修复：注入"假成功"（严重）
- **现象**：远程线程内部异常时 `GetExitCodeThread` 返回的是异常码（如 `0xC0000005`），旧判据只拦 `== 0`，于是把它当成"模块基址"报成注入成功
- **修法**：注入成功判据改为**目标进程模块枚举**（Toolhelp32，标志 `0x18`）—— 只有"模块确实出现在目标进程模块表里"才算成功；退出码降级为诊断信息
- **同时**：等待 5s → 15s（实测正常注入 0.2~3.5s，但出现过 >5s 与 9.6s 的慢加载）；注入前先查模块，已加载则跳过重复注入

### 修复：底栏文字配色永远不生效
- **根因**：`Application.Resources` 会把写入的 Freezable 自动 `SealIfSealable`（即冻结）；代码 `Clone()` 出可变副本后又写回字典，**刚克隆的副本立刻被重新冻住**，导致 `BeginAnimation` 必抛异常、配色 100% 失败
- **修法**：删除写回字典那一句；克隆体只作元素本地值（本地值优先级高于 Style Setter 里的 DynamicResource）

### 修复/加固：驱动通信层与日志
- 卸载驱动日志按真实结果记录（服务不存在属正常 → INFO；打开服务异常时返回失败并记 ERROR；不再无条件写"驱动已卸载"）
- `HKLM\SYSTEM\CurrentControlSet\Enum\Root\LEGACY_*` 受系统 ACL 保护，Win7 上必然删不掉且无害 → 日志由 WARN 降为 DEBUG 并补充说明

### 调整：透明度文案 + 文字可读性（不改任何透明度数值）
- **文案**：`内容层透明度` 滑块下补三行说明，明确"这一个值同时决定三件事"——白纱厚度、自定义壁纸可见度、滑块位置；0% 白纱最厚（壁纸清晰）、100% 白纱消失（壁纸被遮住、直接看到桌面）。原"自定义壁纸"卡片提示与说明页里那句**写反了的**"内容层透明度越低，背景越清晰"一并改正
- **文字对比（着色）**：次级文字整体压深一档 —— `#666666→#404040`、`#555555→#383838`、`#3A3A3A→#262626`、`#2A2A2A→#1A1A1A`（只改 `Foreground`，不涉及任何透明度/图层）
- **文字对比（字形光晕）**：页面内容根节点加一层 `ShadowDepth=0` 的白色投影（`BlurRadius=3, Opacity=0.6`）—— 等于给字形外缘补一圈与桌面无关的亮底，深色文字压到桌面暗部/杂乱区域时仍可分辨；因为卡片底色半透明，光晕强度按 alpha 自然衰减，**字形亮、卡片边缘几乎看不见**（实测 2× 放大对比：字形边缘依旧锐利，不再糊进桌面文字里）。整页只做一次渲染合成，开销远低于逐控件 `Effect`
- **自检钩子**：环境变量 `JYKILLER_START_PAGE=<页面名>` 可指定启动页（配合截图核对各页面，鼠标点击无法模拟时用）

### 调整：视觉收敛 + 内容卡片统一 + 热键诊断
- **折射强度默认 `0.5 → 0.35`**：强度 1.0 时边缘折射 16px、色散彩边明显，观感更像"水波"；0.35 约 5.6px，彩边收敛成一层薄冷色
- **内容卡片底色统一**：新增 `GlassCardBrush`（白渐变 `#3AFFFFFF→#1FFFFFFF`）/ `GlassCardBorderBrush`（1px `#33FFFFFF`），30 处卡片由 `#26000000`/`#20000000`/`#10000000` 统一替换 —— 此前"快捷栏卡片白渐变、设置页卡片 15% 黑"两套底色夹着同一套深色文字，深色字落在浅底上对比才够；另修正 4 处漏网的 `#FF888888` 浅灰注释（教师模拟日志框是深底浅字，**刻意保留**）
- **热键注册失败改为界面可见提示**：`高级设置 → 快捷键` 卡片新增一行红色提示（默认隐藏），用 `Marshal.GetLastWin32Error()` 区分"被其它程序占用(1409)"与其它错误码，并补 `SetLastError = true`；日志同步升为 WARN
  - **实测发现**：`Ctrl+Alt+F` 在测试机上一直被占用（1409），即"紧急全屏"从未生效而旧代码只写 INFO —— 这类"静默失效"现在会直接显示给用户

- **修复：底栏折射方向反了（观感「凹」→「凸」）**：`NavBarLens.hlsl` 原为 `offsetUV = -dir * ...`（**向内**取样），边缘显示的是玻璃内侧内容，看着像凹坑；改为 `+dir`（**向外**取样），边缘把玻璃外侧的背景卷进轮廓并压缩，才像凸起的水滴。依据参考实现 `liquid-glass-js-main/container.js:408,423`（外法线 + `textureCoord +=`，即向外取）
- **修坑：`fxc` 不接受 UTF-8 BOM**：`.hlsl` 此前被 BOM 统一扫过，导致文档里那条 `fxc ... NavBarLens.hlsl` 直接报 `X3000 Illegal character (1,1)`、编译产物根本没更新（改动「看似无效」的隐形原因）。`NavBarLens.hlsl` 现刻意**不带 BOM**，并在文件头注明

### 其他
- **量化自检**：调试页命令 `navlens [强度]`，或环境变量 `JYKILLER_NAVLENS_SELFTEST=1` 启动 → 打印中心/边缘差异与逐通道位移，并导出对比 PNG；另有隔离单测脚本 `tools/NavBarLens-unit-test.ps1`
- **编码**：全项目源文件补齐 UTF-8 BOM（此前因用 GBK 保存导致中文注释/日志字符串成片损坏，乱码已清零）

---

## QD_V2.7 正式版 - 虚假反监视（缩略图+实时屏幕替换）

### 版本信息
- **版本号**：QD_V2.7_JiYuRebuild_JustLikeThis
- **发布日期**：2026-09-14
- **Commit**：7c6f3cb（主项目）/ 4bf282d（DLL）

### 虚假反监视页面重构
- **页面更名**：从"截图替换"改名为"虚假反监视"
- **功能分离**：缩略图替换和实时屏幕替换完全独立，各有独立预览框和按钮
- **删除禁用警告**：移除缩略图功能禁用UI和警告文本

### 缩略图替换修复（JPEG编码层方案）
- **原理**：hook `EncodeToJPEGBuffer`，在JPEG编码完成后替换输出缓冲区
- **不依赖关闭allowMonitor**：不触碰GDI/驱动，仅替换80x60缩略图编码输出
- **修复1 - INI路径错误**：`LoadFakeJpeg`用`GetModuleFileNameW(NULL)`获取极域学生端路径，导致读不到主程序INI。改为`mainIniPath`（主程序通过inipath消息传来的路径）
- **修复2 - JPEG参数错误**：日志验证`a6[0]=0xAC3`是大小值不是指针。正确参数：`a5`=输出JPEG缓冲区指针，`a6[0]`=输出大小
- **修复3 - JPEG文件过大**：质量90生成2767字节 > 输出缓冲区2477字节，替换条件不满足。质量降为60（约1800字节）
- **应用后自动重启极域**：确保hook重新加载配置

### 实时屏幕替换（H.264编码层方案）
- **原理**：vtable patch `IH264EncoderModule::CreateCBREncoder`返回接口的Encode虚函数（vtable[9]），在编码前替换YUV输入缓冲区
- **不依赖关闭allowMonitor**：不触碰GDI/驱动，仅替换编码器输入
- **图片替换**：选择图片后即时生成1024x768 YV12假帧，教师端实时观看显示替换图片
- **视频替换**：ffmpeg输出rawvideo YUV420P 320x240 → C#最近邻缩放到1024x768并交换U/V（I420→YV12）→ 约10fps流畅播放
- **即时生效**：无需重启极域，应用后下次观看即生效
- **配置路径**：`C:\Users\Public\JiYuKiller\realtime_replace.ini` + `fake_screen.yuv`
- **模式切换修复**：移除Apply/Disable中的InitRealtimeReplace调用，避免LoadCurrent覆盖用户已选择的视频/图片状态

### 视频预览功能（静态提示+系统播放器）
- **32位性能优化**：原MediaElement内嵌预览在32位系统下导致主程序严重卡顿，改为静态提示方案
- **预览框显示文字**："点击下方按钮，用系统播放器预览视频"
- **系统播放器预览**：点击按钮用Process.Start调系统默认播放器打开视频（新窗口）
- **不影响图片预览**：选择图片时预览框正常显示图片

### 自定义设置默认值优化
- **模糊强度**：90% → **80%**
- **内容层透明度**：50% → **25%**
- **外层光晕**：20% → **5%**
- **中层光晕**：30% → **5%**
- **修复所有默认值入口**：XAML Slider Value、AppSettings.GlassOpacity、GlassyWindowManager._blurIntensity、Border Opacity、Text显示百分比全部同步修改
- **MainWindow_Loaded同步**：_glassyManager初始化后同步SliderBlurIntensity.Value，确保默认值生效

### 帮助文档完善
- **虚假反监视使用说明**：在「帮助文档」→「介绍」页面添加详细使用说明
  - 缩略图替换使用方法
  - 实时屏幕替换-图片模式
  - 实时屏幕替换-视频模式
  - 注意事项

### 广播窗口控制（回退原作者逻辑）
- **回退原因**：多次修改（自动拦截全屏、自动调整大小）导致逻辑混乱，老师来检查时无法恢复全屏
- **原作者设计**：fakeFull变量控制是否允许全屏，FixWindow只改样式不改大小，右键菜单手动切换全屏/窗口模式
- **DLL回退**：恢复fakeFull控制+白名单机制，移除自动拦截全屏和调试日志
- **主程序回退**：FixBroadcastWindow只改样式，移除自动调整大小逻辑

### 技术突破记录
- **真实编码入口定位**：`LibVOIP10.dll!IH264EncoderModule::CreateCBREncoder`（ordinal 71）
- **vtable patch方案**：非Mhook，直接修改虚函数表，稳定可靠
- **YUV格式确认**：极域用YV12（Y+V+U），非标准I420（Y+U+V），U/V反序会导致颜色负片
- **第二次观看崩溃根因**：vtable共享，重复patch导致g_origEncode被覆盖→无限递归→栈溢出。修复：检查是否已patch
- **网络层I帧替换排除**：极域编码器首帧I帧+后续全P帧，无周期性I帧，网络层替换不可行

### 待办事项
- **视频替换流畅度优化**：当前约10fps，可尝试ffmpeg输出更大分辨率（640x480）、双缓冲
- **32位/64位ffmpeg适配**：~~正式机房使用需要识别系统位数~~ **已完成**，64位系统优先使用ffmpeg64.exe（82MB），32位系统回退到ffmpeg.exe（35MB）
- **GDI hook方向研究**：~~作为长期待办~~ **暂缓**，当前编码层方案已满足需求

## QD_V2.6 正式版 - teacher_sim实时观看 + 群发消息 + 快捷指令

### 版本信息
- **版本号**：QD_V2.6_JiYuRebuild_IMTeacher
- **发布日期**：2026-09-13
- **Commit**：6937470

### teacher_sim实时观看功能
- **ffmpeg集成**：打包ffmpeg.exe（87MB）随teacher_sim分发，H.264实时解码不再依赖系统环境
- **teacher_sim.py路径修复**：优先查找同目录ffmpeg.exe，其次sys.executable目录，最后fallback到imageio_ffmpeg
- **路径统一**：teacher_sim.exe和ffmpeg.exe统一放在Drivers\目录下
- **画面滚动修复**：根因是编码尺寸1152×768与可见尺寸1150×759不匹配，ffmpeg crop过滤器输出尺寸与读取尺寸不一致导致帧边界错位。修复：去掉crop，ffmpeg输出完整编码尺寸，Pillow显示时裁剪
- **黑边去除**：ffmpeg输出完整1152×768，PIL显示前crop到1150×759可见区域
- **窗口可调节大小**：监听<Configure>事件，每帧按窗口尺寸动态缩放（LANCZOS，保持宽高比）
- **UI按钮**：教师模拟页面目标操作区域新增"启动观看"(紫色)和"停止观看"(灰色)按钮
- **实时观看协议**：TCP 4806/UMSP channel 10，HHRF(H.264 Annex-B)封装，U/V色度平面交换

### 群发消息功能
- **teacher_sim新增msgall命令**：向所有已登录学生群发消息，间隔50ms避免丢包
- **主程序UI**：教师模拟页面新增紫色"群发消息"按钮，弹窗输入后发送
- **教师名修改**：默认教师名从admin改为"神秘人"

### UDP攻击快捷指令
- **快捷指令按钮**：记事本、画图、写字板、字符映射表、屏幕键盘、放大镜
- **移除失效指令**：计算器(Win10 UWP)、命令行(极域禁用)、任务管理器(极域禁用)、注册表(权限不足)
- **命令规范提示**：说明输入system32下的可执行文件名
- **UI提示**："快捷指令（可能对部分系统无效）"

### UI提示
- **教师模拟快捷指令提示**："黑屏锁定/解锁、关机/重启等功能可能对部分系统（尤其是32位Win10）无效"

### DLL/SYS嵌入主程序
- JiYuTrainerHooks.dll和JiYuTrainerDriver.sys已作为嵌入资源打包进学习不通.exe
- 程序启动时自动释放到%TEMP%\学习不通\目录
- 打包时无需额外包含这两个文件

### 截图替换（编码层方案）
- 采用EncodeToJPEGBuffer输出替换，完全重写原作者GDI Hook方案
- 缩略图80×60，垂直翻转+红蓝通道交换，应用后自动重启极域
- 功能稳定，不依赖关闭allowMonitor，不触碰GDI/驱动层

### 实时监控抓包分析
- 三次抓包确认实时屏幕协议：UDP 5575端口，1152×768，约26fps，H.264编码
- HHRF(H.264)/HJRF(JPEG)封装，TKPC外层+12字节分片
- 实时大屏幕网络层替换难度极高，暂列为长期预研方向

### 已知问题
- Win10黑屏锁定后无法解锁（黑屏时发MESS+COMD双锁定，解锁只发MESS，需抓真实教师端解锁包）
- teacher_sim学生端登录协议仅支持旧版学生端，6.0学生端无法登录（协议逆向难度高）
- 关闭allowMonitor后极域可能崩溃（原作者已知问题，32位Win10驱动兼容性）

## QD_V2.6 开发中 - teacher_sim实时观看 + 截图替换

### teacher_sim实时观看功能修复
- **ffmpeg集成**：打包ffmpeg.exe（87MB）随teacher_sim分发，H.264实时解码不再依赖系统环境
- **teacher_sim.py路径修复**：优先查找同目录ffmpeg.exe，其次sys.executable目录，最后fallback到imageio_ffmpeg
- **路径统一**：teacher_sim.exe和ffmpeg.exe统一放在Drivers\目录下（主程序代码写死Drivers\teacher_sim.exe）
- **画面滚动修复**：根因是编码尺寸1152×768与可见尺寸1150×759不匹配，ffmpeg crop过滤器输出尺寸与读取尺寸不一致导致帧边界错位。修复：去掉crop，ffmpeg输出完整编码尺寸，Pillow显示时裁剪
- **黑边去除**：ffmpeg输出完整1152×768，PIL显示前crop到1150×759可见区域
- **窗口可调节大小**：监听<Configure>事件，每帧按窗口尺寸动态缩放（LANCZOS，保持宽高比）
- **UI按钮**：教师模拟页面目标操作区域新增"启动观看"(紫色)和"停止观看"(灰色)按钮
- **实时观看协议**：TCP 4806/UMSP channel 10，HHRF(H.264 Annex-B)封装，U/V色度平面交换
- **已知限制**：Pillow/Tkinter显示窗口，远控模式下鼠标坐标映射基于缩放后图片尺寸

### 实时监控抓包分析（已完成）
- **三次抓包**：最终在realtime_fullscreen2.pcapng（22792包）中找到实时屏幕协议
- **端口**：UDP 5575（实时查看，标识0x3e2c）、UDP 5566（屏幕广播，标识0x2377）
- **参数**：1152×768分辨率，约26fps，每帧压缩后约2900字节
- **编码**：H.264（HHRF记录），TKPC外层+12字节桌面分片
- **组播**：5563→225.2.2.29、5572→225.2.2.35（帧同步心跳）
- **文档**：实时监控抓包分析报告.md

## QD_V2.6 开发中 - 截图替换（编码层方案完整版）

### 新增
- **截图替换功能重新启用**：采用编码层替换方案，完全重写原作者GDI Hook方案
- **EncodeToJPEGBuffer参数逆向**：通过四轮DLL注入日志探测，100%确认函数参数含义
  - a5 = 输出JPEG缓冲区指针（FF D8 FF DB开头，FF D9结尾）
  - a6->size = 输出JPEG大小
  - a2=80, a3=60, a4=240(步长), a7=90(JPEG质量)
- **DLL端实现**：hkEncodeToJPEGBuffer中用预编码JPEG替换输出缓冲区
- **主程序端实现**：用户选图→自动缩放80×60→JPEG编码质量90→写入INI
- **应用后自动重启极域**：极域会缓存缩略图JPEG，应用后自动重启学生端使设置生效
- **UI告示**：截图替换页面添加"应用后将自动重启极域学生端"提示
- **技术文档**：EncodeToJPEGBuffer参数逆向与截图替换实现.md

### 修复
- **缩略图上下反向**：极域EncodeToJPEGBuffer输入是bottom-up BMP，输出JPEG本身反向，教师端不翻转显示。生成JPEG前添加垂直翻转（RotateNoneFlipY）
- **缩略图颜色负片**：极域整条链路按BGR处理，C# GDI+按RGB编码导致红蓝交换。生成JPEG前添加红蓝通道交换（SwapRedBlue）
- **截图替换不再依赖关闭「允许教师监视电脑」**
- **不再触碰GDI/驱动层**：完全在编码输出层操作，避免32位Win10崩溃问题
- **仅替换80×60缩略图**：不影响屏幕广播等其他功能
- **__try/__except异常保护**：替换失败保持原编码结果

### 探测结果
- **EncodeToJPEGBufferI422**：hook探测确认实时查看学生屏幕时**不被调用**，实时大屏幕可能用原始位图或其他编码方式
- **I422探测代码保留在DLL中**（hk40），前20次调用输出参数日志，便于后续分析

### 技术细节
- INI新增配置项：[JTSettings] FakeJpegPath
- 假JPEG文件：程序目录下 ake_screenshot.jpg
- 替换条件：a2==80 && a3==60 && 大小<=65536
- DLL函数数量：499个（含I422探测）
- 原作者GDI Hook方案分析：hkGetDesktopWindow/hkGetWindowDC/hkCreateDCW三个hook，崩溃根因是CreateDCW返回NULL导致空指针访问

### 与原作者方案对比
| 维度 | 原作者GDI Hook | 编码层替换（当前） |
|------|---------------|-------------------|
| 原理 | CreateDCW返回NULL + 假窗口DC | EncodeToJPEGBuffer输出替换 |
| 依赖 | 需关闭allowMonitor | 不依赖allowMonitor |
| 触碰层级 | GDI/驱动层 | 纯用户态编码层 |
| 32位Win10兼容性 | 已知崩溃问题 | 无已知兼容性问题 |
| 影响范围 | 所有GDI绘制 | 仅80x60缩略图编码 |

## QD_V2.6 开发中 - 截图替换（编码层方案）

### 新增
- **截图替换功能重新启用**：采用编码层替换方案，完全重写原作者GDI Hook方案
- **EncodeToJPEGBuffer参数逆向**：通过四轮DLL注入日志探测，100%确认函数参数含义
  - a5 = 输出JPEG缓冲区指针（FF D8 FF DB开头，FF D9结尾）
  - a6->size = 输出JPEG大小
  - a2=80, a3=60, a4=240(步长), a7=90(JPEG质量)
- **DLL端实现**：hkEncodeToJPEGBuffer中用预编码JPEG替换输出缓冲区
- **主程序端实现**：用户选图→自动缩放80×60→JPEG编码质量90→写入INI
- **技术文档**：EncodeToJPEGBuffer参数逆向与截图替换实现.md

### 修复
- 截图替换不再依赖关闭「允许教师监视电脑」
- 不再触碰GDI/驱动层，避免32位Win10崩溃问题
- 仅替换80×60缩略图，不影响屏幕广播等其他功能
- __try/__except异常保护，替换失败保持原编码结果

### 技术细节
- INI新增配置项：[JTSettings] FakeJpegPath
- 假JPEG文件：程序目录下 ake_screenshot.jpg
- 替换条件：a2==80 && a3==60 && 大小<=65536
- DLL函数数量：497个（调试代码已清理）
# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.5_JiYuRebuild_IMTeacher (2026-09-13) - 教师模拟完善 + 密码读取 + 全体命令

### 极域查看密码功能（新增）
- 对照原项目JiYuTrainer_Next-master的ReadTopDomanPassword和UnDecryptJiyuKnock实现
- 支持两种模式：
  - **4.0老版本**：读取HKLM\SOFTWARE\TopDomain\e-Learning Class Standard\1.00的UninstallPasswd键，解析Passwd[xxxxxx]格式
  - **6.0版本**：读取HKLM\SOFTWARE\TopDomain\e-Learning Class\Student的Knock1 REG_BINARY，两轮DWORD异或解密（0x50434C45 → 0x454C4350）
- C#实现：JiYuController.ReadJiYuPassword方法
- 64位注册表用RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
- UI：设置页面添加"读取极域密码"按钮（青色背景#FF17A2B8）
- 万能密码：mythware_super_password

### teacher_sim全体关机/重启命令（新增）
- 新增send_shutdown_all函数，遍历所有已登录学生逐个发送
- 命令别名：shutdown_all/sdall/全体关机，reboot_all/rball/全体重启
- 支持可选参数：倒计时秒数 + 提示文字
- 每台之间间隔0.1秒，避免网络突发拥塞
- 主程序UI快捷命令区域新增「全体关机」「全体重启」按钮，带确认弹窗

### teacher_sim多学生info.json修复（重大）
- **根因**：handle_mess中type 5系统信息包要求len(payload)>=0x2E0(736字节)，32位win10学生端的包可能被过滤
- **修复4处**：
  1. _parse_student_info添加wstr/rd_u32/rd_bytes边界保护，payload不完整时不崩溃
  2. handle_mess type 5长度检查从0x2E0放宽到0x100(256字节)，日志级别从debug改为warning
  3. request_info_with_retry从retries=2/delay=5改为retries=3/delay=8，失败后保存基础档案
  4. 学生登录成功后立即调用save_student_profile保存基础档案（IP+最后在线时间）
- 验证：32位win7和32位win10同时连接都能生成info.json

### teacher_sim 6.0协议完善
- 学生登录修复：waca()函数IP字段错误（教师IP→学生IP）
- Python闭包变量捕获bug修复：_delayed_info用默认参数sip=sip
- CANC包结构修正、NIPQ 4809端口无需回复
- preview功能最终用应用层节流方案修复（TRMC回复LPNT(enabled=1)+DMOC，应用层丢弃多余帧）
- preview清晰度320x240，登录时抓一张，后续手动抓取
- info.json顺序：设备IP → MAC → 系统 → 最后在线 → 设备信息 → 当前窗口名称 → 窗口列表 → 设备进程列表
- 学生列表显示IP+MAC，可滚动，选中后自动填充目标IP
- 控制台上下布局（上stdout下日志）

### 教师屏幕广播修复（重大）
- 对照deepseek诊断报告和原项目JiYuTrainer_Next-master分析
- 窗口控制模块：DLL的hkb:*回调全部接线到实际窗口操作
- HandleDllCallback改为薄转发，统一交给JiYuController.HandleVirusCallback
- 新增ManualTop/ManualFull方法，对应参考实现
- AllowGbTop改被动模式：true=不干预TOPMOST，false=去掉TOPMOST
- 广播窗口：移除周期性75%×80%缩放，只改样式位
- 黑屏窗口：改回原项目行为（SW_HIDE隐藏）
- 验证：教师发起广播后学生端能正常看到教师屏幕

### 小游戏功能（新增）
- 底栏新增「小游戏」入口
- 游戏菜单：扫雷 + 恐龙跳两个卡片入口
- **扫雷**：9×9网格/10雷，左键挖雷右键标旗，首次点击安全，显式栈展开防栈溢出
- **恐龙跳**：纯C# Canvas实现，空格/点击跳跃，仙人掌随机生成
  - 跳跃参数：起跳初速-10，重力0.58
  - 速度曲线：初始3.4px/tick，每100分+0.3，最大7.5
- 放弃WebBrowser/WebView2方案：主窗口AllowsTransparency分层窗口与原生HWND控件airspace不兼容

### 截图替换功能暂时禁用
- deepseek分析DLL源码发现上游bug
- 页面添加黄色警告告示（用户指定文案）
- 操作按钮IsEnabled=False
- 已知问题：关闭allowMonitor后教师端查看屏幕可能导致极域崩溃（原作者已知问题，32位Win10尤甚）

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-13) - 小游戏功能 + 截图替换禁用

### 小游戏功能（新增）
- 底栏新增「小游戏」入口
- 游戏菜单：扫雷 + 恐龙跳两个卡片入口
- **扫雷**：9×9网格/10雷，左键挖雷右键标旗，首次点击安全，显式栈展开防栈溢出，计时器，踩雷显示所有雷+标错旗，胜利自动插旗
- **恐龙跳**：纯C# Canvas实现，空格/点击跳跃，仙人掌随机生成，分数递增+速度加快
  - 跳跃参数：起跳初速-10，重力0.58（最大跳跃高度约86px）
  - 速度曲线：初始3.4px/tick，每100分+0.3，最大7.5（约1367分到顶，提速平缓）
- 恐龙跳放弃WebView2方案：主窗口AllowsTransparency分层窗口与原生HWND控件存在airspace问题，且需额外NuGet包+WebView2 Runtime依赖

### 截图替换功能暂时禁用
- 页面添加黄色警告告示：依赖allowMonitor关闭可能导致极域崩溃（原作者已知问题，32位Win10尤甚），DLL注入实现不稳定
- 操作按钮IsEnabled=False

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-13) - teacher_sim学生登录修复 + 功能增强

### teacher_sim学生登录修复（重大）
- **根因**: waca()函数中WACA回复包的IP字段错误地使用了教师IP，真实教师端用的是学生IP
- **修复**: socket.inet_aton(ip) → socket.inet_aton(sip)
- **默认教师名**: 从'1'改为'admin'
- **验证**: 6.0学生端可正常登录，能收到学生信息、进程列表、窗口列表、LANT缩略图
- **抓包分析**: 使用Wireshark抓取真实教师端↔6.0学生端完整握手流程，确认CANC动态哈希和4809端口NIPQ非必须

### teacher_sim功能增强
- **日志路径统一**: 从硬编码桌面改为exe目录/teacher_sim/（BASE_DIR自动检测PyInstaller/开发模式）
- **禁用PowerShell日志窗口**: spawn_log_window()已注释，日志全部通过主程序UI显示
- **preview清晰度**: 从80x60提升到640x480
- **preview保存路径**: 从桌面改为teacher_sim/students/<IP>/screenshots/screenshot_<时间戳>.jpg
- **preview策略**: 登录时抓一张，收到后发送LPNT(disable)停止自动刷新，后续手动抓取
- **学生建档**: 新增save_student_profile()，登录后自动写入teacher_sim/students/<IP>/info.json（含IP/设备信息/进程列表/窗口列表）

### C# UI增强
- **已登录学生列表**: 新增ListBox（IP+MAC显示），可滚动，选中后自动填充目标IP，刷新按钮发list命令
- **控制台上下布局**: 上stdout交互控制台，下日志输出区，中间GridSplitter可拖拽调整
- **日志输出区**: 独立TextBox显示teacher_sim.log内容，清空日志按钮
- **OnLogFileOutput事件**: TeacherSimService新增，日志文件内容与stdout分离输出
# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-12) - 窗口控制模块修复 + teacher_sim集成

### 窗口控制模块修复（对照JiYuTrainer_Next-master）
- **根因修复**: DLL的hkb:*回调之前只打日志不执行动作，现在全部接线到实际窗口操作
- **HandleDllCallback改为薄转发**: 统一交给JiYuController.HandleVirusCallback处理
- **新增ManualTop/ManualFull方法**: 对应参考实现，执行真正的SetWindowLong/SetWindowPos
- **AllowGbTop改被动**: true=不干预TOPMOST，false=去掉TOPMOST（不再强制置顶）
- **新增gbFullManual状态**: 用户从DLL菜单进全屏后，轮询不再把边框加回去
- **严格窗口控制模式**: setAutoIncludeFullWindow已实现，其他极域全屏窗口也处理
- **跳过Virus Window**: 枚举窗口时跳过"JiYu Trainer Virus Window"
- **hk:ckstat握手**: 注入成功后补发，DLL做版本探测+键盘解锁
- **hs:回发**: hkb:succ时回发主窗口句柄给DLL
- **hkb:immck**: 立即刷新窗口枚举
- **去掉自动打开AllowGbTop**: 用户手动置顶不再自动打开设置开关

### teacher_sim集成
- 修复日志路径: 从桌面改为exe目录/teacher_sim/teacher_sim.log（与teacher_sim.py一致）
- 修复日志等待误报: 等待时间从10秒增加到30秒，进程仍在运行时不报警告
- 保留32位teacher_sim.exe（15400270 bytes）
- 已知限制: teacher_sim是极域V6.0协议，与v4.0 2016豪华版不兼容，学生无法登录

### 广播窗口修复
- 去掉周期性75%×80%缩放（原项目FixWindow从不调整尺寸）
- FixBroadcastWindow只改样式位，不移动/缩放窗口
- 补发hw:消息通知DLL接管窗口过程
# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-10) - 用户协议窗口 + 玻璃效果统一

### 用户许可协议窗口 (AgreementWindow)
- 移植原jiyukiller的IDD_DIALOG_ARGEEMENT协议对话框
- 首次启动时弹出, 同意后才进入主界面
- 协议内容: 免责声明/法律声明/版权声明, 保留原文案
- 按钮: "极域给我爬"(同意) / "杰哥不要"(拒绝)
- 协议窗口添加start.png图片(118KB)
- AppSettings新增Argeed字段, 同意后保存到i.chaoxing.xml
- 拒绝协议则程序退出

### 协议窗口毛玻璃效果
- 分层结构: 桌面截图(Blur Radius=20) -> 半透明白色覆盖(#B8FFFFFF) -> 顶部高光渐变 -> 内容
- Window_Loaded中捕获全屏桌面, 按窗口位置裁剪区域作为背景
- 标题栏/按钮区改为半透明, 文字颜色加深保证可读性

### 彩蛋窗口毛玻璃效果
- 背景: #FF1A1A1A -> #C81A1A1A(78%不透明深色, 可透视主窗口)
- 新增顶部高光层: LinearGradient 30%白->透明
- 边框/阴影/标题栏/按钮区统一调整为半透明玻璃风格
- 视频区域保持黑色不透明(视频播放需要)

### 启动流程修复
- 移除App.xaml的StartupUri, 改为ShutdownMode=OnExplicitShutdown
- 同意协议后手动new MainWindow().Show(), 避免协议窗口关闭后应用退出
- 拒绝协议时关闭日志后Shutdown

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - COUI UI/动画三阶段升级

### 第一阶段: 按钮动画 + 滑块样式 + 页面过渡
- GlassButton: Storyboard按压缩放0.92+回弹1.02过冲, 悬停上移1px+状态层8%黑
- NavGlassButton: 动画作用在Grid根元素整体缩放(不受BlurEffect影响), 悬停玻璃变亮
- GlassSlider: 新样式, Thumb悬停放大1.1/拖动放大1.25, 带DropShadow阴影
- 4个滑块引用GlassSlider样式
- ShowPage页面淡入过渡: Opacity 0->1, 300ms CubicEase EaseOut
- CrashReportService添加UI/动效状态诊断

### 第二阶段: 视觉增强
- 光感层: GlassyLayer上方RadialGradient, 顶部光源顶部亮边缘暗
- 噪声抗色带: 程序化生成128x128 Gray8噪声纹理, ImageBrush平铺, Opacity=1%
- 5级Surface配色资源
- 渐进模糊底栏: 底栏顶部LinearGradient遮罩
- 修复: 噪声层透明度从5%降到1%, 修复玻璃背景变暗

### 第三阶段: 动画增强
- 页面方向过渡: 根据导航顺序从左/右滑入+淡入, 280ms CubicEase
- GlassSwitch样式: 开关滑块滑动动画
- BloomStroke简化版: GlassContainer边框LinearGradient顶部亮底部暗
- 修复: 页面方向过渡用BeginAnimation替代Storyboard

### 按钮动画修复
- 修复底栏按钮无动效: 动画移到Grid根元素整体缩放
- 修复按钮变蓝色: 移除背景色ColorAnimation, 只通过stateLayer叠加暗色
- 修复GlassSlider缺少SliderRepeatButtonTransparent样式

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 自定义壁纸功能

### 自定义壁纸 (WallpaperService)
- 新增WallpaperService服务：壁纸加载/cover裁切/10MB文件验证/缩略图/缓存
- 支持格式：.jpg/.jpeg/.png/.bmp，最大10MB
- 壁纸裁切到窗口尺寸450x600（cover模式，保持宽高比居中裁切）
- 三按钮：选择壁纸/应用壁纸/恢复默认
- 壁纸丢失检测：原文件删除时显示红色提示
- 启动自动加载已保存壁纸

### Liquid Glass分层结构重构
- 原分层：BackdropLayer(桌面) → GlassyLayer(绑定桌面) → ContentLayer(半透明白+UI)
- 新分层：BackdropContainer(桌面+白色覆盖层+壁纸层) → GlassyLayer(VisualBrush绑定整个容器) → ContentLayer(透明+UI)
- 壁纸和桌面截图都被玻璃效果模糊
- 白色覆盖层从ContentLayer移到BackdropContainer内部

### 滑块正向逻辑
- 0% → 白色层alpha=255 + 壁纸层opacity=1.0 → 壁纸遮住白色和桌面 → 看到壁纸
- 100% → 白色层alpha=0 + 壁纸层opacity=0 → 看到桌面玻璃效果
- 无壁纸时，0%看到白色默认背景（不是透明）
- 数值越大，白色层和壁纸层越透明，桌面越可见

### CrashReportService壁纸诊断
- 错误报告新增【自定义壁纸状态】段落
- 包含：壁纸路径、是否有效、壁纸层Opacity、白色覆盖层Alpha、玻璃桌面可见度
- MainWindow壁纸相关方法(ApplyWallpaper/Reset/滑块)实时更新诊断信息

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 代码审查与稳定性修复

### 调试模式修复
- 修复调试模式关闭后重启仍为开启状态的bug
- 原因：DebugMode_Checked/Unchecked只设置了值，未调用_settings.Save()保存到文件
- 添加_settings.Save()确保设置持久化
- 添加Logger.Instance.Enable()/Disable()实时启用/禁用日志
- 关闭调试模式时先记录日志再禁用

### 退出进程清理
- 修复退出程序后teacher_sim.exe仍在后台运行的问题
- ExitApplication()添加_teacherSimService.Stop()调用
- 退出前检查教师端模拟进程是否运行并停止

### 代码审查结果
- 总代码量：6338行（19个.cs + 2个.xaml）
- 70个Click事件全部有对应方法
- 12个页面控件XAML和CS引用完整
- 11个服务文件全部在csproj中编译
- 编译结果：0错误，0警告
- 15处CheckBox空引用保护
- 全局异常双保险：DispatcherUnhandledException + AppDomain.UnhandledException
- 日志覆盖：147次调用（ButtonClick 53 + Info 41 + Debug 14 + Error 8等）

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 最终审核与资源修复

### 代码最终审核
- 总代码量：6032行（C# + XAML），16个.cs文件 + 2个.xaml文件
- 70个Click事件全部有对应方法
- 12个页面控件XAML和CS引用完整
- 11个服务文件全部在csproj中编译
- 编译结果：0错误，0警告

### 修复ShowPage页面切换bug
- 修复case "screenshot"被case "teachersim"吞掉的问题
- 导致截图替换页面无法切换，代码无法访问

### Assets图片嵌入资源修复
- 将6个Assets文件从Content改为Resource，嵌入exe内部
- 修复发布版缺少Assets文件夹导致XamlParseException启动崩溃
- XAML图片引用改为pack URI格式：pack://application:,,,/学习不通;component/Assets/xxx
- 修复系统托盘图标从文件路径加载改为GetResourceStream从嵌入资源加载
- 修复StreamResourceInfo命名空间（System.Windows.Resources）
- exe体积：813KB → 1291KB（含图片资源）

### 隐藏所有滚动条
- 将所有ScrollViewer的VerticalScrollBarVisibility从Auto改为Hidden
- 涉及4个页面：UDP攻击、小小私聊、截图替换、教师端模拟

### 单文件打包
- 驱动JiYuTrainerDriver.sys和JiYuTrainerHooks.dll嵌入为EmbeddedResource
- 程序启动时自动释放到%TEMP%\学习不通\目录
- 文件已存在且大小相同时跳过释放
- 发布包仅需：学习不通.exe + Drivers\teacher_sim.exe

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 教师端模拟控制台修复与增强

### 教师端模拟控制台修复
- 修复控制台无反馈问题：teacher_sim.py的print()输出到stdout，之前只监控日志文件导致收不到输出
- 修复编码问题：teacher_sim.exe输出是GBK编码，改为Encoding.GetEncoding("GB2312")解码
- 修复Python stdout全缓冲问题：重新打包teacher_sim.exe，在文件开头添加sys.stdout.reconfigure(line_buffering=True)
- 添加OutputDataReceived事件异步接收stdout输出
- 添加ErrorDataReceived事件接收stderr输出
- 启动前验证teacher_sim.exe是否存在，不存在时显示错误提示
- 启动后等待2秒检查进程是否存活，启动失败显示退出码

### 教师端模拟功能增强
- 新增目标操作区域：目标IP输入框
- 新增快捷操作按钮：发送消息、黑屏锁定、解锁、关机、重启、屏幕预览、学生信息
- 关机/重启操作添加确认弹窗
- 发送消息自动填充命令到控制台输入框

### teacher_sim.py修改说明
- 仅添加3行：sys.stdout.reconfigure(line_buffering=True)和sys.stderr.reconfigure(line_buffering=True)
- 目的：确保重定向stdout时print()实时输出，不修改任何业务逻辑

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 教师端模拟与单文件打包

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

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 错误报告与调试模式

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

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09)

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
- 版本号: QD_V2.3_JiYuRebuild_CoUI-Glass
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

## 2026-09-13

### teacher_sim preview功能修复（多次迭代）

**问题**：学生端登录后preview要么一直持续抓取，要么完全收不到。

**根因分析**（通过真实抓包v6_login.pcapng对比）：
1. LPNT包第一个字段学生端当作subtype处理，只识别2和3，递增的policy_version被忽略
2. 学生端定期发TRMC心跳，教师端必须回复LPNT(enabled=1)+DMOC，否则学生端不发preview
3. TRMC回复的LPNT(enabled)会覆盖keep_alive的LPNT(disable)，协议层无法停止preview
4. request_preview触发的预览没有节流逻辑

**最终方案：应用层节流**
- TRMC恢复回复LPNT(enabled=1)+DMOC（和真实教师端一致）
- handle_tnal开头检查preview_saved[sip]，为True时丢弃帧
- 保存一张preview后自动设置preview_saved[sip]=True
- request_preview时重置preview_saved[sip]=False，允许保存下一张
- 学生端持续发小帧（320x240，约10KB，带宽可忽略），应用层只保存需要的图片

### 其他修复
- info.json顺序调整：设备IP → MAC → 系统 → 最后在线 → 设备信息 → 当前窗口名称 → 窗口列表 → 设备进程列表
- 主程序所有滚动条隐藏（ScrollViewer/ListBox/TextBox）
- 教师模拟页面TextTeacherSimLog滚动条隐藏
- LPNT包subtype固定为3（学生端只认subtype=2/3）
- 登录时lp2 enabled=1（学生端开始发preview）
- preview清晰度320x240
- 解锁时单播补发MESS防止组播丢失
- teacher_sim日志路径统一到exe目录/teacher_sim/
- preview图片保存到students/<IP>/screenshots/按时间戳命名
- 学生建档info.json（设备信息/进程列表/窗口列表）
- 主程序教师模拟页面：学生列表（IP+MAC，可滚动）、控制台上下布局（stdout+日志区）
- 黑屏窗口改回原项目行为（SW_HIDE隐藏）
- 窗口控制模块修复（HandleDllCallback薄转发、ManualTop/ManualFull、AllowGbTop被动模式）
- 广播窗口修复（移除每3.1秒缩放逻辑、补发hw:消息）




---

## 2026-09-19 版本更新

### ChatRoom 文件发送上限提升
- 文件发送上限从 50KB 提升到 **200KB**
- 块数上限 96 → 260，组装上限 80K → 300K 字符
- 帮助文档提示文字同步更新

### 主程序启动 ChatRoom 昵称覆盖修复
- 主程序启动 ChatRoom 时不再传 --nick 参数，避免覆盖用户在 ChatRoom 注册页设置的昵称

### 清理
- 删除临时修复脚本和旧测试文件夹
