# WL Mouse 电量托盘（WLMouseTray）

在 Windows 任务栏通知区显示 WLmouse 鼠标电量的小工具，替代笨重的官方 WL MOUSE HUB。

## 当前状态

| 功能 | 状态 |
| --- | --- |
| 托盘图标显示电量百分比 | ✅ 已实现 |
| 悬停查看电量/充电状态 | ✅ 已实现 |
| 充电中蓝字 + 闪电标记 | ✅ 已实现 |
| 电量分档变色（绿 >50 / 黄 20-50 / 红 <20） | ✅ 已实现 |
| 右键：刷新 / 开机自启 / 退出 | ✅ 已实现 |
| 详情窗口（固件版本、原始回包、调试日志） | ✅ 已实现 |
| **真正读到电量数值** | ✅ 已实现（`00 A1 00 02 02 00 83 00 5A` → 90%） |
| DPI / 回报率 / 抬升高度等配置读写 | ✅ 已实现（`MouseConfig.cs`，见下） |

## 这是怎么做到的

官方 WL MOUSE HUB 是 Electron 应用，HID 通信逻辑在其 `resources/app.asar`
里的 WebHID 代码中。分析后得到 WLmouse 私有协议：

- 通过**厂商配置接口**（`MI_02`，UsagePage `0xFFFF`，Feature Report 65 字节）收发命令
- 帧格式：`[2]=目标设备(鼠标=2, 接收器=0)`、`[3]=长度`、`[5]=命令码`
- 电量查询：`buf[2]=2, buf[3]=2, buf[5]=0x83`，成功回包状态 `0xA1`，
  数据为 `[充电标志, 电量百分比]`；`0xA3`=忙、`0xA0`=无响应

你的接收器为 `WLMOUSE MINI PRO 8K RECEIVER`（VID `36A7` / PID `A868`），
对应官方型号表里的 `WL_MINI_PRO`（8K 接收器 `A868`，有线鼠标 `A869`，1K 接收器 `A882`）。
它**不**暴露标准 HID 电量页面，所以只能用厂商私有命令。

## 曾经遇到的：设备不回应电量查询

后来确认是**鼠标本体处于关机或深层休眠**——接收器插着但无线链路没上线。
鼠标唤醒后读数正常，上面的 ✅ 就是这样验证的。

当时的现象：
- 能成功枚举接口、打开设备、写入 Feature Report
- 但每次读回都是 `A0`（无响应）或 `A3`（忙），拿不到 `A1` + 数据

排查方法：

1. **打开鼠标电源并动一下鼠标**，再右键托盘点「刷新」
2. 鼠标改用**有线直连**（PID 会变成 `A869`），此时可确认是否无线链路问题
3. 详情窗口里的「调试日志 / 最近回包」会显示每次的原始回包，便于判断
4. 若官方 WL MOUSE HUB 此时也读不到电量，即可确认是硬件连线问题而非程序问题

## 配置页协议（DPI / 回报率等）

电量用的是「设备页」，配置用的是「配置页」，帧结构相同，只有页号不同：

```
缓冲区[3] = 目标（鼠标 2 / 接收器 0）
缓冲区[4] = 长度
缓冲区[5] = 页号   ← 0 = 设备页，1 = 配置页，3 = 按键页
缓冲区[6] = 命令码
缓冲区[7..] = 参数
```

`MouseConfig.cs` 封装了读写的全部细节。几个要点：

- **所有配置页命令的第一个参数都是「当前生效的板载配置档」**，由设备页 `0x85` 读出。
  写到别的档位会被静默忽略，所以每次写之前都要重新读一次。
- 写操作**一律先写后读校验**。鼠标对越界值是静默拒绝的，不看回包就当成功会骗自己。
- DPI 表整张一次写回：`[档数, xHi xLo yHi yLo, ...]`，共 6 档。
- 抬升高度编码：`0x87` = 低、`0x01` = 中、`0x02` = 高；带 `0x80` 位的是 0.1 mm 精度的值。
- 回报率编码：`0x08`=125、`0x04`=250、`0x02`=500、`0x01`/`0x10`=1000、
  `0x20`=2000、`0x40`=4000、`0x80`=8000。

`Probe` 可以直接改值来验证：

```bash
cd WLMouseTray/Probe
dotnet run -- set polling 4000
dotnet run -- set dpi 1 1600
dotnet run -- set lod High
dotnet run -- set snap on
```

协议细节参考 [OpenMouse-Project/mouse-protocol](https://github.com/OpenMouse-Project/mouse-protocol)
里对 WLmouse 的 compx 逆向。

## 目录结构

```
WLMouseTray/
├── WLMouseTray.csproj     主程序（WinForms 托盘，net9.0-windows）
├── Program.cs             托盘图标 / 菜单 / 轮询 / 详情窗口
├── Hid.cs                 HID 枚举与 Feature Report 收发（P/Invoke hid.dll）
├── WlMouse.cs             WLmouse 私有协议封装（电量 / 固件 / 通用命令）
├── MouseConfig.cs         配置页读写（DPI、回报率、抬升高度、去抖、休眠…）
├── app.ico                程序图标（脚本生成的电池图标）
├── Probe/                 命令行诊断工具（dotnet run 可看原始回包）
└── dist/WLMouseTray.exe   发布产物（单文件，约 200 KB，需 .NET 9 桌面运行时）
```

## 构建与运行

```bash
cd WLMouseTray
dotnet build
dotnet run
```

发布单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

诊断协议（输出所有 HID 接口和每次原始回包）：

```bash
cd WLMouseTray/Probe
dotnet run
```
