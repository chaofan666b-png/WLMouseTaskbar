namespace WLMouseTray;

/// <summary>
/// 命令行诊断工具。默认读电量 + dump 配置页；带 --set 参数可以改值，用于验证写命令。
///
///   dotnet run -- set polling 4000
///   dotnet run -- set dpi 1 1600
///   dotnet run -- set lod High
///   dotnet run -- set debounce 4
///   dotnet run -- set sleep 600
///   dotnet run -- set snap on
///   dotnet run -- set sync off
/// </summary>
internal static class ProbeProgram
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 只在显式要求时刷屏，否则输出会淹没真正的结果
        bool verbose = args.Contains("--verbose");

        if (verbose) DumpInterfaces();

        var log = new List<string>();
        using var device = WlMouseDevice.TryOpen(log);

        foreach (string line in log) Console.WriteLine($"  log: {line}");
        if (device == null)
        {
            Console.WriteLine("未找到 WLmouse 配置接口");
            return 1;
        }

        Console.WriteLine($"VID {device.Vid:X4} PID {device.Pid:X4}");

        var (charging, percent, ok) = device.ReadBattery();
        Console.WriteLine($"电量: ok={ok} charging={charging} percent={percent}  raw={WlMouseDevice.Hex(device.LastResponse)}");

        var client = new MouseConfigClient(device, log);

        if (args.Length >= 3 && args[0] == "set")
        {
            bool applied = args[1] switch
            {
                "polling" => client.SetPollingRate(int.Parse(args[2])),
                "dpi" => args.Length >= 4 && client.SetDpiStage(int.Parse(args[2]) - 1, int.Parse(args[3])),
                "stage" => client.SetActiveDpiStage(int.Parse(args[2]) - 1),
                "lod" => client.SetLiftOff(Enum.Parse<LiftOffDistance>(args[2], true)),
                "debounce" => client.SetDebounce(int.Parse(args[2])),
                "sleep" => client.SetSleep(int.Parse(args[2])),
                "snap" => client.SetAngleSnapping(ParseOnOff(args[2])),
                "sync" => client.SetMotionSync(ParseOnOff(args[2])),
                _ => throw new ArgumentException($"未知设置项 {args[1]}"),
            };

            foreach (string line in log) Console.WriteLine($"  {line}");
            Console.WriteLine(applied ? "结果：成功" : "结果：失败");
        }

        DumpConfig(client);

        foreach (string line in log) Console.WriteLine($"  log {line}");
        return 0;
    }

    private static bool ParseOnOff(string value) =>
        value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.Ordinal)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static void DumpInterfaces()
    {
        Console.WriteLine("== HID 接口 ==");
        var all = Hid.Enumerate();
        Console.WriteLine($"  (枚举到 {all.Count} 个 HID 接口) {Hid.Diagnostics.EnumerateError}");

        foreach (var itf in all.Where(i => i.Vid == WlMouseDevice.DefaultVid))
        {
            string path = itf.Path.Split('#') is var p && p.Length > 1 ? p[1] : itf.Path;
            Console.WriteLine($"  {path}  usagePage=0x{itf.UsagePage:X4} usage=0x{itf.Usage:X4} feature={itf.FeatureReportLength}");
        }
    }

    private static void DumpConfig(MouseConfigClient client)
    {
        Console.WriteLine("\n== 配置 ==");

        MouseConfig config = client.Read();
        Console.WriteLine($"  配置档: {config.Profile}");
        Console.WriteLine($"  回报率: {(config.PollingRateHz is int hz ? hz + " Hz" : "读取失败")}");
        Console.WriteLine($"  DPI: {string.Join(" / ", config.DpiStages)}  （当前第 {config.ActiveStage + 1} 档 = {config.CurrentDpi}）");
        Console.WriteLine($"  抬升高度: {config.LiftOff?.ToString() ?? "—"}");
        Console.WriteLine($"  去抖: {config.DebounceMs?.ToString() ?? "—"} ms");
        Console.WriteLine($"  休眠: {config.SleepSeconds?.ToString() ?? "—"} s");
        Console.WriteLine($"  角度吸附: {config.AngleSnapping?.ToString() ?? "—"}");
        Console.WriteLine($"  Motion Sync: {config.MotionSync?.ToString() ?? "—"}");
    }
}
