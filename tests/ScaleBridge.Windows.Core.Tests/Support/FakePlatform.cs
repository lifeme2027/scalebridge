using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.Core.Tests.Support;

internal sealed class FakeCharacteristic : IWinGattCharacteristic
{
    private WinValueChangedHandler? _handlers;
    private readonly object _gate = new();

    public FakeCharacteristic(Guid uuid, GattCharacteristicProperties properties)
    {
        Uuid = uuid;
        Properties = properties;
    }

    public Guid Uuid { get; }
    public GattCharacteristicProperties Properties { get; }
    public List<WinCccdValue> CccdWrites { get; } = new();
    public int HandlerCount { get; private set; }
    public byte[] ReadValue { get; set; } = Array.Empty<byte>();
    public Func<CancellationToken, Task<WinGattResult<byte[]>>>? ReadOverride { get; set; }
    public Func<WinCccdValue, CancellationToken, Task<WinGattResult<bool>>>? CccdOverride { get; set; }

    public Task<WinGattResult<byte[]>> ReadAsync(CancellationToken ct) =>
        ReadOverride is { } o ? o(ct) : Task.FromResult(WinGattResult<byte[]>.Ok(ReadValue));

    public Task<WinGattResult<bool>> WriteCccdAsync(WinCccdValue value, CancellationToken ct)
    {
        lock (_gate) CccdWrites.Add(value);
        return CccdOverride is { } o ? o(value, ct) : Task.FromResult(WinGattResult<bool>.Ok(true));
    }

    public event WinValueChangedHandler? ValueChanged
    {
        add { lock (_gate) { _handlers += value; HandlerCount++; } }
        remove { lock (_gate) { _handlers -= value; HandlerCount--; } }
    }

    public void Emit(ReadOnlySpan<byte> bytes)
    {
        WinValueChangedHandler? h;
        lock (_gate) h = _handlers;
        h?.Invoke(bytes);
    }
}

internal sealed class FakeService : IWinGattService
{
    public FakeService(Guid uuid, params FakeCharacteristic[] characteristics)
    {
        Uuid = uuid;
        Characteristics = characteristics;
    }

    public Guid Uuid { get; }
    public FakeCharacteristic[] Characteristics { get; }
    public int DisposeCount;
    public Func<CancellationToken, Task<WinGattResult<IReadOnlyList<IWinGattCharacteristic>>>>? CharacteristicsOverride { get; set; }

    public Task<WinGattResult<IReadOnlyList<IWinGattCharacteristic>>> GetCharacteristicsAsync(CancellationToken ct) =>
        CharacteristicsOverride is { } o
            ? o(ct)
            : Task.FromResult(WinGattResult<IReadOnlyList<IWinGattCharacteristic>>.Ok(Characteristics));

    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}

internal sealed class FakeDevice : IWinGattDevice
{
    private readonly Dictionary<Guid, FakeService> _services = new();

    public FakeDevice(ulong address = 0xAABBCCDDEEFF) => BluetoothAddress = address;

    public ulong BluetoothAddress { get; }
    public int DisposeCount;
    public int ServiceQueries;
    public Func<Guid, CancellationToken, Task<WinGattResult<IReadOnlyList<IWinGattService>>>>? ServicesOverride { get; set; }
    public event Action<bool>? ConnectionChanged;

    public FakeDevice With(FakeService service)
    {
        _services[service.Uuid] = service;
        return this;
    }

    public void RaiseConnection(bool connected) => ConnectionChanged?.Invoke(connected);

    public int ConnectionHandlerCount => ConnectionChanged?.GetInvocationList().Length ?? 0;

    public Task<WinGattResult<IReadOnlyList<IWinGattService>>> GetServicesForUuidAsync(Guid serviceUuid, CancellationToken ct)
    {
        Interlocked.Increment(ref ServiceQueries);
        if (ServicesOverride is { } o) return o(serviceUuid, ct);
        IReadOnlyList<IWinGattService> list = _services.TryGetValue(serviceUuid, out var s)
            ? new IWinGattService[] { s }
            : Array.Empty<IWinGattService>();
        return Task.FromResult(WinGattResult<IReadOnlyList<IWinGattService>>.Ok(list));
    }

    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}

internal sealed class FakeWatcher : IWinAdvertisementWatcher
{
    public event Action<WinAdvertisement>? Received;
    public event Action<WinBluetoothError>? Stopped;
    public int StartCount, StopCount, DisposeCount;
    public Exception? StartThrows { get; set; }
    public int ReceivedHandlerCount => Received?.GetInvocationList().Length ?? 0;

    public void Start()
    {
        StartCount++;
        if (StartThrows is { } e) throw e;
    }

    public void Stop() => StopCount++;
    public void Dispose() => DisposeCount++;
    public void Raise(WinAdvertisement ad) => Received?.Invoke(ad);
    public void RaiseStopped(WinBluetoothError error) => Stopped?.Invoke(error);
}

internal sealed class FakePlatform : IWinBluetoothPlatform
{
    public WinRadioState Radio { get; set; } = WinRadioState.On;
    public Exception? RadioThrows { get; set; }
    public Func<CancellationToken, Task<WinRadioState>>? RadioOverride { get; set; }
    public FakeWatcher Watcher { get; } = new();
    public Func<ulong, CancellationToken, Task<IWinGattDevice?>>? OpenOverride { get; set; }
    public IWinGattDevice? DeviceToOpen { get; set; }
    public List<ulong> OpenedAddresses { get; } = new();

    public Task<WinRadioState> GetRadioStateAsync(CancellationToken ct)
    {
        if (RadioThrows is { } e) throw e;
        return RadioOverride is { } o ? o(ct) : Task.FromResult(Radio);
    }

    public IWinAdvertisementWatcher CreateWatcher() => Watcher;

    public Task<IWinGattDevice?> OpenDeviceAsync(ulong address, CancellationToken ct)
    {
        OpenedAddresses.Add(address);
        return OpenOverride is { } o ? o(address, ct) : Task.FromResult(DeviceToOpen);
    }
}

internal static class TestData
{
    public static readonly Guid WeightService = ScaleBridge.Core.Protocol.GattUuids.WeightScaleService;
    public static readonly Guid WeightMeasurement = ScaleBridge.Core.Protocol.GattUuids.WeightMeasurement;
    public static readonly Guid BodyService = ScaleBridge.Core.Protocol.GattUuids.BodyCompositionService;
    public static readonly Guid BodyMeasurement = ScaleBridge.Core.Protocol.GattUuids.BodyCompositionMeasurement;
    public static readonly Guid FeatureChar = ScaleBridge.Core.Protocol.GattUuids.WeightScaleFeature;

    public static (FakeDevice Device, FakeService Service, FakeCharacteristic Measurement, FakeCharacteristic Feature) WeightScale()
    {
        var measurement = new FakeCharacteristic(WeightMeasurement, GattCharacteristicProperties.Indicate);
        var feature = new FakeCharacteristic(FeatureChar, GattCharacteristicProperties.Read) { ReadValue = new byte[] { 1, 2, 3, 4 } };
        var service = new FakeService(WeightService, measurement, feature);
        var device = new FakeDevice().With(service);
        return (device, service, measurement, feature);
    }

    public static WindowsBleConnection Connect(IWinGattDevice device, TimeProvider? time = null, WindowsBleOptions? options = null) =>
        new("AA:BB:CC:DD:EE:FF", device, options ?? new WindowsBleOptions(), time ?? TimeProvider.System);

    public static async Task<T> Within<T>(Task<T> task, int ms = 5000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) throw new TimeoutException("测试等待超时");
        return await task;
    }

    public static async Task Within(Task task, int ms = 5000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) throw new TimeoutException("测试等待超时");
        await task;
    }
}
