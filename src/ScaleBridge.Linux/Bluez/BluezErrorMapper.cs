using System.Text.RegularExpressions;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>把 BlueZ/D-Bus 异常映射为 BleException。IOException 视为总线不可用；取消异常不映射。</summary>
internal static class BluezErrorMapper
{
    private static readonly Regex EmbeddedMac = new(
        @"[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>映射为 BleException。OperationCanceledException 抛 ArgumentException；已是 BleException 则原样返回。</summary>
    public static BleException Map(Exception ex, string operation)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is OperationCanceledException)
            throw new ArgumentException("OperationCanceledException is not mapped; the caller handles cancellation.", nameof(ex));
        if (ex is BleException ble)
            return ble;

        var (kind, label) = Classify(ex);
        var message = $"{operation} failed ({label}).";
        var inner = SafeInner(ex);
        return kind switch
        {
            BleErrorKind.BluetoothUnavailable => new BluetoothUnavailableException(message, inner),
            BleErrorKind.PermissionDenied => new BlePermissionException(message, inner),
            BleErrorKind.Timeout => new BleTimeoutException(message, inner),
            BleErrorKind.DeviceDisconnected => new DeviceDisconnectedException(message, inner),
            _ => new BleException(kind, message, inner),
        };
    }

    /// <summary>总线或适配器不可用（含 IOException 套接字失败与 NotReady/NoServer 一类错误）。</summary>
    public static bool IsBluezUnavailable(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is OperationCanceledException)
            return false;
        if (ex is BluetoothUnavailableException)
            return true;
        if (ex is BleException)
            return false;
        return Classify(ex).Kind == BleErrorKind.BluetoothUnavailable;
    }

    /// <summary>不可用与权限映射到对应可用性；超时等瞬时错误仍视为 Available。</summary>
    public static BleAvailability ToAvailability(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is OperationCanceledException)
            return BleAvailability.Available;
        if (ex is BlePermissionException)
            return BleAvailability.PermissionDenied;
        if (ex is BluetoothUnavailableException)
            return BleAvailability.Unavailable;
        if (ex is BleException)
            return BleAvailability.Available;

        return Classify(ex).Kind switch
        {
            BleErrorKind.BluetoothUnavailable => BleAvailability.Unavailable,
            BleErrorKind.PermissionDenied => BleAvailability.PermissionDenied,
            _ => BleAvailability.Available,
        };
    }

    private static (BleErrorKind Kind, string Label) Classify(Exception ex)
    {
        if (ex is BluezDBusException dbus)
            return (KindForName(dbus.ErrorName), string.IsNullOrEmpty(dbus.ErrorName) ? "org.bluez.Error.Failed" : dbus.ErrorName);

        // A missing system bus socket surfaces as IOException before any D-Bus name exists.
        if (ex is IOException)
            return (BleErrorKind.BluetoothUnavailable, ex.GetType().Name);

        return (BleErrorKind.Other, ex.GetType().Name);
    }

    private static BleErrorKind KindForName(string? errorName) => errorName switch
    {
        "org.bluez.Error.NotReady" => BleErrorKind.BluetoothUnavailable,
        "org.bluez.Error.NotAvailable" => BleErrorKind.BluetoothUnavailable,
        "org.freedesktop.DBus.Error.ServiceUnknown" => BleErrorKind.BluetoothUnavailable,
        "org.freedesktop.DBus.Error.NameHasNoOwner" => BleErrorKind.BluetoothUnavailable,
        "org.freedesktop.DBus.Error.NoServer" => BleErrorKind.BluetoothUnavailable,

        "org.bluez.Error.NotAuthorized" => BleErrorKind.PermissionDenied,
        "org.bluez.Error.NotPermitted" => BleErrorKind.PermissionDenied,
        "org.freedesktop.DBus.Error.AccessDenied" => BleErrorKind.PermissionDenied,

        "org.bluez.Error.AuthenticationTimeout" => BleErrorKind.Timeout,
        "org.bluez.Error.ConnectionAttemptFailed" => BleErrorKind.Timeout,
        "org.freedesktop.DBus.Error.NoReply" => BleErrorKind.Timeout,
        "org.freedesktop.DBus.Error.Timeout" => BleErrorKind.Timeout,
        "org.freedesktop.DBus.Error.TimedOut" => BleErrorKind.Timeout,

        "org.bluez.Error.NotConnected" => BleErrorKind.DeviceDisconnected,
        "org.freedesktop.DBus.Error.Disconnected" => BleErrorKind.DeviceDisconnected,

        _ => BleErrorKind.Other,
    };

    private static Exception SafeInner(Exception ex)
    {
        if (IsMessageSafe(ex.Message))
            return ex;

        var redacted = Redact(ex.Message);
        if (ex is BluezDBusException dbus)
            return new BluezDBusException(dbus.ErrorName, redacted);
        return new Exception(redacted);
    }

    private static bool IsMessageSafe(string? message) => string.Equals(message ?? "", Redact(message), StringComparison.Ordinal);

    private static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? "";
        var path = BluezRedaction.Path(message);
        return EmbeddedMac.Replace(path, static match => BluezRedaction.Address(match.Value));
    }
}
