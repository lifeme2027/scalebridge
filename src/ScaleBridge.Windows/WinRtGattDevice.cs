using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.WinRt;

/// <summary>
/// BluetoothLEDevice + GattSession 的包装。实验性（仅按规范实现，未真机验证）。
/// <list type="bullet">
/// <item>GattSession.MaintainConnection = true：告诉系统保持连接请求。文档说明系统会无限期等待连接，
/// 真正的连接发生在第一次 GATT 操作；所以「是否已连上」只能靠第一次 GATT 调用的结果，超时由上层看门狗负责。</item>
/// <item>服务/特征一律 Uncached：已配对设备的 GATT 缓存可能过期或为空。</item>
/// <item>Dispose 同时释放 GattSession 与 BluetoothLEDevice；漏掉任何一个，系统都会继续占着连接。</item>
/// </list>
/// </summary>
internal sealed class WinRtGattDevice : IWinGattDevice
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession? _session;
    private int _disposed;
    private int _established;

    private WinRtGattDevice(BluetoothLEDevice device, GattSession? session)
    {
        _device = device;
        _session = session;
        BluetoothAddress = device.BluetoothAddress;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public ulong BluetoothAddress { get; }

    public event Action<bool>? ConnectionChanged;

    public static async Task<IWinGattDevice?> OpenAsync(ulong address, CancellationToken ct)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false);
        if (device is null) return null;

        GattSession? session = null;
        try
        {
            try
            {
                session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
                session.MaintainConnection = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MaintainConnection 只是「让系统保持连接」的优化；拿不到会话不应让整个连接失败。
                session?.Dispose();
                session = null;
            }

            return new WinRtGattDevice(device, session);
        }
        catch
        {
            session?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public async Task<WinGattResult<IReadOnlyList<IWinGattService>>> GetServicesForUuidAsync(Guid serviceUuid, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);

        if (result.Status != GattCommunicationStatus.Success)
        {
            return WinGattResult<IReadOnlyList<IWinGattService>>.Fail(WinRtMapping.MapStatus(result.Status), result.ProtocolError);
        }

        // 第一次成功的 GATT 往返 = 链路确实建立了；此后的 Disconnected 才算「掉线」。
        Volatile.Write(ref _established, 1);

        var list = new List<IWinGattService>(result.Services.Count);
        foreach (var s in result.Services) list.Add(new WinRtGattService(s));
        return WinGattResult<IReadOnlyList<IWinGattService>>.Ok(list);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        ConnectionChanged = null;
        try { _session?.Dispose(); } catch (Exception) { }
        try { _device.Dispose(); } catch (Exception) { }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        try
        {
            var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
            if (connected)
            {
                Volatile.Write(ref _established, 1);
            }
            else if (Volatile.Read(ref _established) == 0)
            {
                // 还没真正连上就收到的 Disconnected 是初始状态噪声，不是掉线。
                return;
            }

            ConnectionChanged?.Invoke(connected);
        }
        catch (Exception)
        {
        }
    }
}
