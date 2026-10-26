using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.WinRt;

/// <summary>
/// GattDeviceService 包装。它<b>强引用</b>自己发出的特征对象，直到服务被释放：
/// WinRT 的特征对象一旦被 GC 回收，已订阅的通知会悄悄停止（没有任何错误）。
/// </summary>
internal sealed class WinRtGattService : IWinGattService
{
    private readonly GattDeviceService _service;
    private readonly List<WinRtGattCharacteristic> _characteristics = new();
    private int _disposed;

    public WinRtGattService(GattDeviceService service)
    {
        _service = service;
        Uuid = service.Uuid;
    }

    public Guid Uuid { get; }

    public async Task<WinGattResult<IReadOnlyList<IWinGattCharacteristic>>> GetCharacteristicsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return WinGattResult<IReadOnlyList<IWinGattCharacteristic>>.Fail(WinRtMapping.MapStatus(result.Status), result.ProtocolError);
        }

        var list = new List<IWinGattCharacteristic>(result.Characteristics.Count);
        lock (_characteristics)
        {
            foreach (var c in result.Characteristics)
            {
                var wrapped = new WinRtGattCharacteristic(c);
                _characteristics.Add(wrapped);
                list.Add(wrapped);
            }
        }

        return WinGattResult<IReadOnlyList<IWinGattCharacteristic>>.Ok(list);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_characteristics)
        {
            foreach (var c in _characteristics) c.Detach();
            _characteristics.Clear();
        }

        try { _service.Dispose(); } catch (Exception) { }
    }
}
