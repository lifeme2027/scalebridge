namespace ScaleBridge.Core.Ble;

/// <summary>蓝牙操作失败的分类。适配器必须把平台错误映射到这些类别，UI 据此给出可读提示。</summary>
public enum BleErrorKind
{
    /// <summary>蓝牙不可用：没有适配器、已关闭或驱动不可用。</summary>
    BluetoothUnavailable,
    /// <summary>没有蓝牙权限（系统拒绝）。</summary>
    PermissionDenied,
    /// <summary>连接/发现/读取超时。</summary>
    Timeout,
    /// <summary>设备已断开（对端关机、走出范围、主动断开等）。</summary>
    DeviceDisconnected,
    /// <summary>设备没有本程序支持的服务（0x181D / 0x181B）。</summary>
    NoSupportedService,
    /// <summary>其它错误。</summary>
    Other,
}

public class BleException : Exception
{
    public BleException(BleErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
    public BleErrorKind Kind { get; }
}

public sealed class BluetoothUnavailableException : BleException
{
    public BluetoothUnavailableException(string message = "Bluetooth is unavailable.", Exception? inner = null)
        : base(BleErrorKind.BluetoothUnavailable, message, inner) { }
}

public sealed class BlePermissionException : BleException
{
    public BlePermissionException(string message = "Bluetooth permission was denied.", Exception? inner = null)
        : base(BleErrorKind.PermissionDenied, message, inner) { }
}

public sealed class BleTimeoutException : BleException
{
    public BleTimeoutException(string message = "The Bluetooth operation timed out.", Exception? inner = null)
        : base(BleErrorKind.Timeout, message, inner) { }
}

public sealed class DeviceDisconnectedException : BleException
{
    public DeviceDisconnectedException(string message = "The device disconnected.", Exception? inner = null)
        : base(BleErrorKind.DeviceDisconnected, message, inner) { }
}
