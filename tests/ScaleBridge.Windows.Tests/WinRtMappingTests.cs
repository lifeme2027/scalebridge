using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using ScaleBridge.Windows.Platform;
using ScaleBridge.Windows.WinRt;
using CoreProps = ScaleBridge.Core.Ble.GattCharacteristicProperties;
using WinProps = Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties;

namespace ScaleBridge.Windows.Tests;

/// <summary>枚举映射表的测试（纯函数，不需要蓝牙硬件）。</summary>
public class WinRtMappingTests
{
    [Theory]
    [InlineData(GattCommunicationStatus.Success, WinGattStatus.Success)]
    [InlineData(GattCommunicationStatus.Unreachable, WinGattStatus.Unreachable)]
    [InlineData(GattCommunicationStatus.ProtocolError, WinGattStatus.ProtocolError)]
    [InlineData(GattCommunicationStatus.AccessDenied, WinGattStatus.AccessDenied)]
    public void GattStatus_IsMapped(GattCommunicationStatus input, WinGattStatus expected) =>
        Assert.Equal(expected, WinRtMapping.MapStatus(input));

    [Fact]
    public void GattStatus_UnknownValue_IsUnknown() => Assert.Equal(WinGattStatus.Unknown, WinRtMapping.MapStatus((GattCommunicationStatus)99));

    [Theory]
    [InlineData(RadioState.On, WinRadioState.On)]
    [InlineData(RadioState.Off, WinRadioState.Off)]
    [InlineData(RadioState.Disabled, WinRadioState.Disabled)]
    [InlineData(RadioState.Unknown, WinRadioState.Unknown)]
    public void RadioState_IsMapped(RadioState input, WinRadioState expected) =>
        Assert.Equal(expected, WinRtMapping.MapRadio(input));

    [Fact]
    public void BluetoothError_AllValuesMapToTheSameNumericValue()
    {
        // WinBluetoothError 的数值刻意与 WinRT 的 BluetoothError 一致（文档：0 Success … 9 TransportNotSupported）。
        foreach (var e in Enum.GetValues<BluetoothError>())
        {
            Assert.Equal((int)e, (int)WinRtMapping.MapError(e));
        }
    }

    [Fact]
    public void BluetoothError_UnknownValue_IsOtherError() =>
        Assert.Equal(WinBluetoothError.OtherError, WinRtMapping.MapError((BluetoothError)1234));

    [Fact]
    public void Properties_MapByNameNotByNumber()
    {
        // 两边的位值不同（Core: Notify=8；WinRT: Notify=0x10），所以必须按名字翻译。
        Assert.Equal(CoreProps.Notify, WinRtMapping.MapProperties(WinProps.Notify));
        Assert.Equal(CoreProps.Indicate, WinRtMapping.MapProperties(WinProps.Indicate));
        Assert.Equal(CoreProps.Read | CoreProps.Write, WinRtMapping.MapProperties(WinProps.Read | WinProps.Write));
        Assert.Equal(CoreProps.WriteWithoutResponse, WinRtMapping.MapProperties(WinProps.WriteWithoutResponse));
        Assert.Equal(CoreProps.None, WinRtMapping.MapProperties(WinProps.None));
        Assert.Equal(CoreProps.None, WinRtMapping.MapProperties(WinProps.Broadcast));
    }

    [Theory]
    [InlineData(WinCccdValue.None, GattClientCharacteristicConfigurationDescriptorValue.None)]
    [InlineData(WinCccdValue.Notify, GattClientCharacteristicConfigurationDescriptorValue.Notify)]
    [InlineData(WinCccdValue.Indicate, GattClientCharacteristicConfigurationDescriptorValue.Indicate)]
    public void Cccd_IsMapped(WinCccdValue input, GattClientCharacteristicConfigurationDescriptorValue expected) =>
        Assert.Equal(expected, WinRtMapping.MapCccd(input));
}
