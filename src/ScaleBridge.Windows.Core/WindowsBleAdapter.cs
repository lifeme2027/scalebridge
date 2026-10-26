using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows;

/// <summary>
/// Windows 真实蓝牙适配器（WinRT）。<b>实验性（仅按规范实现，未真机验证）</b>：
/// 作者没有 Windows 蓝牙硬件，WinRT 外壳只在 CI 里做过编译检查。
/// 本类只依赖 <see cref="IWinBluetoothPlatform"/>，因此全部逻辑可在任何系统上用假平台测试。
/// </summary>
public sealed class WindowsBleAdapter : IBleAdapter
{
    private readonly IWinBluetoothPlatform _platform;
    private readonly WindowsBleOptions _options;
    private readonly TimeProvider _time;

    public WindowsBleAdapter(IWinBluetoothPlatform platform, WindowsBleOptions? options = null, TimeProvider? time = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _options = options ?? new WindowsBleOptions();
        _time = time ?? TimeProvider.System;
    }

    public string Id => "windows";

    public string DisplayName => "Windows Bluetooth (WinRT, experimental)";

    public bool IsDemo => false;

    public async Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await WinWatchdog.RunAsync(_platform.GetRadioStateAsync, _options.AvailabilityTimeout, _time,
                "Query Bluetooth radio", ct).ConfigureAwait(false);
            return WinErrorMapper.ToAvailability(state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WinErrorMapper.ToAvailability(ex);
        }
    }

    public async IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await EnsureRadioOnAsync(ct).ConfigureAwait(false);

        var filter = options.ServiceFilter is { Count: > 0 } f ? f : null;
        var channel = Channel.CreateBounded<AdvertisedDevice>(new BoundedChannelOptions(Math.Max(1, _options.ScanBuffer))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var cache = new WinAdvertisementCache(_options.AdvertisementCacheCapacity);
        var cacheLock = new object();
        var stopRequested = 0;

        IWinAdvertisementWatcher watcher;
        try
        {
            watcher = _platform.CreateWatcher();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WinErrorMapper.FromException(ex, "Create scanner");
        }

        void OnReceived(WinAdvertisement ad)
        {
            try
            {
                AdvertisedDevice dev;
                lock (cacheLock) dev = cache.Merge(ad, _time.GetLocalNow());
                if (filter is not null && !dev.ServiceUuids.Any(filter.Contains)) return;
                channel.Writer.TryWrite(dev);
            }
            catch
            {
                // WinRT 回调线程上的异常不能外泄
            }
        }

        void OnStopped(WinBluetoothError error)
        {
            if (error == WinBluetoothError.Success || Volatile.Read(ref stopRequested) == 1)
                channel.Writer.TryComplete();
            else
                channel.Writer.TryComplete(WinErrorMapper.FromBluetoothError(error, "Scan"));
        }

        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            try
            {
                watcher.Start();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw WinErrorMapper.FromException(ex, "Start scan");
            }

            var reader = channel.Reader;
            while (true)
            {
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                if (!more) yield break;
                while (reader.TryRead(out var d)) yield return d;
            }
        }
        finally
        {
            Volatile.Write(ref stopRequested, 1);
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            try { watcher.Stop(); } catch { /* 已停止或已被系统中止 */ }
            try { watcher.Dispose(); } catch { }
            channel.Writer.TryComplete();
        }
    }

    public async Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!WinAddress.TryParse(deviceId, out var address))
            throw new BleException(BleErrorKind.Other, "The device id is not a Bluetooth address.");
        var limit = timeout is { } t && t > TimeSpan.Zero ? t : _options.DefaultConnectTimeout;

        var device = await WinWatchdog.RunAsync(
            async token =>
            {
                var state = await _platform.GetRadioStateAsync(token).ConfigureAwait(false);
                if (WinErrorMapper.ToAvailability(state) != BleAvailability.Available)
                    throw WinErrorMapper.FromRadio(state);
                return await _platform.OpenDeviceAsync(address, token).ConfigureAwait(false);
            },
            limit, _time, "Connect", ct,
            onLateResult: late => { try { late?.Dispose(); } catch { } }).ConfigureAwait(false);

        if (device is null)
            throw new DeviceDisconnectedException("Connect: Windows does not know this device (not found).");

        var connection = new WindowsBleConnection(WinAddress.Format(address), device, _options, _time);
        if (ct.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        return connection;
    }

    private async Task EnsureRadioOnAsync(CancellationToken ct)
    {
        var state = await WinWatchdog.RunAsync(_platform.GetRadioStateAsync, _options.AvailabilityTimeout, _time,
            "Query Bluetooth radio", ct).ConfigureAwait(false);
        if (WinErrorMapper.ToAvailability(state) != BleAvailability.Available)
            throw WinErrorMapper.FromRadio(state);
    }
}
