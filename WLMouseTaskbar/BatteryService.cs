using WLMouseTray;

namespace WLMouseTaskbar;

/// <summary>一次采样得到的状态。</summary>
public sealed record BatteryState(
    bool DevicePresent,
    int? Percent,
    bool Charging,
    string? MouseFirmware,
    string? ReceiverFirmware,
    string RawHex,
    string? Error,
    DateTime Timestamp)
{
    public static BatteryState Initial { get; } =
        new(false, null, false, null, null, "", "正在读取…", DateTime.MinValue);
}

/// <summary>
/// 后台电量采集：持有 HID 句柄、失败指数退避、线程安全。
/// 所有 HID 调用都在线程池上跑，UI 线程只收 <see cref="Updated"/>。
/// </summary>
public sealed class BatteryService : IDisposable
{
    private readonly Lock _gate = new();

    /// <summary>所有 HID 收发都在这把锁里串行执行（定时器线程和界面线程会同时用设备）。</summary>
    private readonly Lock _hid = new();

    private readonly System.Threading.Timer _timer;

    private WlMouseDevice? _device;
    private int _failureCount;
    private DateTime _nextAttempt = DateTime.MinValue;
    private bool _busy;
    private bool _configLoaded;

    private readonly List<string> _log = new();

    public int PollSeconds { get; set; } = 60;
    public BatteryState State { get; private set; } = BatteryState.Initial;

    /// <summary>最近一次读到的鼠标配置（DPI / 回报率等）。</summary>
    public MouseConfig Config { get; private set; } = MouseConfig.Empty;

    /// <summary>在后台线程触发，订阅方需要自己切回 UI 线程。</summary>
    public event Action<BatteryState>? Updated;

    /// <summary>配置读完后在后台线程触发。</summary>
    public event Action<MouseConfig>? ConfigUpdated;

    public BatteryService()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public string LogText
    {
        get { lock (_log) return string.Join(Environment.NewLine, _log); }
    }

    private void Log(string message)
    {
        lock (_log)
        {
            _log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (_log.Count > 300) _log.RemoveRange(300, _log.Count - 300);
        }
    }

    /// <summary>立即刷新（用户点「刷新」时）。</summary>
    public void RequestRefresh()
    {
        lock (_gate) _nextAttempt = DateTime.MinValue;
        Poll();
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_busy) return;
            if (DateTime.Now < _nextAttempt && _device != null) return;
            _busy = true;
        }

        try
        {
            // 每 5 秒被定时器唤醒一次，但只有到点才真正读设备
            if (DateTime.Now < _nextAttempt && _device != null) return;

            BatteryState state = ReadOnce();
            State = state;
            TaskbarWidgetWindow.Diag($"poll present={state.DevicePresent} pct={state.Percent} err={state.Error}");

            if (state.DevicePresent && state.Error is null)
            {
                _failureCount = 0;
                _nextAttempt = DateTime.Now.AddSeconds(PollSeconds);
            }
            else
            {
                // 15s → 30s → 60s → 120s → 240s → 300s 封顶
                int fails = ++_failureCount;
                int delay = Math.Min(300, 15 << Math.Min(fails - 1, 4));
                _nextAttempt = DateTime.Now.AddSeconds(delay);
                Log($"读取失败（第 {fails} 次），{delay}s 后重试：{state.Error}");
            }

            Updated?.Invoke(state);

            // 配置（DPI / 回报率）是右键菜单和概览页的数据来源。读到电量后补读，
            // 成功一次就不再读——鼠标休眠时读不到，下个轮询周期自然会再试。
            if (!_configLoaded && state.Percent is not null)
                _ = RefreshConfigAsync();
        }
        catch (Exception ex)
        {
            Log($"采集异常：{ex.Message}");
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private BatteryState ReadOnce()
    {
        lock (_hid)
        {
            if (_device is null)
            {
                _device = WlMouseDevice.TryOpen(_log);
                if (_device is null)
                    return new BatteryState(false, null, false, null, null, "", "未检测到接收器", DateTime.Now);
            }

            try
            {
                var (charging, percent, ok) = _device.ReadBattery();
                string raw = WlMouseDevice.Hex(_device.LastResponse);

                if (!ok)
                {
                    // 鼠标休眠 / 接收器重插：丢掉句柄，下次重新枚举
                    _device.Dispose();
                    _device = null;
                    return new BatteryState(true, null, false, null, null, raw,
                        "鼠标无响应（可能已休眠）", DateTime.Now);
                }

                // 固件版本没有时不重复读，省掉无谓的往返
                string? mouseFw = State.MouseFirmware ?? _device.ReadFirmwareVersion();
                string? receiverFw = State.ReceiverFirmware ?? _device.ReadReceiverFirmwareVersion();

                Log($"电量 {percent}%" + (charging == true ? "（充电中）" : "") + $"  raw={raw}");
                return new BatteryState(true, percent, charging == true, mouseFw, receiverFw,
                    raw, null, DateTime.Now);
            }
            catch (Exception ex)
            {
                _device?.Dispose();
                _device = null;
                return new BatteryState(false, null, false, null, null, "", ex.Message, DateTime.Now);
            }
        }
    }

    /// <summary>
    /// 重新读取 DPI / 回报率等配置。在线程池上跑，读完通过 <see cref="ConfigUpdated"/> 通知。
    /// 鼠标休眠时读不到，会返回上一次的结果并记一条日志。
    /// </summary>
    public Task RefreshConfigAsync() => Task.Run(() =>
    {
        lock (_hid)
        {
            if (_device is null)
            {
                _device = WlMouseDevice.TryOpen(_log);
                if (_device is null) return;
            }

            try
            {
                var client = new MouseConfigClient(_device, _log);
                Config = client.Read();
                Log($"配置：回报率 {Config.PollingRateHz?.ToString() ?? "—"} Hz，" +
                    $"DPI {Config.CurrentDpi}");
                if (Config.DpiStages.Length > 0) _configLoaded = true;
                TaskbarWidgetWindow.Diag($"config rate={Config.PollingRateHz} " +
                    $"stages={Config.DpiStages.Length} stage={Config.ActiveStage} loaded={_configLoaded}");
                ConfigUpdated?.Invoke(Config);
            }
            catch (Exception ex)
            {
                TaskbarWidgetWindow.Diag($"config failed: {ex.Message}");
                Log($"读取配置失败：{ex.Message}");
            }
        }
    });

    /// <summary>
    /// 修改一项设置。<paramref name="action"/> 在 HID 锁内执行，完成后自动重读配置。
    /// 返回是否成功（鼠标会静默拒绝越界值，所以以读回校验为准）。
    /// </summary>
    public Task<bool> ChangeConfigAsync(Func<MouseConfigClient, bool> action) => Task.Run(() =>
    {
        lock (_hid)
        {
            if (_device is null)
            {
                _device = WlMouseDevice.TryOpen(_log);
                if (_device is null) return false;
            }

            try
            {
                var client = new MouseConfigClient(_device, _log);
                bool ok = action(client);
                Config = client.Read();
                ConfigUpdated?.Invoke(Config);
                return ok;
            }
            catch (Exception ex)
            {
                Log($"修改设置失败：{ex.Message}");
                return false;
            }
        }
    });

    public void Dispose()
    {
        _timer.Dispose();
        lock (_hid)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
