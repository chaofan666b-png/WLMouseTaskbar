# WL Mouse 任务栏电量（WLMouseTaskbar）

把 WLmouse 鼠标电量**直接显示在 Windows 11 任务栏上**，外观与
[FluentFlyout](https://github.com/unchihugo/FluentFlyout) 的 Taskbar Widget 同款：
32 px 圆角卡片、顶部高光、左边色块里放电池图形、右边两行（电量 + 设备名）。

设置界面用 WinUI / Windows 11「设置」那套视觉语言：左侧导航 + 右侧卡片，
深浅色跟随系统。

```
┌──────────────────────────────────────────────────────────┐
│  ...  │ [🔋 90%] │  ⌃  🔊  📶  🔋  14:29   │
│         WL Mouse                                          │
└──────────────────────────────────────────────────────────┘
         ↑ 本程序
```

## 一眼看懂

| 功能 | 位置 |
| --- | --- |
| 电量百分比 + 电池液面 + 充电闪电 | 任务栏，可贴在通知区域左边 / 居中 / 靠近开始按钮 |
| 悬停提示（电量 / 更新时间 / 操作提示） | 鼠标移到控件上 |
| 左键单击 | 打开设置窗口 |
| 右键 | DPI 档位、回报率、刷新、设置、退出 |
| 通知区托盘图标 | 可在设置里单独开关 |
| 开机自启 | 写 `HKCU\...\Run`，无需管理员 |

电量分档配色：≥50% 绿、20–50% 橙、<20% 红、充电中 蓝 + 闪电。
配色跟随系统深浅色主题自动切换。

## 设备设置（DPI / 回报率等）

这些是**写进鼠标板载内存**的设置，改完立刻生效，拔掉接收器也保留。全部来自
逆向出来的配置页协议：

| 项目 | 可选值 | 协议 |
| --- | --- | --- |
| DPI 档位 | 50–30000，每档 50 | 配置页 `0x81` / `0x01`，当前档 `0x82` / `0x02` |
| 回报率 | 125 / 250 / 500 / 1000 / 2000 / 4000 / 8000 Hz | 配置页 `0x80` / `0x00` |
| 抬升高度 | 低 / 中 / 高 | 配置页 `0x88` / `0x08` |
| 角度吸附 | 开 / 关 | 配置页 `0x84` / `0x04` |
| Motion Sync | 开 / 关 | 配置页 `0x89` / `0x09` |
| 去抖时间 | 0–15 ms | 设备页 `0x88` / `0x08` |
| 休眠时间 | 30 s – 30 min | 设备页 `0x87` / `0x07` |

写入后一律**读回校验**——鼠标会静默拒绝越界值，只有读回一致才算成功。
配置页的命令必须带上「当前生效的板载配置档」（设备页 `0x85` 读出），
写到别的档位会被忽略。

设置项是按设备能力**动态生成**的：读不到某个字段就不显示对应的控件，
不会出现「有这个开关但鼠标不认」的假控件。

## 做了什么（开发记录）

### 起点

原始文件夹里只有一个 122 MB 的 `WL_MOUSE_HUB-1.0.39-setup.exe`——官方驱动，
Electron 打包，开机自启、常驻几百 MB，只为看一个电量数字。目标是做一个常驻小工具，
**不打开任何窗口就能在任务栏上看到电量**。

### 第一步：摸清电量协议

官方驱动是 Electron 应用，HID 通信逻辑写在 `resources/app.asar` 里的 WebHID 代码中。
拆出来得到 WLmouse 私有协议（完整细节见下面的「电量协议」一节）。

同时写了 `../WLMouseTray/Probe/` 命令行诊断工具，打印全部 HID 接口和每次原始回包，
用它确认了接收器（VID `36A7` / PID `A868`）确实会回
`00 A1 00 02 02 00 83 00 5A`。

这一阶段也产出了一版 WinForms 托盘图标程序（`../WLMouseTray`）：能显示数字、
充电闪电、分档配色。但托盘图标只有 16×16，百分比数字挤得几乎看不清，
才有了任务栏挂件这一版。`Hid.cs` / `WlMouse.cs` 通过 `<Compile Include>`
链接到本项目复用，没有复制代码。

### 第二步：为什么最终不是 UWP

最初的设想是用微软 UWP 框架。结论是不行，属于平台硬限制：

- UWP 跑在 AppContainer 沙箱里，**没有 `Shell_NotifyIcon`**
- 沙箱进程**不能 `SetParent` 到 `Shell_TrayWnd`**

想在任务栏上"长"一个控件，必须是完整信任的桌面进程。
所以这里用 WPF（`net9.0-windows`）：Win32 负责把窗口嵌进任务栏，WPF 负责绘制。

### 第三步：照着 FluentFlyout 实现任务栏挂件

参考 [FluentFlyout](https://github.com/unchihugo/FluentFlyout) 的 Taskbar Widget
（它用同样方式在任务栏显示歌词），在通知区域左侧画一个圆角胶囊：
电池图标 + 百分比 + 充电闪电。要求：单击/右键/悬停都工作、
不抢焦点、透明区域不拦截任务栏本身的点击、explorer 重启后能自动恢复。
具体机制见下一节。

### 第四步：调试「窗口存在但看不见」

第一版写完，诊断结果显示一切正常——`WS_CHILD`、`WS_EX_LAYERED`、
`WS_EX_NOACTIVATE` 都设置成功，`GetParent` 是 `Shell_TrayWnd`，
窗口矩形位置正确，`WindowFromPoint` 也能命中——**但屏幕上什么都没有**。

排查路径：

1. 怀疑背景太透明 → 改成完全不透明，仍然不显示。
2. 怀疑 DPI 算错 → 打开 `WLMOUSE_DIAG=1` 打印坐标，数值自洽。
3. 用 `CopyFromScreen`（脚本内先调 `SetProcessDPIAware()`）逐块截图取证，
   确认目标区域确实是空的，排除"只是截不到"。
4. 反过来读 FluentFlyout 的源码：它的窗口**铺满整条任务栏**，再用 region 裁剪。

结论：小尺寸的 `WS_EX_LAYERED` + `WS_CHILD` 窗口，合成表面会被系统丢弃。
改成「窗口铺满整条任务栏 + `SetWindowRgn` 只保留胶囊」后立刻正常。

修好之后又暴露一个衔接问题：胶囊和 FluentFlyout 的媒体控件重叠。
一开始以为是自己画多了，**完全退出本程序后那圈光晕仍在**，才确认那是
FluentFlyout 自己的挂件。于是加了「显示位置」+「与锚点的距离」两个设置项。

### 最终验证

以下全部是 DPI 感知脚本实测，不是"看起来对了"：

| 项目 | 结果 |
| --- | --- |
| 父窗口 | `GetParent(widget) == Shell_TrayWnd` ✓ |
| 几何 | 任务栏 2560×60 @125%，胶囊 68×32，容器坐标 `1850,14` ✓ |
| 命中测试 | 胶囊区域 → 本窗口；空白处/开始按钮/通知区/时钟 → `Shell_TrayWnd` ✓ |
| 真实左键点击 | 打开设置窗口 ✓ |
| 真实右键点击 | 菜单弹出并显示「电量：90%」✓ |
| 电量读取 | `00 A1 00 02 02 00 83 00 5A` = 90% ✓ |
| 固件版本 | 鼠标 1.0.2.13 / 接收器 1.0.2.7 ✓ |

发布产物 `dist/WLMouseTaskbar.exe` 约 815 KB（单文件，需 .NET 9 桌面运行时）。

### 规模

约 2100 行代码，其中 `Hid.cs` + `WlMouse.cs` 共 422 行是复用的；
核心在 `TaskbarWidgetWindow.xaml.cs`（558 行），负责窗口嵌入、定位、裁剪和绘制。

## 它是怎么贴到任务栏上的

和 FluentFlyout 同一套做法，全部靠 Win32（见 `Native.cs` / `TaskbarWidgetWindow.xaml.cs`）：

1. 创建一个 `WindowStyle=None`、`AllowsTransparency=True`、`SizeToContent=WidthAndHeight`
   的 WPF 窗口。
2. 窗口风格从 `WS_POPUP` 改成 `WS_CHILD`，`SetParent` 到 `Shell_TrayWnd`。
   — Windows 8 起允许 layered 子窗口，所以透明 + 子窗口可以共存。
3. `ScreenToClient` 把任务栏客户区原点换算出来，再用 `SetWindowPos` 以父窗口坐标定位。
4. 用 `SetWindowRgn` 裁成圆角胶囊，透明区域不拦截任务栏的点击。
5. `WS_EX_NOACTIVATE` + 拦截 `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`，点控件不抢焦点。
6. 每 1.2s 校一次位置，处理任务栏重建、DPI 变化、多显示器切换。
7. 注册 `TaskbarCreated` 消息，`explorer.exe` 重启后自动重新挂载。

> 严格意义上的 UWP 沙箱**没有** `Shell_NotifyIcon`、也无法 `SetParent` 到任务栏，
> 这是平台限制。要"任务栏上直接看"，必须是完整信任的桌面进程，所以这里用 WPF。

## 电量协议

直接复用 `../WLMouseTray` 里已实测可用的实现（`Hid.cs` / `WlMouse.cs`），
通过 `WLMouseTaskbar.csproj` 的 `<Compile Include>` 链接进来，不复制代码。

- 厂商接口：`MI_02`，UsagePage `0xFFFF`，65 字节 Feature Report
- 电量查询：`buf[3]=2, buf[4]=2, buf[6]=0x83`
- 成功回包：`00 A1 00 02 02 00 83 00 5A` → `[7]`=充电标志，`[8]`=电量（0x5A = 90）
- 固件查询：`0x81`

## 运行

```bash
cd WLMouseTaskbar
dotnet build
dotnet run
```

发布单文件（需 .NET 9 桌面运行时）：

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

开发辅助参数：

```bash
# 把任务栏控件渲染成 PNG（不挂任务栏），用于确认外观
WLMouseTaskbar.exe --snapshot out.png
WLMouseTaskbar.exe --snapshot out.png --light

# 把设置窗口整页渲染成 PNG（不依赖屏幕截图）
WLMouseTaskbar.exe --shot-ui page.png --page overview
WLMouseTaskbar.exe --shot-ui page.png --page device --light

# 直接打开设置窗口
WLMouseTaskbar.exe --settings
```

> 截图为什么不用屏幕抓取：这台机器上别的置顶窗口会盖住被截窗口，
> `CopyFromScreen` 和 `PrintWindow` 都拍不到。`--shot-ui` 让 WPF 自己
> 用 `RenderTargetBitmap` 渲染，结果稳定。

## 设置

配置文件：`%APPDATA%\WLMouseTaskbar\settings.json`

| 字段 | 含义 | 默认 |
| --- | --- | --- |
| `PollSeconds` | 电量刷新间隔（秒） | 60 |
| `ShowWidget` | 任务栏控件开关 | true |
| `ShowTrayIcon` | 通知区图标开关 | true |
| `WidgetPosition` | 0 = 紧贴通知区域，1 = 居中，2 = 靠近开始按钮 | 0 |
| `WidgetOffset` | 与锚点的距离（DIP），用于和别的挂件错开 | 6 |
| `AvoidOtherWidgets` | 自动避让任务栏上别的挂件 | true |
| `HideWhenDisconnected` | 鼠标未连接时隐藏控件 | false |

`AvoidOtherWidgets` 打开时不需要再手工调 `WidgetOffset`：程序会枚举任务栏的子窗口，
读出它们**真实的可见区域**（`GetWindowRgn` + `GetRgnBox`），撞上就往左让。
FluentFlyout、别的电量条这类挂件都是「窗口铺满整条任务栏 + region 裁剪」，
光看窗口矩形是整条任务栏，只有 region 才是实际占位。

出错时写到 `%APPDATA%\WLMouseTaskbar\error.log`。
设置环境变量 `WLMOUSE_DIAG=1` 会额外把每次定位的 DPI / 尺寸写到 `%TEMP%\wlmouse-diag.txt`。

## 目录结构

```
WLMouseTaskbar/
├── WLMouseTaskbar.csproj    WPF 单体程序（net9.0-windows）
├── App.xaml(.cs)            应用入口、生命周期、单实例、截图模式
├── MainWindow.*             设置 / 详情窗口（Fluent 布局，左侧导航 + 卡片）
├── Controls/Ui.cs           用代码搭 Fluent 设置页的小工具
├── Theme/Fluent.xaml        Fluent 控件样式（开关、滑杆、下拉、卡片、导航项）
├── Theme/Light.xaml         浅色颜色令牌
├── Theme/Dark.xaml          深色颜色令牌
├── Theme/ThemeManager.cs    按系统深浅色切换令牌
├── TaskbarWidgetWindow.*    任务栏控件（窗口嵌入 + 电量绘制 + 右键菜单）
├── TrayIcon.cs              通知区图标（WinForms NotifyIcon）
├── BatteryService.cs        后台采样、配置读写、失败指数退避、线程安全
├── AppSettings.cs           设置持久化 + 开机自启
├── Native.cs                任务栏嵌入、region 查询用到的 Win32 声明
├── app.manifest             PerMonitorV2 DPI
└── dist/WLMouseTaskbar.exe  发布产物（约 870 KB）
```

共享的协议源码在 `../WLMouseTray/`：`Hid.cs`、`WlMouse.cs`、`MouseConfig.cs`，
通过 `<Compile Include>` 链接进来，两个程序用同一份实现。

## 关于第三方 UI 库

试过 WPF-UI（wpfui），但它的 `FluentWindow` + Mica 在这台机器上渲染成空白，
而且本程序要嵌进任务栏，依赖越少越稳。所以 `Theme/` 下的样式是手写的——
只覆盖真正用到的控件，约 400 行 XAML。

## 已知限制

- **只支持横排任务栏**：竖排（任务栏在左右两侧）时控件会自动隐藏。
- **只挂主任务栏**：副屏任务栏上不显示。
- **不要和 WLMouseTray 同时运行**：两者都会打开同一个 HID 接口并轮询，
  会互相干扰导致读数变慢。用这个就够了（它包含托盘图标和详情窗口）。
- **和别的任务栏挂件会重叠**：例如 FluentFlyout 的媒体控件也贴在通知区域左侧。
  在设置里把「与锚点的距离」调大即可错开（本机实测调成 230 正好并排）。

## 踩过的坑（改动时注意）

1. **小尺寸的 `WS_EX_LAYERED` + `WS_CHILD` 窗口不会绘制**。窗口必须铺满整条任务栏，
   再用 `SetWindowRgn` 只保留可见的那块。做成"刚好控件大小"会整个不显示。
2. `GetWindowRgn` 对分层窗口返回 0（空区域）是正常现象，不代表 region 没生效；
   用 `WindowFromPoint` 验证命中测试才准。
3. 用 PowerShell 存 `settings.json` 会带 UTF-8 BOM，`System.Text.Json` 默认解析失败
   会把设置重置成默认值。读取端已经处理 BOM。
4. 截图/测量脚本必须 `SetProcessDPIAware()`，否则拿到的是 125% 缩放下的虚拟坐标，
   会和程序算出的物理坐标对不上。
5. **`WindowChrome` 不要设 `GlassFrameThickness="0"`**。设了之后窗口的 DWM 合成表面
   会失效：布局照常、`IsWindowVisible` 也是 true、`WindowFromPoint` 也能命中，
   但屏幕上什么都不画，截屏和 `PrintWindow` 都是空白。只给 `CaptionHeight` 和
   `ResizeBorderThickness` 就正常。排查时用「把根元素背景改成纯红」二分最快。
6. **`CreateRectRgn` / `GetRgnBox` 在 gdi32，不在 user32**。写成 `user32.dll` 会抛
   `EntryPointNotFoundException`，而且只在真正调用的那一刻才炸。
7. **`--shot-ui` 之类的截图模式绝不能写回设置**。WPF 在控件挂到可视化树时会补发一次
   `Checked` / `ValueChanged`，如果处理器直接 `_settings.Save()`，用默认设置构造的
   临时窗口就会把用户的配置覆盖成默认值。本程序用 `_snapshotState is not null` 拦住。
8. **`Transfer` 要有时间预算**。鼠标对不认识的命令码会一直回 `0xA0`（pending），
   照搬官方那种「5 轮 × 6 次 × 30 次轮询」要 27 秒才放弃，一次配置刷新十几条命令
   就是几分钟，界面看起来像卡死。现在每条命令 1.2 秒封顶。
