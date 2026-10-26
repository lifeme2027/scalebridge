using ScaleBridge.Core.Ble;
using ScaleBridge.Linux.Bluez;

namespace ScaleBridge.Linux.Tests;

/// <summary>
/// IOException 映射为 BluetoothUnavailableException：连接系统总线失败时没有 D-Bus 错误名，只剩套接字 IO 异常。
/// 超时、断开和未知错误不把适配器标成不可用（ToAvailability 仍为 Available）。
/// </summary>
public class BluezErrorMapperTests
{
    [Theory]
    [InlineData("org.bluez.Error.NotReady", BleErrorKind.BluetoothUnavailable)]
    [InlineData("org.bluez.Error.NotAvailable", BleErrorKind.BluetoothUnavailable)]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", BleErrorKind.BluetoothUnavailable)]
    [InlineData("org.freedesktop.DBus.Error.NameHasNoOwner", BleErrorKind.BluetoothUnavailable)]
    [InlineData("org.freedesktop.DBus.Error.NoServer", BleErrorKind.BluetoothUnavailable)]
    [InlineData("org.bluez.Error.NotAuthorized", BleErrorKind.PermissionDenied)]
    [InlineData("org.bluez.Error.NotPermitted", BleErrorKind.PermissionDenied)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", BleErrorKind.PermissionDenied)]
    [InlineData("org.bluez.Error.AuthenticationTimeout", BleErrorKind.Timeout)]
    [InlineData("org.bluez.Error.ConnectionAttemptFailed", BleErrorKind.Timeout)]
    [InlineData("org.freedesktop.DBus.Error.NoReply", BleErrorKind.Timeout)]
    [InlineData("org.freedesktop.DBus.Error.Timeout", BleErrorKind.Timeout)]
    [InlineData("org.freedesktop.DBus.Error.TimedOut", BleErrorKind.Timeout)]
    [InlineData("org.bluez.Error.NotConnected", BleErrorKind.DeviceDisconnected)]
    [InlineData("org.freedesktop.DBus.Error.Disconnected", BleErrorKind.DeviceDisconnected)]
    [InlineData("org.bluez.Error.AuthenticationFailed", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.AuthenticationCanceled", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.AuthenticationRejected", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.Failed", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.InProgress", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.AlreadyConnected", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.DoesNotExist", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.NotSupported", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.InvalidArguments", BleErrorKind.Other)]
    [InlineData("org.bluez.Error.AlreadyExists", BleErrorKind.Other)]
    public void Map_KnownName_UsesKindAndOmitsDbusText(string errorName, BleErrorKind kind)
    {
        var dbus = new BluezDBusException(errorName, "adapter detail");
        var mapped = BluezErrorMapper.Map(dbus, "Connect");

        Assert.Equal(kind, mapped.Kind);
        Assert.Equal(ExpectedType(kind), mapped.GetType());
        Assert.Contains("Connect", mapped.Message);
        Assert.Contains(errorName, mapped.Message);
        Assert.DoesNotContain("adapter detail", mapped.Message);
        Assert.Same(dbus, mapped.InnerException);
    }

    [Fact]
    public void Map_UnknownDbusName_IsGenericFailure()
    {
        var mapped = BluezErrorMapper.Map(new BluezDBusException("org.bluez.Error.Weird", "x"), "Read");

        Assert.IsType<BleException>(mapped);
        Assert.Equal(BleErrorKind.Other, mapped.Kind);
        Assert.Contains("org.bluez.Error.Weird", mapped.Message);
        Assert.Contains("Read", mapped.Message);
    }

    [Fact]
    public void Map_IoException_IsBluetoothUnavailable()
    {
        var io = new IOException("no socket");
        var mapped = BluezErrorMapper.Map(io, "Open");

        Assert.IsType<BluetoothUnavailableException>(mapped);
        Assert.Contains("Open", mapped.Message);
        Assert.Contains(nameof(IOException), mapped.Message);
        Assert.DoesNotContain("no socket", mapped.Message);
        Assert.Same(io, mapped.InnerException);
        Assert.True(BluezErrorMapper.IsBluezUnavailable(io));
        Assert.Equal(BleAvailability.Unavailable, BluezErrorMapper.ToAvailability(io));
    }

    [Fact]
    public void Map_OtherException_IsGenericFailure()
    {
        var mapped = BluezErrorMapper.Map(new InvalidOperationException("bad state"), "Scan");

        Assert.IsType<BleException>(mapped);
        Assert.Equal(BleErrorKind.Other, mapped.Kind);
        Assert.Contains(nameof(InvalidOperationException), mapped.Message);
        Assert.DoesNotContain("bad state", mapped.Message);
    }

    [Fact]
    public void Map_BleException_IsReturnedAsIs()
    {
        var ble = new BleTimeoutException("already mapped");
        Assert.Same(ble, BluezErrorMapper.Map(ble, "Read"));
    }

    [Fact]
    public void Map_OperationCanceled_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BluezErrorMapper.Map(new OperationCanceledException(), "Connect"));
    }

    [Fact]
    public void Map_RedactsAddressInMessageAndInner()
    {
        const string raw = "see /org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF and AA:BB:CC:DD:EE:FF";
        var dbus = new BluezDBusException("org.bluez.Error.Failed", raw);
        var mapped = BluezErrorMapper.Map(dbus, "Connect");

        AssertNoLastOctets(mapped.Message);
        var inner = Assert.IsType<BluezDBusException>(mapped.InnerException);
        Assert.NotSame(dbus, inner);
        Assert.Equal("org.bluez.Error.Failed", inner.ErrorName);
        AssertNoLastOctets(inner.Message);
        Assert.Contains("dev_AA_BB_CC_DD_**_**", inner.Message);
        Assert.Contains("AA:BB:CC:DD:**:**", inner.Message);
        Assert.DoesNotContain(raw, mapped.ToString());
    }

    [Theory]
    [InlineData("org.bluez.Error.NotReady", true, BleAvailability.Unavailable)]
    [InlineData("org.bluez.Error.NotAvailable", true, BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", true, BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.NameHasNoOwner", true, BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.NoServer", true, BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", false, BleAvailability.PermissionDenied)]
    [InlineData("org.bluez.Error.NotAuthorized", false, BleAvailability.PermissionDenied)]
    [InlineData("org.bluez.Error.NotPermitted", false, BleAvailability.PermissionDenied)]
    [InlineData("org.freedesktop.DBus.Error.Timeout", false, BleAvailability.Available)]
    [InlineData("org.freedesktop.DBus.Error.TimedOut", false, BleAvailability.Available)]
    [InlineData("org.freedesktop.DBus.Error.NoReply", false, BleAvailability.Available)]
    [InlineData("org.bluez.Error.ConnectionAttemptFailed", false, BleAvailability.Available)]
    [InlineData("org.bluez.Error.NotConnected", false, BleAvailability.Available)]
    [InlineData("org.freedesktop.DBus.Error.Disconnected", false, BleAvailability.Available)]
    [InlineData("org.bluez.Error.Failed", false, BleAvailability.Available)]
    [InlineData("org.bluez.Error.Unknown", false, BleAvailability.Available)]
    public void ToAvailability_AndUnavailableFlag(string errorName, bool unavailable, BleAvailability availability)
    {
        var ex = new BluezDBusException(errorName, "plain");
        Assert.Equal(unavailable, BluezErrorMapper.IsBluezUnavailable(ex));
        Assert.Equal(availability, BluezErrorMapper.ToAvailability(ex));
    }

    [Fact]
    public void ToAvailability_MappedBleExceptions()
    {
        Assert.Equal(BleAvailability.Unavailable, BluezErrorMapper.ToAvailability(new BluetoothUnavailableException()));
        Assert.Equal(BleAvailability.PermissionDenied, BluezErrorMapper.ToAvailability(new BlePermissionException()));
        Assert.Equal(BleAvailability.Available, BluezErrorMapper.ToAvailability(new BleTimeoutException()));
        Assert.False(BluezErrorMapper.IsBluezUnavailable(new BleTimeoutException()));
        Assert.True(BluezErrorMapper.IsBluezUnavailable(new BluetoothUnavailableException()));
        Assert.False(BluezErrorMapper.IsBluezUnavailable(new OperationCanceledException()));
        Assert.Equal(BleAvailability.Available, BluezErrorMapper.ToAvailability(new OperationCanceledException()));
    }

    private static Type ExpectedType(BleErrorKind kind) => kind switch
    {
        BleErrorKind.BluetoothUnavailable => typeof(BluetoothUnavailableException),
        BleErrorKind.PermissionDenied => typeof(BlePermissionException),
        BleErrorKind.Timeout => typeof(BleTimeoutException),
        BleErrorKind.DeviceDisconnected => typeof(DeviceDisconnectedException),
        _ => typeof(BleException),
    };

    private static void AssertNoLastOctets(string text)
    {
        Assert.DoesNotContain("EE_FF", text);
        Assert.DoesNotContain("EE:FF", text);
        Assert.DoesNotContain("ee_ff", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ee:ff", text, StringComparison.OrdinalIgnoreCase);
    }
}
