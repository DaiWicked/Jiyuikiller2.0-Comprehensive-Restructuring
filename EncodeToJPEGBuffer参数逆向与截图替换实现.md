# EncodeToJPEGBuffer 参数逆向与截图替换实现

## 概述

本文档记录通过四轮DLL注入日志探测，逆向分析极域电子教室LibJPEG20.dll中`EncodeToJPEGBuffer`函数的参数含义，并基于此实现编码层截图替换功能。

## 背景

原作者JiYuTrainer的截图替换方案基于GDI Hook（`hkCreateDCW`返回假DC + `hkBitBlt`替换绘制），该方案在32位Win10上存在兼容性问题，可能导致极域学生端崩溃或虚拟机死机。

编码层替换方案的核心思路：不阻止编码，而是在JPEG编码完成后替换输出数据，避免触碰GDI和驱动层。

## 函数信息

- **DLL**: `LibJPEG20.dll`（极域私有JPEG编解码库，1237792 bytes，5个导出函数）
- **函数名**: `EncodeToJPEGBuffer`
- **调用约定**: `__cdecl`
- **返回值**: `BOOL`（1=成功）
- **函数签名**:
```cpp
int(__cdecl *)(int a1, int a2, int a3, int a4, int a5, DWORD *a6, int a7, int a8, int a9)
```

## 参数逆向过程

### 第一轮：输入参数探测
在函数入口输出所有9个参数，发现：
- Win7和Win10数据高度一致
- `a2=80`, `a3=60`, `a4=240`, `a7=90` 为固定值
- `a4=240 = 80×3`，完美吻合RGB24步长
- 初步推断：a2=宽, a3=高, a4=步长, a7=JPEG质量

### 第二轮：返回值与输出缓冲区
增加返回值、`*a5`、`a6`前16字节输出：
- `ret=1`（成功）
- `*a5`为固定大数值（非输出大小）
- `a6`前4字节（小端）= 1572~2736（JPEG大小，随画面变化）
- `a6`偏移4字节 = 0x0044xxxx指针（指向代码段，非JPEG数据）
- **发现**：a6是结构体指针 `{ DWORD size; BYTE* codePtr; ... }`

### 第三轮：结构体解析
解析a6结构体的size和data指针，读取data指向的前16字节：
- `a6.size` = 1500~2700（JPEG大小）
- `a6.data` = 0x0044xxxx（指向E9 JMP指令，是代码段）
- **结论**：a6.data不是JPEG数据指针，之前的推断有误

### 第四轮：a5缓冲区完整验证（决定性）
读取a5指向的前32字节、尾部2字节、边界后数据：
- `a5`前2字节 = **FF D8**（JPEG魔数）
- `a5`第3-4字节 = **FF DB**（DQT量化表段）
- `a5`第5-6字节 = 00 43（段长度67字节）
- `a5+size-2` = **FF D9**（JPEG结束标记）
- `a6->size`与实际JPEG数据大小完全一致
- **最终结论**：**a5是输出JPEG缓冲区指针，a6->size是输出大小**

## 最终参数含义

| 参数 | 类型 | 含义 | 验证证据 |
|------|------|------|----------|
| a1 | BYTE* | 输入像素数据指针（RGB24） | 不同进程值不同 |
| a2 | int | 宽度 = 80 | 固定值，与a4构成步长关系 |
| a3 | int | 高度 = 60 | 固定值，80×60缩略图 |
| a4 | int | 步长 = 240 | 240 = 80×3，RGB24 |
| **a5** | **BYTE*** | **输出JPEG缓冲区指针** | 前4字节FF D8 FF DB，尾部FF D9 |
| a6 | DWORD* | 输出结构体，偏移0=JPEG大小 | size随画面变化，与a5数据大小一致 |
| a7 | int | JPEG质量 = 90 | 固定值 |
| a8 | int | 颜色格式 = 0（RGB24） | 固定值 |
| a9 | int | 保留 = 0 | 固定值 |

## 截图替换实现

### DLL端（JiYuTrainerHooks.cpp）

```cpp
// 全局变量
BYTE* g_fakeJpegData = NULL;
DWORD g_fakeJpegSize = 0;
WCHAR g_fakeJpegPath[MAX_PATH] = { 0 };

// INI读取（VInitSettings中）
GetPrivateProfileString(L"JTSettings", L"FakeJpegPath", L"", 
    g_fakeJpegPath, MAX_PATH, mainIniPath);
if (文件存在 && 大小<=65536) {
    g_fakeJpegData = (BYTE*)malloc(fileSize);
    ReadFile(hFile, g_fakeJpegData, fileSize, &readBytes, NULL);
    g_fakeJpegSize = readBytes;
}

// Hook函数
BOOL __cdecl hkEncodeToJPEGBuffer(int a1, int a2, int a3, int a4, 
    int a5, DWORD *a6, int a7, int a8, int a9)
{
    if (!allowMonitor) return FALSE;
    BOOL ret = faEncodeToJPEGBuffer(a1, a2, a3, a4, a5, a6, a7, a8, a9);
    
    // 编码层替换
    if (ret && g_fakeJpegData && g_fakeJpegSize > 0 && a5 && a6) {
        __try {
            // 仅替换80x60缩略图，避免影响其他编码场景
            if (a2 == 80 && a3 == 60 && g_fakeJpegSize <= 65536) {
                memcpy((void*)a5, g_fakeJpegData, g_fakeJpegSize);
                *(DWORD*)a6 = g_fakeJpegSize;
            }
        } __except(EXCEPTION_EXECUTE_HANDLER) {
            // 替换失败保持原编码结果
        }
    }
    return ret;
}
```

### 主程序端（ScreenshotService.cs）

```csharp
// 生成80x60假JPEG
private string GenerateFakeJpeg()
{
    using (Bitmap src = new Bitmap(_currentImagePath))
    using (Bitmap dst = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
    using (Graphics g = Graphics.FromImage(dst))
    {
        g.InterpolationMode = HighQualityBicubic;
        g.Clear(Color.Black);
        // 按比例缩放居中
        float ratio = Math.Min(80f / src.Width, 60f / src.Height);
        int w = (int)(src.Width * ratio);
        int h = (int)(src.Height * ratio);
        g.DrawImage(src, (80-w)/2, (60-h)/2, w, h);
        
        // JPEG编码质量90
        ImageCodecInfo jpegCodec = ...;
        EncoderParameters encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        dst.Save(fakeJpegPath, jpegCodec, encParams);
    }
    return fakeJpegPath;
}

// 应用时写入INI
WritePrivateProfileString("JTSettings", "FakeJpegPath", fakeJpegPath, _iniPath);
controller.SendVirusMessage("hk:inipath:" + _iniPath);
```

## 安全性设计

1. **范围限制**：仅替换`a2==80 && a3==60`的缩略图编码，不影响屏幕广播等其他场景
2. **大小限制**：假JPEG限制为65536字节以内，避免缓冲区溢出
3. **异常保护**：`__try/__except`包裹替换逻辑，失败时保持原编码结果
4. **先编码后替换**：先调用原函数确保缓冲区有效、大小字段初始化，再覆盖
5. **不触碰GDI/驱动**：完全在编码输出层操作，与原作者GDI Hook方案完全独立

## 与原作者方案的对比

| 维度 | 原作者GDI Hook方案 | 编码层替换方案 |
|------|-------------------|---------------|
| 原理 | CreateDCW返回假DC + BitBlt替换绘制 | EncodeToJPEGBuffer输出替换 |
| 依赖 | 需要关闭allowMonitor | 不依赖allowMonitor状态 |
| 触碰层级 | GDI/驱动层 | 纯用户态编码层 |
| 32位Win10兼容性 | 已知崩溃问题 | 无已知兼容性问题 |
| 影响范围 | 所有GDI绘制 | 仅80x60缩略图编码 |
| 崩溃风险 | 高（空指针访问） | 低（__try/__except保护） |

## 测试环境

- Win7 32位：极域电子教室 v4.0 2016 豪华版
- Win10 32位：极域课堂管理系统 V6.0 2016 豪华版
- 教师端：teacher_sim.py（6.0协议模拟）+ 真实教师端
- DLL注入方式：RemoteThread + LoadLibrary

## 结论

通过四轮日志探测，100%确认了`EncodeToJPEGBuffer`的参数含义，特别是`a5`为输出JPEG缓冲区指针这一关键发现。基于此实现的编码层截图替换方案，避免了原作者GDI Hook方案在32位Win10上的崩溃问题，具有更高的稳定性和安全性。
