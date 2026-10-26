using Windows.Devices.Bluetooth.Advertisement;
using Windows.Security.Cryptography;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.WinRt;

/// <summary>主动扫描的 BluetoothLEAdvertisementWatcher 包装（主动扫描才会收到 Scan Response 里的名字/服务）。</summary>
internal sealed class WinRtAdvertisementWatcher : IWinAdvertisementWatcher
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private int _disposed;

    public WinRtAdvertisementWatcher()
    {
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    public event Action<WinAdvertisement>? Received;

    public event Action<WinBluetoothError>? Stopped;

    public void Start() => _watcher.Start();

    public void Stop()
    {
        try
        {
            _watcher.Stop();
        }
        catch (Exception)
        {
            // 已经停止或已被系统中止时 Stop 可能抛异常；停止是尽力而为，不影响清理。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        Stop();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // WinRT 回调里抛异常会直接拖垮进程，所以这里一律吞掉，只丢这一条广播。
        try
        {
            var handler = Received;
            if (handler is null) return;
            var adv = args.Advertisement;

            var services = new List<Guid>(adv.ServiceUuids.Count);
            foreach (var uuid in adv.ServiceUuids) services.Add(uuid);

            var manufacturer = new List<WinManufacturerData>(adv.ManufacturerData.Count);
            foreach (var m in adv.ManufacturerData)
            {
                // IBuffer 可能被复用：立即拷贝成独立数组。
                CryptographicBuffer.CopyToByteArray(m.Data, out var bytes);
                manufacturer.Add(new WinManufacturerData(m.CompanyId, bytes));
            }

            var name = string.IsNullOrEmpty(adv.LocalName) ? null : adv.LocalName;
            handler(new WinAdvertisement(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, services, manufacturer));
        }
        catch (Exception)
        {
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        try
        {
            Stopped?.Invoke(WinRtMapping.MapError(args.Error));
        }
        catch (Exception)
        {
        }
    }
}
