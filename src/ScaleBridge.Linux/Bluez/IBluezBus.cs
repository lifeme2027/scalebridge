namespace ScaleBridge.Linux.Bluez;

/// <summary>BlueZ D-Bus 名称与接口常量。</summary>
internal static class BluezNames
{
    public const string Service = "org.bluez";
    public const string Adapter1 = "org.bluez.Adapter1";
    public const string Device1 = "org.bluez.Device1";
    public const string GattService1 = "org.bluez.GattService1";
    public const string GattCharacteristic1 = "org.bluez.GattCharacteristic1";
    public const string RootPath = "/org/bluez";
}

/// <summary>已规范化的 BlueZ 对象：接口名 → 属性名 → 值。</summary>
internal sealed record BluezObject(string Path, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Interfaces);

/// <summary>PropertiesChanged 的规范化载荷。</summary>
internal sealed record BluezPropertiesChanged(
    string Path,
    string Interface,
    IReadOnlyDictionary<string, object?> Changed,
    IReadOnlyList<string> Invalidated);

/// <summary>SetDiscoveryFilter 参数。</summary>
internal sealed record BluezDiscoveryFilter(IReadOnlyList<string> Uuids, string Transport = "le", bool DuplicateData = true);

/// <summary>BlueZ 对象管理与 GATT 的最小总线抽象。</summary>
internal interface IBluezBus : IAsyncDisposable
{
    /// <summary>GetManagedObjects。</summary>
    Task<IReadOnlyList<BluezObject>> GetManagedObjectsAsync(CancellationToken ct);

    /// <summary>Adapter1.SetDiscoveryFilter。</summary>
    Task SetDiscoveryFilterAsync(string adapterPath, BluezDiscoveryFilter filter, CancellationToken ct);

    /// <summary>Adapter1.StartDiscovery。</summary>
    Task StartDiscoveryAsync(string adapterPath, CancellationToken ct);

    /// <summary>Adapter1.StopDiscovery。</summary>
    Task StopDiscoveryAsync(string adapterPath, CancellationToken ct);

    /// <summary>Device1.Connect。</summary>
    Task ConnectDeviceAsync(string devicePath, CancellationToken ct);

    /// <summary>Device1.Disconnect。</summary>
    Task DisconnectDeviceAsync(string devicePath, CancellationToken ct);

    /// <summary>GattCharacteristic1.ReadValue({})。</summary>
    Task<byte[]> ReadValueAsync(string characteristicPath, CancellationToken ct);

    /// <summary>GattCharacteristic1.StartNotify。</summary>
    Task StartNotifyAsync(string characteristicPath, CancellationToken ct);

    /// <summary>GattCharacteristic1.StopNotify。</summary>
    Task StopNotifyAsync(string characteristicPath, CancellationToken ct);

    /// <summary>订阅 ObjectManager.InterfacesAdded。</summary>
    Task<IDisposable> WatchInterfacesAddedAsync(Action<BluezObject> handler, CancellationToken ct);

    /// <summary>订阅 ObjectManager.InterfacesRemoved（路径与接口名）。</summary>
    Task<IDisposable> WatchInterfacesRemovedAsync(Action<string, IReadOnlyList<string>> handler, CancellationToken ct);

    /// <summary>订阅 PropertiesChanged；path 为 null 时覆盖 /org/bluez 下全部对象。</summary>
    Task<IDisposable> WatchPropertiesChangedAsync(string? path, Action<BluezPropertiesChanged> handler, CancellationToken ct);

    /// <summary>连接关闭。参数为原因，正常关闭时为 null。</summary>
    event Action<Exception?>? ConnectionClosed;
}

/// <summary>D-Bus 错误（ErrorName 为接口错误名，消息不得含完整地址或带 MAC 的对象路径）。</summary>
internal sealed class BluezDBusException : Exception
{
    /// <summary>例如 org.bluez.Error.NotReady。</summary>
    public string ErrorName { get; }

    /// <summary>用错误名与不含敏感地址的消息构造。</summary>
    public BluezDBusException(string errorName, string message)
        : base(message)
    {
        ErrorName = errorName;
    }
}
