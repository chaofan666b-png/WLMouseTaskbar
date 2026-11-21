namespace WLMouseTray;

/// <summary>回报率选项。编码值来自官方驱动，0x01 和 0x10 在部分型号上都表示 1000 Hz。</summary>
public static class PollingRates
{
    public static readonly int[] All = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    /// <summary>
    /// 型号支持的最高回报率。只有 1K 接收器（0xA882）限死 1000 Hz，其余型号都按
    /// 8000 放开——具体能跑多高由鼠标自己决定，不支持的档位写入后读回校验会失败。
    ///
    /// 注意这里**不能**用「当前回报率」去推断上限：鼠标当前跑 2000 Hz 不代表它
    /// 只支持 2000 Hz，那样会把 4K / 8K 选项从菜单里整个藏掉。
    /// </summary>
    public static int MaxFor(ushort productId) => productId == 0xA882 ? 1000 : 8000;

    public static byte Encode(int hz) => hz switch
    {
        125 => 0x08,
        250 => 0x04,
        500 => 0x02,
        1000 => 0x01,
        2000 => 0x20,
        4000 => 0x40,
        8000 => 0x80,
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "不支持的回报率"),
    };

    public static int? Decode(byte value) => value switch
    {
        0x08 => 125,
        0x04 => 250,
        0x02 => 500,
        0x01 or 0x10 => 1000,
        0x20 => 2000,
        0x40 => 4000,
        0x80 => 8000,
        _ => null,
    };
}

/// <summary>抬升高度三档。</summary>
public enum LiftOffDistance
{
    Low,
    Medium,
    High,
}

/// <summary>从鼠标读回的一整套配置。某个字段为 null 表示该型号不支持或读取失败。</summary>
public sealed record MouseConfig
{
    public int Profile { get; init; } = 1;

    /// <summary>接收器 / 鼠标的 PID，用来判断型号能力（例如最高回报率）。</summary>
    public ushort ProductId { get; init; }

    /// <summary>DPI 档位（只取 X 轴；未开独立轴时 X 与 Y 相同）。</summary>
    public int[] DpiStages { get; init; } = Array.Empty<int>();

    /// <summary>当前生效的档位，0 起。</summary>
    public int ActiveStage { get; init; }

    public int? PollingRateHz { get; init; }
    public LiftOffDistance? LiftOff { get; init; }
    public int? DebounceMs { get; init; }
    public int? SleepSeconds { get; init; }
    public bool? AngleSnapping { get; init; }
    public bool? MotionSync { get; init; }

    public static MouseConfig Empty { get; } = new();

    public int CurrentDpi =>
        DpiStages.Length == 0 ? 0 : DpiStages[Math.Clamp(ActiveStage, 0, DpiStages.Length - 1)];
}

/// <summary>
/// 鼠标配置页的读写。协议来自 OpenMouse-Project 对 WLmouse 的 compx 逆向，
/// 页号/命令码/编码规则与官方驱动一致：
///
///   设备页（0）：电量 0x83、固件 0x81、配置档 0x85、休眠 0x87/0x07、去抖 0x88/0x08
///   配置页（1）：回报率 0x80/0x00、DPI 表 0x81/0x01、当前档 0x82/0x02、
///               角度吸附 0x84/0x04、抬升高度 0x88/0x08、Motion Sync 0x89/0x09
///
/// 所有配置页命令的第一个参数都是「当前生效的板载配置档」——写到别的档位会被
/// 静默忽略，所以每次写之前都要重新读一次 0x85。
/// 写操作一律先写后读回校验，官方驱动也这么做（鼠标会静默拒绝越界值）。
/// </summary>
public sealed class MouseConfigClient
{
    private const byte PageDevice = 0x00;
    private const byte PageProfile = 0x01;
    private const byte TargetMouse = 0x02;

    private readonly WlMouseDevice _device;
    private readonly List<string> _log;

    public MouseConfigClient(WlMouseDevice device, List<string> log)
    {
        _device = device;
        _log = log;
    }

    // ---- 读 ----

    public MouseConfig Read()
    {
        int profile = ReadActiveProfile();
        var config = new MouseConfig { Profile = profile, ProductId = _device.Pid };

        config = config with
        {
            DpiStages = ReadDpiStages(profile, out int activeStage),
            ActiveStage = activeStage,
            PollingRateHz = ReadPollingRate(profile),
            LiftOff = ReadLiftOff(profile),
            DebounceMs = ReadByte(PageDevice, 0x88, profile, 0x02),
            SleepSeconds = ReadSleep(profile),
            AngleSnapping = ReadFlag(PageProfile, 0x84, profile),
            MotionSync = ReadFlag(PageProfile, 0x89, profile),
        };

        return config;
    }

    private int ReadActiveProfile()
    {
        if (Read(PageDevice, 0x85, 0x01, null, out byte[] payload) && payload.Length >= 1)
            return Math.Max(1, (int)payload[0]);

        return 1;
    }

    private int[] ReadDpiStages(int profile, out int activeStage)
    {
        activeStage = 0;

        if (!Read(PageProfile, 0x81, 0x0A, new byte[] { (byte)profile, 0x06 }, out byte[] stages)
            || stages.Length < 2)
        {
            return Array.Empty<int>();
        }

        int count = stages[1];
        var result = new int[Math.Clamp(count, 0, 6)];

        for (int i = 0; i < result.Length; i++)
        {
            int offset = 2 + i * 4;
            if (offset + 1 >= stages.Length) break;
            result[i] = (stages[offset] << 8) | stages[offset + 1];
        }

        // 当前档位回包是 1 起的序号
        if (result.Length > 0
            && Read(PageProfile, 0x82, 0x02, new byte[] { (byte)profile }, out byte[] current)
            && current.Length >= 2)
        {
            activeStage = Math.Clamp(current[1] - 1, 0, result.Length - 1);
        }

        return result;
    }

    private int? ReadPollingRate(int profile)
    {
        if (!Read(PageProfile, 0x80, 0x02, new byte[] { (byte)profile }, out byte[] payload)
            || payload.Length < 2)
        {
            return null;
        }

        return PollingRates.Decode(payload[1]);
    }

    private LiftOffDistance? ReadLiftOff(int profile)
    {
        if (!Read(PageProfile, 0x88, 0x02, new byte[] { (byte)profile }, out byte[] payload)
            || payload.Length < 2
            || payload[1] == 0)
        {
            return null;
        }

        // 0x87 = 低，0x01/0x02 = 中/高；带 0x80 位的是 0.1mm 精度的毫米值
        byte v = payload[1];
        if (v == 0x87) return LiftOffDistance.Low;
        if (v == 0x01) return LiftOffDistance.Medium;
        if (v == 0x02) return LiftOffDistance.High;

        double mm = (v & 0x80) != 0 ? (v & 0x7F) / 10.0 : v;
        return mm < 1 ? LiftOffDistance.Low : mm < 2 ? LiftOffDistance.Medium : LiftOffDistance.High;
    }

    private int? ReadSleep(int profile)
    {
        if (!Read(PageDevice, 0x87, 0x03, new byte[] { (byte)profile }, out byte[] payload)
            || payload.Length < 3)
        {
            return null;
        }

        int seconds = (payload[1] << 8) | payload[2];

        // 0 或 >= 0xFF00 表示「不休眠」
        return seconds == 0 || seconds >= 0xFF00 ? null : seconds;
    }

    private bool? ReadFlag(byte page, byte command, int profile)
    {
        if (!Read(page, command, 0x02, new byte[] { (byte)profile }, out byte[] payload)
            || payload.Length < 2)
        {
            return null;
        }

        return payload[1] == 1;
    }

    private int? ReadByte(byte page, byte command, int profile, byte length)
    {
        if (!Read(page, command, length, new byte[] { (byte)profile }, out byte[] payload)
            || payload.Length < 2)
        {
            return null;
        }

        return payload[1];
    }

    // ---- 写（写完立即读回校验） ----

    public bool SetPollingRate(int hz)
    {
        int profile = ReadActiveProfile();
        byte encoded = PollingRates.Encode(hz);

        if (!Write(PageProfile, 0x00, profile, encoded)) return false;

        int? confirmed = ReadPollingRate(profile);
        _log.Add($"设置回报率 {hz} Hz → 读回 {confirmed?.ToString() ?? "—"} Hz");
        return confirmed == hz;
    }

    public bool SetDpiStage(int stage, int dpi)
    {
        int profile = ReadActiveProfile();
        int[] stages = ReadDpiStages(profile, out _);
        if (stage < 0 || stage >= stages.Length) return false;

        stages[stage] = dpi;
        return WriteDpiStages(profile, stages);
    }

    public bool SetActiveDpiStage(int stage)
    {
        int profile = ReadActiveProfile();
        int[] stages = ReadDpiStages(profile, out _);
        if (stage < 0 || stage >= stages.Length) return false;

        if (!Write(PageProfile, 0x02, profile, (byte)(stage + 1))) return false;

        bool ok = Read(PageProfile, 0x82, 0x02, new byte[] { (byte)profile }, out byte[] current)
                  && current.Length >= 2
                  && Math.Clamp(current[1] - 1, 0, stages.Length - 1) == stage;

        _log.Add($"切换 DPI 档位 → 第 {stage + 1} 档 {(ok ? "成功" : "失败")}");
        return ok;
    }

    private bool WriteDpiStages(int profile, int[] stages)
    {
        // 整张表一次写回：档数 + 每档 4 字节（xHi xLo yHi yLo），Y 与 X 相同
        var args = new List<byte> { (byte)stages.Length };
        foreach (int dpi in stages)
        {
            args.Add((byte)(dpi >> 8));
            args.Add((byte)(dpi & 0xFF));
            args.Add((byte)(dpi >> 8));
            args.Add((byte)(dpi & 0xFF));
        }

        if (!Write(PageProfile, 0x01, profile, args.ToArray())) return false;

        int[] confirmed = ReadDpiStages(profile, out _);
        bool ok = confirmed.Length == stages.Length
                  && stages.Zip(confirmed).All(pair => pair.First == pair.Second);

        _log.Add($"写入 DPI 表 [{string.Join(", ", stages)}] → 读回 [{string.Join(", ", confirmed)}]");
        return ok;
    }

    public bool SetLiftOff(LiftOffDistance value)
    {
        int profile = ReadActiveProfile();
        byte encoded = value switch
        {
            LiftOffDistance.Low => 0x87,
            LiftOffDistance.Medium => 0x01,
            _ => 0x02,
        };

        if (!Write(PageProfile, 0x08, profile, encoded)) return false;

        LiftOffDistance? confirmed = ReadLiftOff(profile);
        _log.Add($"设置抬升高度 {value} → 读回 {confirmed?.ToString() ?? "—"}");
        return confirmed == value;
    }

    public bool SetDebounce(int milliseconds)
    {
        int profile = ReadActiveProfile();
        if (!Write(PageDevice, 0x08, profile, (byte)milliseconds)) return false;

        int? confirmed = ReadByte(PageDevice, 0x88, profile, 0x02);
        _log.Add($"设置去抖 {milliseconds} ms → 读回 {confirmed?.ToString() ?? "—"} ms");
        return confirmed == milliseconds;
    }

    public bool SetSleep(int seconds)
    {
        int profile = ReadActiveProfile();
        if (!Write(PageDevice, 0x07, profile, (byte)(seconds >> 8), (byte)(seconds & 0xFF))) return false;

        int? confirmed = ReadSleep(profile);
        _log.Add($"设置休眠 {seconds} s → 读回 {confirmed?.ToString() ?? "不休眠"}");
        return confirmed == seconds;
    }

    public bool SetAngleSnapping(bool enabled)
    {
        int profile = ReadActiveProfile();
        if (!Write(PageProfile, 0x04, profile, enabled ? (byte)1 : (byte)0)) return false;

        bool? confirmed = ReadFlag(PageProfile, 0x84, profile);
        _log.Add($"设置角度吸附 {enabled} → 读回 {confirmed?.ToString() ?? "—"}");
        return confirmed == enabled;
    }

    public bool SetMotionSync(bool enabled)
    {
        int profile = ReadActiveProfile();
        if (!Write(PageProfile, 0x09, profile, enabled ? (byte)1 : (byte)0)) return false;

        bool? confirmed = ReadFlag(PageProfile, 0x89, profile);
        _log.Add($"设置 Motion Sync {enabled} → 读回 {confirmed?.ToString() ?? "—"}");
        return confirmed == enabled;
    }

    // ---- 底层 ----

    private bool Read(byte page, byte command, byte length, byte[]? args, out byte[] payload) =>
        _device.Command(TargetMouse, page, command, length, args, out payload);

    private bool Write(byte page, byte command, int profile, params byte[] values)
    {
        // 写命令的参数列表就是 [配置档, ...值]，长度取列表本身
        var args = new byte[values.Length + 1];
        args[0] = (byte)profile;
        Array.Copy(values, 0, args, 1, values.Length);

        return _device.Command(TargetMouse, page, command, (byte)args.Length, args, out _);
    }
}
