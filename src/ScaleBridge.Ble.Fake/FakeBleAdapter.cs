using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Ble.Fake;

/// <summary>
/// 演示用的蓝牙适配器：按 <see cref="FakeScenario"/> 回放扫描、连接与 indicate。
/// <b>产生的全是演示数据（IsDemo = true）</b>，不涉及任何真实蓝牙硬件。
/// </summary>
public sealed class FakeBleAdapter : IBleAdapter
{
    private readonly FakeScenario _scenario;
    private readonly double _delayScale;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, int> _connectCounts = new();
    private readonly object _gate = new();

    /// <param name="delayScale">延时倍率：1 = 按脚本真实时间，0 = 不等待（测试用）。</param>
    public FakeBleAdapter(FakeScenario scenario, double delayScale = 1.0, TimeProvider? clock = null)
    {
        _scenario = scenario;
        _delayScale = Math.Max(0, delayScale);
        _clock = clock ?? TimeProvider.System;
    }

    public FakeScenario Scenario => _scenario;
    public string Id => "fake";
    public string DisplayName => "Fake (demo)";
    public bool IsDemo => true;

    public Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default) =>
        Task.FromResult(BleAvailability.Available);

    internal Task DelayAsync(int ms, CancellationToken ct) =>
        _delayScale <= 0 || ms <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(ms * _delayScale), ct);

    public async IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = options.ServiceFilter is { Count: > 0 } f ? f : null;
        var devices = _scenario.Devices
            .Select(d => (Device: d, Services: d.Services.Select(ParseUuid).ToList()))
            .Where(x => filter is null || x.Services.Any(filter.Contains))
            .OrderBy(x => x.Device.AppearAfterMs)
            .ToList();
        const int stepMs = 400; // 脚本时间里每轮广播的间隔
        var pace = Math.Max(5, (int)(stepMs * _delayScale));
        var elapsed = 0;
        var round = 0;
        while (!ct.IsCancellationRequested)
        {
            foreach (var (d, services) in devices)
            {
                if (d.AppearAfterMs > elapsed && _delayScale > 0) continue;
                var jitter = (round % 3) - 1; // 确定性的 ±1 dB 抖动，不使用随机数
                yield return new AdvertisedDevice(
                    d.Id, d.Name, d.Rssi + jitter, services,
                    d.ManufacturerData.ToDictionary(kv => ParseCompany(kv.Key), kv => Convert.FromHexString(kv.Value)),
                    _clock.GetLocalNow(), IsDemo: true);
            }
            round++;
            try { await Task.Delay(pace, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            elapsed += stepMs;
        }
    }

    public async Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var device = _scenario.Devices.FirstOrDefault(d => d.Id == deviceId)
            ?? throw new BleException(BleErrorKind.Other, $"Unknown demo device '{deviceId}'.");
        int attempt;
        lock (_gate)
        {
            _connectCounts.TryGetValue(deviceId, out attempt);
            _connectCounts[deviceId] = attempt + 1;
        }
        var script = device.Connections.Count == 0
            ? new FakeConnectionScript()
            : device.Connections[Math.Min(attempt, device.Connections.Count - 1)];
        await DelayAsync(script.ConnectDelayMs, ct).ConfigureAwait(false);
        switch (script.FailWith?.ToLowerInvariant())
        {
            case "timeout": throw new BleTimeoutException("Connecting to the demo device timed out.");
            case "unavailable": throw new BluetoothUnavailableException("Demo: Bluetooth is turned off.");
            case "permission": throw new BlePermissionException("Demo: Bluetooth permission denied.");
        }
        return new FakeConnection(this, device, script);
    }

    internal static Guid ParseUuid(string text) =>
        GattUuids.TryParse(text, out var g) ? g : throw new FormatException($"Bad UUID '{text}' in scenario.");

    private static ushort ParseCompany(string text) =>
        Convert.ToUInt16(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, 16);

    private sealed class FakeConnection : IBleConnection
    {
        private readonly FakeBleAdapter _owner;
        private readonly FakeDevice _device;
        private readonly FakeConnectionScript _script;
        private readonly Dictionary<Guid, Channel<GattNotification>> _channels = new();
        private readonly CancellationTokenSource _cts = new();
        private int _started;
        private volatile ConnectionState _state = ConnectionState.Connected;

        public FakeConnection(FakeBleAdapter owner, FakeDevice device, FakeConnectionScript script)
        {
            _owner = owner;
            _device = device;
            _script = script;
            foreach (var e in script.Events.Where(e => e.Type == "indicate"))
            {
                var ch = ParseUuid(e.Characteristic ?? throw new FormatException("indicate event without characteristic"));
                if (!_channels.ContainsKey(ch)) _channels[ch] = Channel.CreateUnbounded<GattNotification>();
            }
        }

        public string DeviceId => _device.Id;
        public ConnectionState State => _state;
        public event EventHandler<ConnectionStateChange>? StateChanged;

        public Task<IReadOnlyList<GattServiceInfo>> DiscoverServicesAsync(CancellationToken ct = default)
        {
            if (_state != ConnectionState.Connected) throw new DeviceDisconnectedException();
            var result = new List<GattServiceInfo>();
            foreach (var svcText in _device.Services)
            {
                var svc = ParseUuid(svcText);
                var chars = new List<GattCharacteristicInfo>();
                if (svc == GattUuids.WeightScaleService)
                {
                    chars.Add(new(GattUuids.WeightScaleFeature, GattCharacteristicProperties.Read));
                    chars.Add(new(GattUuids.WeightMeasurement, GattCharacteristicProperties.Indicate));
                }
                else if (svc == GattUuids.BodyCompositionService)
                {
                    chars.Add(new(GattUuids.BodyCompositionFeature, GattCharacteristicProperties.Read));
                    chars.Add(new(GattUuids.BodyCompositionMeasurement, GattCharacteristicProperties.Indicate));
                }
                result.Add(new GattServiceInfo(svc, chars));
            }
            return Task.FromResult<IReadOnlyList<GattServiceInfo>>(result);
        }

        public Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct = default)
        {
            if (_state != ConnectionState.Connected) throw new DeviceDisconnectedException();
            foreach (var (k, v) in _device.Features)
                if (ParseUuid(k) == characteristic) return Task.FromResult(Convert.FromHexString(v));
            throw new BleException(BleErrorKind.Other, $"Characteristic {characteristic} is not readable on the demo device.");
        }

        public async IAsyncEnumerable<GattNotification> SubscribeAsync(Guid service, Guid characteristic, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_state != ConnectionState.Connected) throw new DeviceDisconnectedException();
            if (!_channels.TryGetValue(characteristic, out var channel))
            {
                // 设备没有脚本化事件：保持订阅直到取消/断开
                channel = Channel.CreateUnbounded<GattNotification>();
                _channels[characteristic] = channel;
            }
            if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(RunScriptAsync, CancellationToken.None);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            while (true)
            {
                GattNotification n;
                try
                {
                    n = await channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (ChannelClosedException ex) when (ex.InnerException is not null)
                {
                    throw ex.InnerException; // 脚本里的 disconnect → DeviceDisconnectedException
                }
                catch (ChannelClosedException) { yield break; }
                catch (OperationCanceledException) { yield break; }
                yield return n;
            }
        }

        private async Task RunScriptAsync()
        {
            try
            {
                foreach (var e in _script.Events)
                {
                    await _owner.DelayAsync(e.DelayMs, _cts.Token).ConfigureAwait(false);
                    if (e.Type == "disconnect")
                    {
                        var reason = e.Reason ?? "Device disconnected (demo).";
                        _state = ConnectionState.Dropped;
                        foreach (var c in _channels.Values) c.Writer.TryComplete(new DeviceDisconnectedException(reason));
                        StateChanged?.Invoke(this, new ConnectionStateChange(ConnectionState.Dropped, reason, BleErrorKind.DeviceDisconnected));
                        return;
                    }
                    var ch = ParseUuid(e.Characteristic!);
                    var svc = ch == GattUuids.BodyCompositionMeasurement ? GattUuids.BodyCompositionService : GattUuids.WeightScaleService;
                    _channels[ch].Writer.TryWrite(new GattNotification(svc, ch, Convert.FromHexString(e.Hex ?? ""), _owner._clock.GetLocalNow()));
                }
                // 脚本播完：设备保持连接、不再推送，直到被取消/断开
            }
            catch (OperationCanceledException) { }
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            if (_state == ConnectionState.Connected)
            {
                _state = ConnectionState.Disconnected;
                StateChanged?.Invoke(this, new ConnectionStateChange(ConnectionState.Disconnected));
            }
            _cts.Cancel();
            foreach (var c in _channels.Values) c.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
