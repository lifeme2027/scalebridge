using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows;

/// <summary>
/// 把 WinRT 的状态码 / 异常 HRESULT 映射为 Core 的 <see cref="BleException"/> 家族。
/// 异常消息只放操作名与错误码，不放设备地址（避免日志泄漏）。
/// ATT 错误码来自 Bluetooth Core Spec Vol 3 Part F §3.4.1.1；HRESULT 取值来自 Win32 系统错误码文档。
/// 「某个 HRESULT 在真实蓝牙栈里是否确实出现」未经真机验证。
/// </summary>
internal static class WinErrorMapper
{
    // ATT 错误码（Core Spec Vol 3 Part F §3.4.1.1 Table 3.4）
    internal const byte AttInsufficientAuthentication = 0x05;
    internal const byte AttInsufficientAuthorization = 0x08;
    internal const byte AttInsufficientEncryptionKeySize = 0x0C;
    internal const byte AttInsufficientEncryption = 0x0F;
    internal const byte AttAttributeNotFound = 0x0A;
    internal const byte AttReadNotPermitted = 0x02;
    internal const byte AttWriteNotPermitted = 0x03;

    // HRESULT（Win32 错误码经 HRESULT_FROM_WIN32 转换：0x8007xxxx）
    internal const int EAccessDenied = unchecked((int)0x80070005);
    internal const int ErrorNotReady = unchecked((int)0x80070015);
    internal const int ErrorGenFailure = unchecked((int)0x8007001F);
    internal const int ErrorSemTimeout = unchecked((int)0x80070079);
    internal const int ErrorDeviceNotConnected = unchecked((int)0x8007048F);
    internal const int ErrorNotFound = unchecked((int)0x80070490);
    internal const int ErrorTimeout = unchecked((int)0x800705B4);
    internal const int ErrorOperationAborted = unchecked((int)0x800703E3);
    internal const int ErrorDeviceNotAvailable = unchecked((int)0x800710DF);
    internal const int RpcEDisconnected = unchecked((int)0x80010108);
    internal const int EIllegalMethodCall = unchecked((int)0x8000000E);

    public static BleAvailability ToAvailability(WinRadioState state) => state switch
    {
        WinRadioState.On or WinRadioState.Unknown => BleAvailability.Available,
        _ => BleAvailability.Unavailable,
    };

    /// <summary>给用户看的无线电状态说明（英文技术描述，UI 层用本地化文案另行包装）。</summary>
    public static string DescribeRadio(WinRadioState state) => state switch
    {
        WinRadioState.NoAdapter => "no Bluetooth adapter found",
        WinRadioState.LowEnergyNotSupported => "the Bluetooth adapter does not support Bluetooth LE",
        WinRadioState.Off => "the Bluetooth radio is turned off",
        WinRadioState.Disabled => "the Bluetooth radio is disabled by the system or policy",
        _ => "Bluetooth is available",
    };

    public static BleException FromRadio(WinRadioState state) =>
        new BluetoothUnavailableException($"Bluetooth unavailable: {DescribeRadio(state)}.");

    public static BleException FromGattStatus(WinGattStatus status, byte? protocolError, string operation)
    {
        switch (status)
        {
            case WinGattStatus.Success:
                throw new ArgumentException("Success 不是错误。", nameof(status));
            case WinGattStatus.Unreachable:
                return new DeviceDisconnectedException($"{operation}: the device is unreachable.");
            case WinGattStatus.AccessDenied:
                return new BlePermissionException($"{operation}: access was denied (the device may need to be paired in Windows settings first).");
            case WinGattStatus.ProtocolError:
                return FromProtocolError(protocolError, operation);
            default:
                return new BleException(BleErrorKind.Other, $"{operation}: unknown GATT status.");
        }
    }

    public static BleException FromProtocolError(byte? code, string operation)
    {
        if (code is null)
            return new BleException(BleErrorKind.Other, $"{operation}: ATT protocol error (no code).");
        var c = code.Value;
        if (c is AttInsufficientAuthentication or AttInsufficientAuthorization
            or AttInsufficientEncryptionKeySize or AttInsufficientEncryption)
            return new BlePermissionException(
                $"{operation}: the device requires pairing/encryption (ATT error 0x{c:X2}); pair it in Windows settings and retry.");
        if (c == AttAttributeNotFound)
            return new BleException(BleErrorKind.NoSupportedService, $"{operation}: attribute not found (ATT error 0x{c:X2}).");
        return new BleException(BleErrorKind.Other, $"{operation}: ATT protocol error 0x{c:X2}.");
    }

    public static BleException FromBluetoothError(WinBluetoothError error, string operation) => error switch
    {
        WinBluetoothError.RadioNotAvailable => new BluetoothUnavailableException($"{operation}: the Bluetooth radio is not available (turned off?)."),
        WinBluetoothError.TransportNotSupported or WinBluetoothError.NotSupported =>
            new BluetoothUnavailableException($"{operation}: not supported by the Bluetooth hardware/driver ({error})."),
        WinBluetoothError.DisabledByPolicy or WinBluetoothError.DisabledByUser or WinBluetoothError.ConsentRequired =>
            new BlePermissionException($"{operation}: blocked by policy/user/consent ({error})."),
        WinBluetoothError.DeviceNotConnected => new DeviceDisconnectedException($"{operation}: the device is not connected."),
        WinBluetoothError.ResourceInUse => new BleException(BleErrorKind.Other, $"{operation}: the Bluetooth resource is in use by another operation."),
        _ => new BleException(BleErrorKind.Other, $"{operation}: Bluetooth error {error}."),
    };

    /// <summary>
    /// 把任意异常映射成 BleException。已经是 BleException 的原样返回；
    /// OperationCanceledException 不应交给这里（调用方先放行）。
    /// </summary>
    public static BleException FromException(Exception ex, string operation)
    {
        switch (ex)
        {
            case BleException be:
                return be;
            case TimeoutException:
                return new BleTimeoutException($"{operation}: timed out.", ex);
            case UnauthorizedAccessException:
                return new BlePermissionException($"{operation}: access denied.", ex);
            case ObjectDisposedException:
                return new DeviceDisconnectedException($"{operation}: the device object was already released.", ex);
        }

        return ex.HResult switch
        {
            EAccessDenied => new BlePermissionException($"{operation}: access denied (0x{ex.HResult:X8}).", ex),
            ErrorNotReady => new BluetoothUnavailableException($"{operation}: the Bluetooth device is not ready (0x{ex.HResult:X8}).", ex),
            ErrorTimeout or ErrorSemTimeout => new BleTimeoutException($"{operation}: timed out (0x{ex.HResult:X8}).", ex),
            ErrorDeviceNotConnected or ErrorNotFound or ErrorDeviceNotAvailable or RpcEDisconnected =>
                new DeviceDisconnectedException($"{operation}: the device is not available or disconnected (0x{ex.HResult:X8}).", ex),
            _ => new BleException(BleErrorKind.Other, $"{operation}: {ex.GetType().Name} (0x{ex.HResult:X8}).", ex),
        };
    }

    public static BleAvailability ToAvailability(Exception ex) => FromException(ex, "availability").Kind switch
    {
        BleErrorKind.PermissionDenied => BleAvailability.PermissionDenied,
        _ => BleAvailability.Unavailable,
    };
}
