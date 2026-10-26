using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using CoreProps = ScaleBridge.Core.Ble.GattCharacteristicProperties;
using WinProps = Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.WinRt;

/// <summary>WinRT 枚举 ↔ 平台无关枚举的翻译。刻意只用名字映射，不依赖数值（Core 与 WinRT 的特征属性位值并不相同）。</summary>
internal static class WinRtMapping
{
    public static WinGattStatus MapStatus(GattCommunicationStatus status) => status switch
    {
        GattCommunicationStatus.Success => WinGattStatus.Success,
        GattCommunicationStatus.Unreachable => WinGattStatus.Unreachable,
        GattCommunicationStatus.ProtocolError => WinGattStatus.ProtocolError,
        GattCommunicationStatus.AccessDenied => WinGattStatus.AccessDenied,
        _ => WinGattStatus.Unknown,
    };

    public static WinRadioState MapRadio(RadioState state) => state switch
    {
        RadioState.On => WinRadioState.On,
        RadioState.Off => WinRadioState.Off,
        RadioState.Disabled => WinRadioState.Disabled,
        _ => WinRadioState.Unknown,
    };

    public static WinBluetoothError MapError(BluetoothError error) => error switch
    {
        BluetoothError.Success => WinBluetoothError.Success,
        BluetoothError.RadioNotAvailable => WinBluetoothError.RadioNotAvailable,
        BluetoothError.ResourceInUse => WinBluetoothError.ResourceInUse,
        BluetoothError.DeviceNotConnected => WinBluetoothError.DeviceNotConnected,
        BluetoothError.OtherError => WinBluetoothError.OtherError,
        BluetoothError.DisabledByPolicy => WinBluetoothError.DisabledByPolicy,
        BluetoothError.NotSupported => WinBluetoothError.NotSupported,
        BluetoothError.DisabledByUser => WinBluetoothError.DisabledByUser,
        BluetoothError.ConsentRequired => WinBluetoothError.ConsentRequired,
        BluetoothError.TransportNotSupported => WinBluetoothError.TransportNotSupported,
        _ => WinBluetoothError.OtherError,
    };

    public static CoreProps MapProperties(WinProps props)
    {
        var result = CoreProps.None;
        if (props.HasFlag(WinProps.Read)) result |= CoreProps.Read;
        if (props.HasFlag(WinProps.Write)) result |= CoreProps.Write;
        if (props.HasFlag(WinProps.WriteWithoutResponse)) result |= CoreProps.WriteWithoutResponse;
        if (props.HasFlag(WinProps.Notify)) result |= CoreProps.Notify;
        if (props.HasFlag(WinProps.Indicate)) result |= CoreProps.Indicate;
        return result;
    }

    public static GattClientCharacteristicConfigurationDescriptorValue MapCccd(WinCccdValue value) => value switch
    {
        WinCccdValue.Notify => GattClientCharacteristicConfigurationDescriptorValue.Notify,
        WinCccdValue.Indicate => GattClientCharacteristicConfigurationDescriptorValue.Indicate,
        _ => GattClientCharacteristicConfigurationDescriptorValue.None,
    };
}
