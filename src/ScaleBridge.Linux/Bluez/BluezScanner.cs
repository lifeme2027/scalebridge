using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>通过 IBluezBus 扫描 LE 广播并产出 AdvertisedDevice。</summary>
internal static class BluezScanner
{
    /// <summary>订阅变更后再启动发现。取消时安静结束；连接断开时抛出映射后的 BleException。</summary>
    public static async IAsyncEnumerable<AdvertisedDevice> ScanAsync(
        IBluezBus bus,
        string adapterPath,
        ScanOptions options,
        TimeProvider time,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        var channel = Channel.CreateUnbounded<AdvertisedDevice>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var cache = new BluezAdvertisementCache(adapterPath);
        var filter = options.ServiceFilter;
        IDisposable? added = null;
        IDisposable? removed = null;
        IDisposable? properties = null;
        var startedByUs = false;
        Exception? fault = null;

        void Enqueue(string path)
        {
            if (!cache.Matches(path, filter))
                return;
            if (!cache.TryBuild(path, time.GetLocalNow(), out var device))
                return;
            channel.Writer.TryWrite(device);
        }

        void OnAdded(BluezObject obj)
        {
            if (cache.Apply(obj))
                Enqueue(obj.Path);
        }

        void OnRemoved(string path, IReadOnlyList<string> interfaces)
        {
            if (interfaces is null)
                return;
            for (var i = 0; i < interfaces.Count; i++)
            {
                if (string.Equals(interfaces[i], BluezNames.Device1, StringComparison.Ordinal))
                {
                    cache.Remove(path);
                    return;
                }
            }
        }

        void OnChanged(BluezPropertiesChanged change)
        {
            if (cache.Apply(change))
                Enqueue(change.Path);
        }

        void OnClosed(Exception? error)
        {
            channel.Writer.TryComplete(MapClosed(error));
        }

        bus.ConnectionClosed += OnClosed;
        var stopReading = false;
        try
        {
            try
            {
                added = await bus.WatchInterfacesAddedAsync(OnAdded, ct).ConfigureAwait(false);
                removed = await bus.WatchInterfacesRemovedAsync(OnRemoved, ct).ConfigureAwait(false);
                properties = await bus.WatchPropertiesChangedAsync(null, OnChanged, ct).ConfigureAwait(false);

                var uuids = BuildFilterUuids(options.ServiceFilter);
                try
                {
                    await bus.SetDiscoveryFilterAsync(
                        adapterPath,
                        new BluezDiscoveryFilter(uuids, "le", true),
                        ct).ConfigureAwait(false);
                }
                catch (BluezDBusException ex) when (IsIgnorableFilterError(ex))
                {
                }

                try
                {
                    await bus.StartDiscoveryAsync(adapterPath, ct).ConfigureAwait(false);
                    startedByUs = true;
                }
                catch (BluezDBusException ex) when (string.Equals(ex.ErrorName, "org.bluez.Error.InProgress", StringComparison.Ordinal))
                {
                    startedByUs = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw BluezErrorMapper.Map(ex, "scan");
                }

                var objects = await bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
                for (var i = 0; i < objects.Count; i++)
                    OnAdded(objects[i]);
            }
            catch (OperationCanceledException)
            {
                stopReading = true;
            }
            catch (Exception ex) when (ex is not BleException)
            {
                throw BluezErrorMapper.Map(ex, "scan");
            }

            while (!stopReading)
            {
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException ex)
                {
                    fault = ex.InnerException;
                    break;
                }

                if (!more)
                    break;

                while (channel.Reader.TryRead(out var device))
                    yield return device;
            }
        }
        finally
        {
            bus.ConnectionClosed -= OnClosed;
            added?.Dispose();
            removed?.Dispose();
            properties?.Dispose();
            if (startedByUs)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await bus.StopDiscoveryAsync(adapterPath, stop.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        if (fault is not null)
            throw fault;
    }

    private static string[] BuildFilterUuids(IReadOnlyList<Guid>? filter)
    {
        if (filter is null || filter.Count == 0)
            return [];

        var uuids = new string[filter.Count];
        for (var i = 0; i < filter.Count; i++)
            uuids[i] = filter[i].ToString("D");
        return uuids;
    }

    private static bool IsIgnorableFilterError(BluezDBusException ex) =>
        ex.ErrorName is "org.bluez.Error.NotSupported"
            or "org.bluez.Error.InvalidArguments"
            or "org.bluez.Error.Failed";

    private static Exception MapClosed(Exception? error)
    {
        if (error is null)
            return new BluetoothUnavailableException("scan failed (connection closed).");
        try
        {
            return BluezErrorMapper.Map(error, "scan");
        }
        catch (ArgumentException)
        {
            return new BluetoothUnavailableException("scan failed (connection closed).");
        }
    }
}
