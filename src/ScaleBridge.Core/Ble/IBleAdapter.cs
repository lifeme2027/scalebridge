namespace ScaleBridge.Core.Ble;

/// <summary>
/// 蓝牙适配器抽象。阶段 1 只有 Fake 实现；阶段 2 在此接口上实现 Windows(WinRT) 与 Linux(BlueZ)。
/// 约定：
/// <list type="bullet">
/// <item>失败一律抛 <see cref="BleException"/> 的子类（见 <see cref="BleErrorKind"/>），不要把平台异常泄漏出来。</item>
/// <item>取消通过 <see cref="CancellationToken"/>：扫描流在取消时正常结束或抛 <see cref="OperationCanceledException"/>，调用方两者都要处理。</item>
/// </list>
/// </summary>
public interface IBleAdapter
{
    /// <summary>稳定的适配器标识："fake" / "windows" / "linux"。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>该适配器产生的是否为演示数据（真实蓝牙适配器为 false）。</summary>
    bool IsDemo { get; }

    Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default);

    /// <summary>开始扫描并以事件流形式返回广播，直到取消。同一设备可重复出现。</summary>
    IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, CancellationToken ct = default);

    /// <summary>连接设备。超时抛 <see cref="BleTimeoutException"/>。</summary>
    Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>一个已建立（或正在建立）的 GATT 连接。释放即断开。</summary>
public interface IBleConnection : IAsyncDisposable
{
    string DeviceId { get; }

    ConnectionState State { get; }

    /// <summary>连接状态变化（含掉线原因）。可能在任意线程触发。</summary>
    event EventHandler<ConnectionStateChange>? StateChanged;

    Task<IReadOnlyList<GattServiceInfo>> DiscoverServicesAsync(CancellationToken ct = default);

    Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct = default);

    /// <summary>
    /// 订阅特征的 notify/indicate，返回推送流。取消或释放时结束；连接丢失时抛 <see cref="DeviceDisconnectedException"/>。
    /// </summary>
    IAsyncEnumerable<GattNotification> SubscribeAsync(Guid service, Guid characteristic, CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);
}
