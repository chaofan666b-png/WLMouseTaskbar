namespace WLMouseTray;

/// <summary>命令行诊断工具：列出 HID 接口并尝试读取鼠标电量。</summary>
internal static class ProbeProgram
{
    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("== HID 接口 ==");
        var all = Hid.Enumerate();
        Console.WriteLine($"  (枚举到 {all.Count} 个 HID 接口) {Hid.Diagnostics.EnumerateError}");
        foreach (var itf in all.Where(i => i.Vid == WlMouseDevice.DefaultVid))
        {
            string path = itf.Path.Split('#') is var p && p.Length > 1 ? p[1] : itf.Path;
            Console.WriteLine($"  {path}  usagePage=0x{itf.UsagePage:X4} usage=0x{itf.Usage:X4} feature={itf.FeatureReportLength}");
        }

        var log = new List<string>();
        using var device = WlMouseDevice.TryOpen(log);
        foreach (string line in log) Console.WriteLine($"  log: {line}");
        if (device == null)
        {
            Console.WriteLine("未找到 WLmouse 配置接口");
            return;
        }

        Console.WriteLine($"\n== VID {device.Vid:X4} PID {device.Pid:X4} ==");

        for (int i = 0; i < 3; i++)
        {
            var (charging, percent, ok) = device.ReadBattery();
            Console.WriteLine($"电量 #{i + 1}: ok={ok} charging={charging} percent={percent}  raw={WlMouseDevice.Hex(device.LastResponse)}");
            Thread.Sleep(300);
        }

        Console.WriteLine($"鼠标固件: {device.ReadFirmwareVersion() ?? "—"}    raw={WlMouseDevice.Hex(device.LastResponse)}");
        Console.WriteLine($"接收器固件: {device.ReadReceiverFirmwareVersion() ?? "—"}    raw={WlMouseDevice.Hex(device.LastResponse)}");
    }
}
