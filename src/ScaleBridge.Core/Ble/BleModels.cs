namespace ScaleBridge.Core.Ble;

/// <summary>扫描到的一个广播（同一设备会重复出现，RSSI 等可能变化）。</summary>
/// <param name="Id">平台相关的设备标识（Windows/Linux 通常是 MAC 地址，macOS 是 UUID），在同一适配器内唯一。</param>
/// <param name="ServiceUuids">广播里的服务 UUID（128 位形式）。</param>
/// <param name="ManufacturerData">厂商自定义数据：公司标识 → 载荷。阶段 3 的私有协议识别会用到。</param>
/// <param name="IsDemo">是否来自演示数据（Fake 适配器）。</param>
public sealed record AdvertisedDevice(
    string Id,
    string? Name,
    int? Rssi,
    IReadOnlyList<Guid> ServiceUuids,
    IReadOnlyDictionary<ushort, byte[]> ManufacturerData,
    DateTimeOffset SeenAt,
    bool IsDemo = false);

public sealed record ScanOptions
{
    /// <summary>只返回广播了这些服务之一的设备；null 或空表示不过滤。</summary>
    public IReadOnlyList<Guid>? ServiceFilter { get; init; }
}

public enum BleAvailability
{
    Available,
    /// <summary>没有适配器或蓝牙已关闭。</summary>
    Unavailable,
    PermissionDenied,
    /// <summary>此适配器在当前版本尚未实现（阶段 2）。</summary>
    NotImplemented,
}

[Flags]
public enum GattCharacteristicProperties
{
    None = 0,
    Read = 1,
    Write = 2,
    WriteWithoutResponse = 4,
    Notify = 8,
    /// <summary>Indicate：Weight/Body Composition Measurement（0x2A9D/0x2A9C）使用它。</summary>
    Indicate = 16,
}

public sealed record GattCharacteristicInfo(Guid Uuid, GattCharacteristicProperties Properties);

public sealed record GattServiceInfo(Guid Uuid, IReadOnlyList<GattCharacteristicInfo> Characteristics);

/// <summary>一次 notify/indicate 推送。</summary>
public sealed record GattNotification(Guid Service, Guid Characteristic, byte[] Value, DateTimeOffset ReceivedAt);

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>连接中途丢失（非用户主动断开）。</summary>
    Dropped,
}

/// <param name="Reason">人类可读的原因（掉线/失败时）。</param>
/// <param name="ErrorKind">对应的错误分类（若有）。</param>
public sealed record ConnectionStateChange(ConnectionState State, string? Reason = null, BleErrorKind? ErrorKind = null);
