using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;

namespace ScaleBridge.Linux.Tests.Support;

/// <summary>内存中的 IBluezBus，供纯逻辑测试驱动。</summary>
internal sealed class FakeBluezBus : IBluezBus
{
    private readonly object _gate = new();
    private readonly List<PropWatcher> _propWatchers = [];
    private readonly List<AddedWatcher> _addedWatchers = [];
    private readonly List<RemovedWatcher> _removedWatchers = [];

    public List<BluezObject> Objects { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>方法名（如 ConnectDevice）→ 要抛出的异常。</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>为 false 时每种失败只抛一次后移除；为 true 时一直抛。</summary>
    public bool RepeatFailures { get; set; }

    /// <summary>方法名 → 在记录调用前等待的钩子。</summary>
    public Dictionary<string, Func<CancellationToken, Task>> Gates { get; } = [];

    /// <summary>ReadValue 按特征路径返回的字节。</summary>
    public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

    public int ActiveWatchers
    {
        get
        {
            lock (_gate)
                return _propWatchers.Count + _addedWatchers.Count + _removedWatchers.Count;
        }
    }

    public bool Disposed { get; private set; }

    public event Action<Exception?>? ConnectionClosed;

    public async Task<IReadOnlyList<BluezObject>> GetManagedObjectsAsync(CancellationToken ct)
    {
        await BeforeAsync("GetManagedObjects", null, ct).ConfigureAwait(false);
        return Objects.ToArray();
    }

    public Task SetDiscoveryFilterAsync(string adapterPath, BluezDiscoveryFilter filter, CancellationToken ct) =>
        BeforeAsync("SetDiscoveryFilter", adapterPath, ct);

    public Task StartDiscoveryAsync(string adapterPath, CancellationToken ct) =>
        BeforeAsync("StartDiscovery", adapterPath, ct);

    public Task StopDiscoveryAsync(string adapterPath, CancellationToken ct) =>
        BeforeAsync("StopDiscovery", adapterPath, ct);

    public Task ConnectDeviceAsync(string devicePath, CancellationToken ct) =>
        BeforeAsync("ConnectDevice", devicePath, ct);

    public Task DisconnectDeviceAsync(string devicePath, CancellationToken ct) =>
        BeforeAsync("DisconnectDevice", devicePath, ct);

    public async Task<byte[]> ReadValueAsync(string characteristicPath, CancellationToken ct)
    {
        await BeforeAsync("ReadValue", characteristicPath, ct).ConfigureAwait(false);
        return Values.TryGetValue(characteristicPath, out var bytes) ? bytes : [];
    }

    public Task StartNotifyAsync(string characteristicPath, CancellationToken ct) =>
        BeforeAsync("StartNotify", characteristicPath, ct);

    public Task StopNotifyAsync(string characteristicPath, CancellationToken ct) =>
        BeforeAsync("StopNotify", characteristicPath, ct);

    public async Task<IDisposable> WatchInterfacesAddedAsync(Action<BluezObject> handler, CancellationToken ct)
    {
        await BeforeAsync("WatchInterfacesAdded", null, ct).ConfigureAwait(false);
        var watcher = new AddedWatcher(this, handler);
        lock (_gate)
            _addedWatchers.Add(watcher);
        return watcher;
    }

    public async Task<IDisposable> WatchInterfacesRemovedAsync(Action<string, IReadOnlyList<string>> handler, CancellationToken ct)
    {
        await BeforeAsync("WatchInterfacesRemoved", null, ct).ConfigureAwait(false);
        var watcher = new RemovedWatcher(this, handler);
        lock (_gate)
            _removedWatchers.Add(watcher);
        return watcher;
    }

    public async Task<IDisposable> WatchPropertiesChangedAsync(string? path, Action<BluezPropertiesChanged> handler, CancellationToken ct)
    {
        await BeforeAsync("WatchPropertiesChanged", path ?? "*", ct).ConfigureAwait(false);
        var watcher = new PropWatcher(this, path, handler);
        lock (_gate)
            _propWatchers.Add(watcher);
        return watcher;
    }

    public void RaisePropertiesChanged(BluezPropertiesChanged change)
    {
        PropWatcher[] snapshot;
        lock (_gate)
            snapshot = _propWatchers.ToArray();
        foreach (var watcher in snapshot)
        {
            if (watcher.Path is null || string.Equals(watcher.Path, change.Path, StringComparison.Ordinal))
                watcher.Handler(change);
        }
    }

    public void RaiseInterfacesAdded(BluezObject obj)
    {
        AddedWatcher[] snapshot;
        lock (_gate)
            snapshot = _addedWatchers.ToArray();
        foreach (var watcher in snapshot)
            watcher.Handler(obj);
    }

    public void RaiseInterfacesRemoved(string path, IReadOnlyList<string> interfaces)
    {
        RemovedWatcher[] snapshot;
        lock (_gate)
            snapshot = _removedWatchers.ToArray();
        foreach (var watcher in snapshot)
            watcher.Handler(path, interfaces);
    }

    public void RaiseConnectionClosed(Exception? error) => ConnectionClosed?.Invoke(error);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task BeforeAsync(string method, string? arg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Gates.TryGetValue(method, out var gate) && gate is not null)
            await gate(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        Exception? failure = null;
        lock (_gate)
        {
            Calls.Add(arg is null ? method : method + ":" + arg);
            if (Failures.TryGetValue(method, out failure) && !RepeatFailures)
                Failures.Remove(method);
        }

        if (failure is not null)
            throw failure;
    }

    private void Remove(PropWatcher watcher)
    {
        lock (_gate)
            _propWatchers.Remove(watcher);
    }

    private void Remove(AddedWatcher watcher)
    {
        lock (_gate)
            _addedWatchers.Remove(watcher);
    }

    private void Remove(RemovedWatcher watcher)
    {
        lock (_gate)
            _removedWatchers.Remove(watcher);
    }

    private sealed class PropWatcher(FakeBluezBus owner, string? path, Action<BluezPropertiesChanged> handler) : IDisposable
    {
        public string? Path { get; } = path;
        public Action<BluezPropertiesChanged> Handler { get; } = handler;
        public void Dispose() => owner.Remove(this);
    }

    private sealed class AddedWatcher(FakeBluezBus owner, Action<BluezObject> handler) : IDisposable
    {
        public Action<BluezObject> Handler { get; } = handler;
        public void Dispose() => owner.Remove(this);
    }

    private sealed class RemovedWatcher(FakeBluezBus owner, Action<string, IReadOnlyList<string>> handler) : IDisposable
    {
        public Action<string, IReadOnlyList<string>> Handler { get; } = handler;
        public void Dispose() => owner.Remove(this);
    }
}

/// <summary>构造 BlueZ 风格对象树的测试数据。</summary>
internal static class BluezTestData
{
    public static BluezObject Adapter(string path, string name, bool powered) =>
        new(path, If(BluezNames.Adapter1, Props(("Name", name), ("Powered", powered))));

    public static BluezObject Device(
        string adapterPath,
        string mac,
        string? name = null,
        short? rssi = null,
        string[]? uuids = null,
        IReadOnlyDictionary<ushort, byte[]>? manufacturerData = null,
        IReadOnlyDictionary<string, byte[]>? serviceData = null,
        bool? servicesResolved = null)
    {
        var path = adapterPath.TrimEnd('/') + "/dev_" + mac.Replace(':', '_').ToUpperInvariant();
        var props = new Dictionary<string, object?>
        {
            ["Address"] = mac,
            ["Adapter"] = adapterPath,
        };
        if (name is not null)
            props["Name"] = name;
        if (rssi is not null)
            props["RSSI"] = rssi.Value;
        if (uuids is not null)
            props["UUIDs"] = uuids;
        if (manufacturerData is not null)
            props["ManufacturerData"] = manufacturerData;
        if (serviceData is not null)
            props["ServiceData"] = serviceData;
        if (servicesResolved is not null)
            props["ServicesResolved"] = servicesResolved.Value;
        return new BluezObject(path, If(BluezNames.Device1, props));
    }

    public static BluezObject Service(string devicePath, int index, string uuid) =>
        new(
            devicePath.TrimEnd('/') + "/service" + index.ToString("D4"),
            If(BluezNames.GattService1, Props(("UUID", uuid), ("Device", devicePath))));

    public static BluezObject Characteristic(string servicePath, int index, string uuid, string[] flags) =>
        new(
            servicePath.TrimEnd('/') + "/char" + index.ToString("D4"),
            If(BluezNames.GattCharacteristic1, Props(("UUID", uuid), ("Service", servicePath), ("Flags", flags))));

    public static IReadOnlyDictionary<string, object?> Props(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(pairs.Length);
        foreach (var (key, value) in pairs)
            map[key] = value;
        return map;
    }

    /// <summary>
    /// 体重秤 + 身体成分：0x181D（0x2A9D indicate、0x2A9E read）与 0x181B（0x2A9C indicate、0x2A9B read）。
    /// </summary>
    public static IReadOnlyList<BluezObject> StandardScale(string adapterPath, string mac)
    {
        var device = Device(
            adapterPath,
            mac,
            name: "Scale",
            rssi: -50,
            uuids:
            [
                Uuid(GattUuids.WeightScaleService),
                Uuid(GattUuids.BodyCompositionService),
            ],
            servicesResolved: true);

        var weight = Service(device.Path, 10, Uuid(GattUuids.WeightScaleService));
        var weightMeasurement = Characteristic(weight.Path, 11, Uuid(GattUuids.WeightMeasurement), ["indicate"]);
        var weightFeature = Characteristic(weight.Path, 12, Uuid(GattUuids.WeightScaleFeature), ["read"]);

        var body = Service(device.Path, 13, Uuid(GattUuids.BodyCompositionService));
        var bodyMeasurement = Characteristic(body.Path, 14, Uuid(GattUuids.BodyCompositionMeasurement), ["indicate"]);
        var bodyFeature = Characteristic(body.Path, 15, Uuid(GattUuids.BodyCompositionFeature), ["read"]);

        return [device, weight, weightMeasurement, weightFeature, body, bodyMeasurement, bodyFeature];
    }

    public static string Uuid(Guid id) => id.ToString("D");

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> If(
        string interfaceName,
        IReadOnlyDictionary<string, object?> props) =>
        new Dictionary<string, IReadOnlyDictionary<string, object?>> { [interfaceName] = props };
}
