using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WLMouseTray;

/// <summary>
/// WLmouse 私有 HID 协议（从官方 WL MOUSE HUB 1.0.39 的 app.asar 中分析得到）。
///
/// 官方的逻辑跑在 WebHID 上，`sendFeatureReport(0, payload)` 会在 USB 缓冲区最前面
/// 再补一个 report ID 字节。所以官方代码里的 payload 下标 n[i] 落到实际 65 字节
/// 缓冲区时是 [i + 1]：
///
///   实际缓冲区（65 字节，Feature Report，Report ID = 0）：
///     [0] report id (0)
///     [1] 回包中的状态字节
///     [2] 保留
///     [3] 目标设备 id（鼠标 = 2，接收器/有线设备 = 0）   ← 官方 n[2]
///     [4] 数据长度                                      ← 官方 n[3]
///     [5] 保留                                          ← 官方 n[4]
///     [6] 命令码                                        ← 官方 n[5]
///     [7..] 数据                                        ← 官方 n[6..]
///
/// 回包状态（原始缓冲 [1]）：
///   0xA1 = 成功，数据在后续字节
///   0xA3 = 设备忙，需要重发
///   0xA0 = 暂无响应（设备休眠 / 无线链路未上线），需要轮询
///
/// 实测电量回包：00 A1 00 02 02 00 83 00 3B
///   → [7] = 充电标志，[8] = 电量百分比（0x3B = 59%）
/// </summary>
public sealed class WlMouseDevice : IDisposable
{
    public const ushort DefaultVid = 0x36A7;

    private readonly SafeFileHandle _handle;
    private readonly List<string> _log;

    public ushort Vid { get; }
    public ushort Pid { get; }
    public string Path { get; }
    public byte[] LastResponse { get; private set; } = new byte[65];

    private WlMouseDevice(SafeFileHandle handle, ushort vid, ushort pid, string path, List<string> log)
    {
        _handle = handle;
        Vid = vid;
        Pid = pid;
        Path = path;
        _log = log;
    }

    /// <summary>找到 WLmouse 的厂商配置接口（Feature Report 长度 65）。</summary>
    public static WlMouseDevice? TryOpen(List<string> log, ushort vid = DefaultVid)
    {
        foreach (var itf in Hid.Enumerate())
        {
            if (itf.Vid != vid) continue;
            if (itf.FeatureReportLength != 65) continue;
            if (itf.UsagePage is not (0xFFFF or 0xFFA0 or 0xFF00)) continue;

            var handle = Hid.Open(itf.Path);
            if (handle.IsInvalid)
            {
                log.Add($"打开失败 0x{Marshal_GetLastError():X2}  {ShortPath(itf.Path)}");
                handle.Dispose();
                continue;
            }

            log.Add($"已连接 {(itf.Path.Split('#').Length > 2 ? itf.Path.Split('#')[1] : itf.Path)}");
            return new WlMouseDevice(handle, itf.Vid, itf.Pid, itf.Path, log);
        }

        return null;
    }

    private static int Marshal_GetLastError() => Marshal.GetLastWin32Error();

    private static string ShortPath(string path)
    {
        var parts = path.Split('#');
        return parts.Length > 1 ? parts[1] : path;
    }

    /// <summary>
    /// 收发一条命令，重试策略照搬官方 retrySetGet：
    /// 成功(0xA1)直接返回；无响应(&lt;0xA1)时轮询 30 次等待；忙(&gt;0xA1)时重发。
    /// </summary>
    /// <param name="page">
    /// 命令页。0 = 设备页（电量、固件、休眠、去抖），1 = 配置页（DPI、回报率、
    /// 抬升高度等），3 = 按键页。官方 WebHID 的 n[4]，落在缓冲区 [5]。
    /// </param>
    /// <param name="budgetMs">
    /// 单条命令的总时间预算。鼠标对不认识的命令码会一直回 0xA0（pending），
    /// 而官方那种「5 轮 × 6 次 × 30 轮轮询」在真机上要 27 秒才能放弃，
    /// 一次配置刷新有十几条命令，加起来就是几分钟，界面会像卡死。
    /// </param>
    public bool Transfer(byte target, byte length, byte command, byte[]? payload,
        out byte[] raw, int delayMs = 30, byte page = 0, int budgetMs = 1200)
    {
        var request = new byte[65];
        request[0] = 0;        // report id（与官方 WebHID 自带的前缀一致）
        request[3] = target;   // ← 官方 payload 的 n[2]
        request[4] = length;   // ← n[3]
        request[5] = page;     // ← n[4]
        request[6] = command;  // ← n[5]
        if (payload != null)
        {
            Array.Copy(payload, 0, request, 7, Math.Min(payload.Length, request.Length - 7));
        }

        raw = new byte[65];
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);

        for (int round = 0; round < 5; round++)
        {
            if (DateTime.UtcNow >= deadline) break;

            if (!Hid.SetFeature(_handle, request))
            {
                _log.Add($"SetFeature 失败 err={Marshal.GetLastWin32Error()}");
                return false;
            }

            Thread.Sleep(delayMs);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (DateTime.UtcNow >= deadline) return false;

                Array.Clear(raw);
                if (!Hid.GetFeature(_handle, raw))
                {
                    _log.Add($"GetFeature 失败 err={Marshal.GetLastWin32Error()}");
                    return false;
                }

                LastResponse = (byte[])raw.Clone();
                byte status = raw[1];

                if (status is 0xA1 or 0x02) return true;   // 成功
                if (status == 0xA2)                        // 设备明确说「不支持这条命令」
                {
                    _log.Add($"页 0x{page:X2} 命令 0x{command:X2} 不受支持");
                    return false;
                }

                if (status > 0xA1) break;                  // 忙 -> 重发
                if (status == 0) Thread.Sleep(delayMs);    // 空 -> 稍等再重发

                // 无响应 (0xA0)：继续轮询，直到预算用完
                for (int poll = 0; poll < 30 && DateTime.UtcNow < deadline; poll++)
                {
                    Thread.Sleep(delayMs);
                    Array.Clear(raw);
                    if (!Hid.GetFeature(_handle, raw)) return false;
                    LastResponse = (byte[])raw.Clone();
                    if (raw[1] is 0xA1 or 0x02) return true;
                    if (raw[1] is 0xA2) return false;
                    if (raw[1] > 0xA1) break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 发一条带页号 / 参数的命令，返回去掉 7 字节回包头之后的 payload。
    ///
    /// 注意 <paramref name="length"/> 是**请求里的数据长度**，和 <paramref name="args"/>
    /// 的个数并不总相等：读命令（电量、固件、DPI 表）由官方规定固定长度而参数为空，
    /// 写命令则是 length == args.Length。
    ///
    /// 回包：<c>[1]</c>=状态 <c>[4]</c>=长度 <c>[5]</c>=页 <c>[6]</c>=命令 <c>[7..]</c>=数据。
    /// </summary>
    public bool Command(byte target, byte page, byte command, byte length,
        byte[]? args, out byte[] payload, int delayMs = 30, int budgetMs = 1200)
    {
        payload = Array.Empty<byte>();

        if (!Transfer(target, length, command, args, out byte[] raw, delayMs, page, budgetMs))
            return false;

        int n = Math.Min(raw[4], raw.Length - 7);
        if (n <= 0) return true;

        payload = new byte[n];
        Array.Copy(raw, 7, payload, 0, n);
        return true;
    }

    /// <summary>读取电量。返回 (是否充电, 电量百分比)。</summary>
    public (bool? charging, int? percent, bool ok) ReadBattery()
    {
        // 官方实现：buf[2]=2, buf[3]=2, buf[5]=0x83
        bool ok = Transfer(2, 2, 0x83, null, out byte[] raw);
        if (!ok) return (null, null, false);

        (bool? charging, int? percent)? parsed = ParseBattery(raw);
        return parsed is { } p ? (p.charging, p.percent, true) : (null, null, false);
    }

    /// <summary>
    /// 解析电量回包。官方代码（WebHID，可能不含 report id 字节）有三套偏移，
    /// 这里全部试一遍，取“充电标志 0/1 + 电量 1..100”的合理组合。
    /// </summary>
    private static (bool?, int?)? ParseBattery(byte[] raw)
    {
        int[][] candidates = { new[] { 7, 8 }, new[] { 8, 9 }, new[] { 6, 7 } };
        foreach (int[] c in candidates)
        {
            int flag = raw[c[0]];
            int percent = raw[c[1]];
            if (flag <= 1 && percent is > 0 and <= 100)
            {
                return (flag == 1, percent);
            }
        }

        return null;
    }

    /// <summary>鼠标固件版本，失败返回 null。</summary>
    public string? ReadFirmwareVersion()
    {
        if (!Transfer(2, 16, 0x81, null, out byte[] raw)) return null;
        return ParseVersion(raw, 0x81);
    }

    /// <summary>接收器固件版本，失败返回 null。</summary>
    public string? ReadReceiverFirmwareVersion()
    {
        if (!Transfer(0, 16, 0x81, null, out byte[] raw)) return null;
        return ParseVersion(raw, 0x81);
    }

    private static string? ParseVersion(byte[] raw, byte cmd)
    {
        // 官方：回包中 cmd 标记在第 5 或第 6 字节，版本号紧随其后 4 个字节
        for (int i = 4; i <= 7; i++)
        {
            if (raw[i] != cmd) continue;
            if (i + 4 >= raw.Length) continue;
            string version = $"{raw[i + 1]}.{raw[i + 2]}.{raw[i + 3]}.{raw[i + 4]}";
            if (version != "0.0.0.0") return version;
        }

        return null;
    }

    public static string Hex(byte[] data, int count = 16)
    {
        count = Math.Min(count, data.Length);
        return string.Join(' ', data.Take(count).Select(b => b.ToString("X2")));
    }

    public void Dispose() => _handle.Dispose();
}
