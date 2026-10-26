using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using ScaleBridge.Windows.Platform;
using CoreProps = ScaleBridge.Core.Ble.GattCharacteristicProperties;

namespace ScaleBridge.Windows.WinRt;

/// <summary>GattCharacteristic 包装。实验性（仅按规范实现，未真机验证）。</summary>
internal sealed class WinRtGattCharacteristic : IWinGattCharacteristic
{
    private readonly GattCharacteristic _native;
    private readonly object _gate = new();
    private WinValueChangedHandler? _handlers;
    private bool _hooked;

    public WinRtGattCharacteristic(GattCharacteristic native)
    {
        _native = native;
        Uuid = native.Uuid;
        Properties = WinRtMapping.MapProperties(native.CharacteristicProperties);
    }

    public Guid Uuid { get; }

    public CoreProps Properties { get; }

    /// <summary>首个订阅者加入时才挂接系统事件，最后一个退出就摘除。</summary>
    public event WinValueChangedHandler? ValueChanged
    {
        add
        {
            lock (_gate)
            {
                _handlers += value;
                if (!_hooked)
                {
                    _native.ValueChanged += OnNativeValueChanged;
                    _hooked = true;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _handlers -= value;
                if (_handlers is null && _hooked)
                {
                    _native.ValueChanged -= OnNativeValueChanged;
                    _hooked = false;
                }
            }
        }
    }

    public async Task<WinGattResult<byte[]>> ReadAsync(CancellationToken ct)
    {
        var result = await _native.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return WinGattResult<byte[]>.Fail(WinRtMapping.MapStatus(result.Status), result.ProtocolError);
        }

        if (result.Value is null) return WinGattResult<byte[]>.Ok(Array.Empty<byte>());
        CryptographicBuffer.CopyToByteArray(result.Value, out var bytes);
        return WinGattResult<byte[]>.Ok(bytes);
    }

    public async Task<WinGattResult<bool>> WriteCccdAsync(WinCccdValue value, CancellationToken ct)
    {
        var result = await _native
            .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(WinRtMapping.MapCccd(value))
            .AsTask(ct).ConfigureAwait(false);
        return result.Status == GattCommunicationStatus.Success
            ? WinGattResult<bool>.Ok(true)
            : WinGattResult<bool>.Fail(WinRtMapping.MapStatus(result.Status), result.ProtocolError);
    }

    /// <summary>服务被释放时调用：摘掉系统事件并丢弃所有处理器。</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            _handlers = null;
            if (_hooked)
            {
                _native.ValueChanged -= OnNativeValueChanged;
                _hooked = false;
            }
        }
    }

    private void OnNativeValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            WinValueChangedHandler? handler;
            lock (_gate) handler = _handlers;
            if (handler is null) return;

            // IBuffer 只在回调期间有效（可能被复用），必须在这里同步拷贝。
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
            handler(bytes);
        }
        catch (Exception)
        {
            // 回调里的异常会拖垮进程；丢掉这一条通知。
        }
    }
}
