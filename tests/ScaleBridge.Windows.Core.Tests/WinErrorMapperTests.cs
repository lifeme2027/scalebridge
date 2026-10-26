using System.Runtime.InteropServices;
using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.Core.Tests;

public class WinErrorMapperTests
{
    [Fact]
    public void GattUnreachable_IsDeviceDisconnected() =>
        Assert.IsType<DeviceDisconnectedException>(WinErrorMapper.FromGattStatus(WinGattStatus.Unreachable, null, "x"));

    [Fact]
    public void GattAccessDenied_IsPermission_AndMentionsPairing()
    {
        var e = WinErrorMapper.FromGattStatus(WinGattStatus.AccessDenied, null, "Read");
        Assert.Equal(BleErrorKind.PermissionDenied, e.Kind);
        Assert.Contains("paired", e.Message);
    }

    [Theory]
    [InlineData((byte)0x05)]
    [InlineData((byte)0x08)]
    [InlineData((byte)0x0C)]
    [InlineData((byte)0x0F)]
    public void AttSecurityErrors_MapToPermission(byte code)
    {
        var e = WinErrorMapper.FromGattStatus(WinGattStatus.ProtocolError, code, "Enable notifications");
        Assert.Equal(BleErrorKind.PermissionDenied, e.Kind);
        Assert.Contains($"0x{code:X2}", e.Message);
    }

    [Fact]
    public void AttAttributeNotFound_MapsToNoSupportedService() =>
        Assert.Equal(BleErrorKind.NoSupportedService, WinErrorMapper.FromGattStatus(WinGattStatus.ProtocolError, 0x0A, "x").Kind);

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x03)]
    [InlineData((byte)0x0E)]
    public void OtherAttErrors_MapToOther_WithCode(byte code)
    {
        var e = WinErrorMapper.FromGattStatus(WinGattStatus.ProtocolError, code, "x");
        Assert.Equal(BleErrorKind.Other, e.Kind);
        Assert.Contains($"0x{code:X2}", e.Message);
    }

    [Fact]
    public void ProtocolErrorWithoutCode_IsOther() =>
        Assert.Equal(BleErrorKind.Other, WinErrorMapper.FromGattStatus(WinGattStatus.ProtocolError, null, "x").Kind);

    [Fact]
    public void UnknownStatus_IsOther() =>
        Assert.Equal(BleErrorKind.Other, WinErrorMapper.FromGattStatus(WinGattStatus.Unknown, null, "x").Kind);

    [Fact]
    public void SuccessIsNotAnError() =>
        Assert.Throws<ArgumentException>(() => WinErrorMapper.FromGattStatus(WinGattStatus.Success, null, "x"));

    [Theory]
    [InlineData(WinBluetoothError.RadioNotAvailable, BleErrorKind.BluetoothUnavailable)]
    [InlineData(WinBluetoothError.NotSupported, BleErrorKind.BluetoothUnavailable)]
    [InlineData(WinBluetoothError.TransportNotSupported, BleErrorKind.BluetoothUnavailable)]
    [InlineData(WinBluetoothError.DisabledByPolicy, BleErrorKind.PermissionDenied)]
    [InlineData(WinBluetoothError.DisabledByUser, BleErrorKind.PermissionDenied)]
    [InlineData(WinBluetoothError.ConsentRequired, BleErrorKind.PermissionDenied)]
    [InlineData(WinBluetoothError.DeviceNotConnected, BleErrorKind.DeviceDisconnected)]
    [InlineData(WinBluetoothError.ResourceInUse, BleErrorKind.Other)]
    [InlineData(WinBluetoothError.OtherError, BleErrorKind.Other)]
    public void BluetoothError_Mapping(WinBluetoothError error, BleErrorKind expected) =>
        Assert.Equal(expected, WinErrorMapper.FromBluetoothError(error, "Scan").Kind);

    [Theory]
    [InlineData(WinErrorMapper.EAccessDenied, BleErrorKind.PermissionDenied)]
    [InlineData(WinErrorMapper.ErrorNotReady, BleErrorKind.BluetoothUnavailable)]
    [InlineData(WinErrorMapper.ErrorTimeout, BleErrorKind.Timeout)]
    [InlineData(WinErrorMapper.ErrorSemTimeout, BleErrorKind.Timeout)]
    [InlineData(WinErrorMapper.ErrorDeviceNotConnected, BleErrorKind.DeviceDisconnected)]
    [InlineData(WinErrorMapper.ErrorNotFound, BleErrorKind.DeviceDisconnected)]
    [InlineData(WinErrorMapper.ErrorDeviceNotAvailable, BleErrorKind.DeviceDisconnected)]
    [InlineData(WinErrorMapper.RpcEDisconnected, BleErrorKind.DeviceDisconnected)]
    [InlineData(WinErrorMapper.EIllegalMethodCall, BleErrorKind.Other)]
    [InlineData(unchecked((int)0x80004005), BleErrorKind.Other)]
    public void HResult_Mapping(int hr, BleErrorKind expected)
    {
        var e = WinErrorMapper.FromException(new COMException("boom", hr), "Op");
        Assert.Equal(expected, e.Kind);
        Assert.Contains($"{hr:X8}", e.Message);
    }

    [Fact]
    public void ManagedExceptions_Mapping()
    {
        Assert.IsType<BleTimeoutException>(WinErrorMapper.FromException(new TimeoutException(), "x"));
        Assert.IsType<BlePermissionException>(WinErrorMapper.FromException(new UnauthorizedAccessException(), "x"));
        Assert.IsType<DeviceDisconnectedException>(WinErrorMapper.FromException(new ObjectDisposedException("x"), "x"));
    }

    [Fact]
    public void BleException_PassesThroughUnchanged()
    {
        var original = new BleTimeoutException("t");
        Assert.Same(original, WinErrorMapper.FromException(original, "x"));
    }

    [Fact]
    public void Messages_NeverContainDeviceAddress()
    {
        var e = WinErrorMapper.FromException(new COMException("AA:BB:CC:DD:EE:FF failed", WinErrorMapper.ErrorNotFound), "Connect");
        Assert.DoesNotContain("AA:BB", e.Message);
    }

    [Theory]
    [InlineData(WinRadioState.On, BleAvailability.Available)]
    [InlineData(WinRadioState.Unknown, BleAvailability.Available)]
    [InlineData(WinRadioState.Off, BleAvailability.Unavailable)]
    [InlineData(WinRadioState.Disabled, BleAvailability.Unavailable)]
    [InlineData(WinRadioState.NoAdapter, BleAvailability.Unavailable)]
    [InlineData(WinRadioState.LowEnergyNotSupported, BleAvailability.Unavailable)]
    public void RadioState_ToAvailability(WinRadioState state, BleAvailability expected) =>
        Assert.Equal(expected, WinErrorMapper.ToAvailability(state));

    [Fact]
    public void AvailabilityFromException_PermissionVsUnavailable()
    {
        Assert.Equal(BleAvailability.PermissionDenied, WinErrorMapper.ToAvailability(new UnauthorizedAccessException()));
        Assert.Equal(BleAvailability.Unavailable, WinErrorMapper.ToAvailability(new InvalidOperationException()));
    }
}
