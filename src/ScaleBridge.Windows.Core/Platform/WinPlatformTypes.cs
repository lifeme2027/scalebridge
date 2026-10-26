using ScaleBridge.Core.Ble;

namespace ScaleBridge.Windows.Platform;

/// <summary>蓝牙无线电（Radio）状态，由 WinRT 外壳从 BluetoothAdapter / Radio 读出后翻译成这个与平台无关的枚举。</summary>
public enum WinRadioState
{
    /// <summary>没有蓝牙适配器（BluetoothAdapter.GetDefaultAsync 返回 null）。</summary>
    NoAdapter,
    /// <summary>有适配器但不支持低功耗蓝牙（IsLowEnergySupported = false）。</summary>
    LowEnergyNotSupported,
    On,
    Off,
    /// <summary>被系统/策略禁用（RadioState.Disabled）。</summary>
    Disabled,
    /// <summary>读不到状态（外壳无法判断）；按「可以尝试」处理，让后续真实操作给出准确错误。</summary>
    Unknown,
}

/// <summary>GATT 操作结果（对应 GattCommunicationStatus，AccessDenied 需要 Windows 10 1903+）。</summary>
public enum WinGattStatus
{
    Success,
    /// <summary>设备不可达（Unreachable）。</summary>
    Unreachable,
    /// <summary>对端返回 ATT 错误，错误码在 <see cref="WinGattResult{T}.ProtocolError"/>。</summary>
    ProtocolError,
    AccessDenied,
    Unknown,
}

/// <summary>对应 Windows.Devices.Bluetooth.BluetoothError（扫描被中止时的原因）。</summary>
public enum WinBluetoothError
{
    Success = 0,
    RadioNotAvailable = 1,
    ResourceInUse = 2,
    DeviceNotConnected = 3,
    OtherError = 4,
    DisabledByPolicy = 5,
    NotSupported = 6,
    DisabledByUser = 7,
    ConsentRequired = 8,
    TransportNotSupported = 9,
}

/// <summary>写 Client Characteristic Configuration Descriptor（CCCD）的目标值。</summary>
public enum WinCccdValue
{
    None,
    Notify,
    Indicate,
}

/// <summary>一次 GATT 调用的结果；<see cref="ProtocolError"/> 只在 <see cref="WinGattStatus.ProtocolError"/> 时有意义（ATT 错误码）。</summary>
public readonly record struct WinGattResult<T>(WinGattStatus Status, byte? ProtocolError, T? Value)
{
    public bool IsSuccess => Status == WinGattStatus.Success;
    public static WinGattResult<T> Ok(T value) => new(WinGattStatus.Success, null, value);
    public static WinGattResult<T> Fail(WinGattStatus status, byte? protocolError = null) => new(status, protocolError, default);
}

/// <summary>一条厂商自定义数据（公司标识 + 载荷）。</summary>
public sealed record WinManufacturerData(ushort CompanyId, byte[] Data);

/// <summary>一次广播事件（广播包与 Scan Response 在 Windows 上是两次独立事件，由 <c>WinAdvertisementCache</c> 合并）。</summary>
/// <param name="Address">48 位蓝牙地址，放在 ulong 低 48 位（WinRT 的 BluetoothAddress）。</param>
/// <param name="Rssi">dBm；外壳原样传入，127（规范里的「不可用」）由缓存层处理。</param>
public sealed record WinAdvertisement(
    ulong Address,
    string? LocalName,
    int Rssi,
    IReadOnlyList<Guid> ServiceUuids,
    IReadOnlyList<WinManufacturerData> ManufacturerData);

/// <summary>
/// 特征值变化回调。<paramref name="value"/> 只在回调期间有效（WinRT 的 IBuffer 可能被复用），
/// 接收方必须在回调里同步拷贝。
/// </summary>
public delegate void WinValueChangedHandler(ReadOnlySpan<byte> value);

/// <summary>蓝牙平台外壳：全部 WinRT 调用的入口。真实实现见 ScaleBridge.Windows；测试用假实现。</summary>
public interface IWinBluetoothPlatform
{
    /// <summary>读取蓝牙无线电状态。失败（如被拒绝）抛异常，由适配器映射。</summary>
    Task<WinRadioState> GetRadioStateAsync(CancellationToken ct);

    /// <summary>创建一个主动扫描的广播监视器（尚未启动）。</summary>
    IWinAdvertisementWatcher CreateWatcher();

    /// <summary>
    /// 打开设备并保持连接请求（BluetoothLEDevice.FromBluetoothAddressAsync + GattSession.MaintainConnection）。
    /// 系统不认识这个地址时返回 null。返回的对象释放时必须同时释放 GattSession 与 Device。
    /// </summary>
    Task<IWinGattDevice?> OpenDeviceAsync(ulong address, CancellationToken ct);
}

public interface IWinAdvertisementWatcher : IDisposable
{
    event Action<WinAdvertisement>? Received;

    /// <summary>监视器停止（含被系统中止）。<see cref="WinBluetoothError.Success"/> 表示正常停止。</summary>
    event Action<WinBluetoothError>? Stopped;

    /// <summary>启动扫描；启动失败抛异常。</summary>
    void Start();

    void Stop();
}

public interface IWinGattDevice : IDisposable
{
    ulong BluetoothAddress { get; }

    /// <summary>连接状态变化：true = Connected，false = Disconnected。可能在任意线程触发。</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>按 UUID 发现服务（BluetoothCacheMode.Uncached）。</summary>
    Task<WinGattResult<IReadOnlyList<IWinGattService>>> GetServicesForUuidAsync(Guid serviceUuid, CancellationToken ct);
}

public interface IWinGattService : IDisposable
{
    Guid Uuid { get; }

    Task<WinGattResult<IReadOnlyList<IWinGattCharacteristic>>> GetCharacteristicsAsync(CancellationToken ct);
}

public interface IWinGattCharacteristic
{
    Guid Uuid { get; }

    GattCharacteristicProperties Properties { get; }

    /// <summary>读取（Uncached）。返回的数组归调用方所有。</summary>
    Task<WinGattResult<byte[]>> ReadAsync(CancellationToken ct);

    /// <summary>写 CCCD 开启/关闭 notify 或 indicate。返回的 Value 无意义。</summary>
    Task<WinGattResult<bool>> WriteCccdAsync(WinCccdValue value, CancellationToken ct);

    /// <summary>特征值变化。首个订阅者加入时外壳挂接系统事件，最后一个退出时摘除。</summary>
    event WinValueChangedHandler? ValueChanged;
}
