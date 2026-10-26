using System.Runtime.CompilerServices;
using ScaleBridge.Core.Ble;
using ScaleBridge.Linux.Bluez;

namespace ScaleBridge.Linux;

/// <summary>
/// Linux BlueZ（经 D-Bus）的 <see cref="IBleAdapter"/> 实现。
/// 实验性（仅按规范/公开资料实现，未真机验证）。
/// 每次可用性检查、扫描、连接各自打开一条系统总线连接；连接对象持有自己的总线并在释放时关闭。
/// </summary>
public sealed class BluezBleAdapter : IBleAdapter
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<IBluezBus>> _busFactory;
    private readonly TimeProvider _time;
    private readonly string? _preferredAdapter;
    private readonly bool _platformSupported;

    internal BluezBleAdapter(
        Func<CancellationToken, Task<IBluezBus>> busFactory,
        TimeProvider? time = null,
        string? preferredAdapter = null,
        bool? platformSupported = null)
    {
        ArgumentNullException.ThrowIfNull(busFactory);
        _busFactory = busFactory;
        _time = time ?? TimeProvider.System;
        _preferredAdapter = string.IsNullOrWhiteSpace(preferredAdapter) ? null : preferredAdapter;
        _platformSupported = platformSupported ?? OperatingSystem.IsLinux();
    }

    /// <inheritdoc />
    public string Id => "linux";

    /// <inheritdoc />
    public string DisplayName => "Linux (BlueZ, experimental)";

    /// <inheritdoc />
    public bool IsDemo => false;

    /// <summary>无 Linux、无 BlueZ 服务、无适配器或适配器未上电都报告 Unavailable；D-Bus 拒绝访问报告 PermissionDenied。</summary>
    public async Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        if (!_platformSupported)
            return BleAvailability.Unavailable;

        IBluezBus? bus = null;
        try
        {
            bus = await _busFactory(ct).ConfigureAwait(false);
            var objects = await bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
            var adapterPath = BluezObjectModel.FindAdapterPath(objects, _preferredAdapter);
            if (adapterPath is null)
                return BleAvailability.Unavailable;

            var adapter = objects.FirstOrDefault(o => string.Equals(o.Path, adapterPath, StringComparison.Ordinal));
            if (adapter?.Interfaces is not null
                && adapter.Interfaces.TryGetValue(BluezNames.Adapter1, out var props)
                && BluezProps.TryGetBool(props, "Powered", out var powered)
                && !powered)
            {
                return BleAvailability.Unavailable;
            }

            return BleAvailability.Available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BluezErrorMapper.ToAvailability(ex) == BleAvailability.Available
                ? BleAvailability.Unavailable
                : BluezErrorMapper.ToAvailability(ex);
        }
        finally
        {
            await DisposeQuietlyAsync(bus).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AdvertisedDevice> ScanAsync(
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsurePlatform();

        IBluezBus? bus = null;
        try
        {
            string adapterPath;
            try
            {
                bus = await _busFactory(ct).ConfigureAwait(false);
                adapterPath = await ResolveAdapterAsync(bus, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex) when (ex is not BleException)
            {
                throw BluezErrorMapper.Map(ex, "scan");
            }

            await foreach (var device in BluezScanner.ScanAsync(bus, adapterPath, options, _time, ct).ConfigureAwait(false))
                yield return device;
        }
        finally
        {
            await DisposeQuietlyAsync(bus).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        EnsurePlatform();

        var effective = timeout is { } t && t > TimeSpan.Zero ? t : DefaultConnectTimeout;
        IBluezBus? bus = null;
        string adapterPath;
        try
        {
            bus = await _busFactory(ct).ConfigureAwait(false);
            adapterPath = await ResolveAdapterAsync(bus, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposeQuietlyAsync(bus).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(bus).ConfigureAwait(false);
            throw ex as BleException ?? BluezErrorMapper.Map(ex, "connect");
        }

        // 总线所有权从此交给 BluezConnector：失败时它会关闭总线，成功时交给连接对象。
        return await BluezConnector.ConnectAsync(bus, adapterPath, deviceId, effective, _time, ownsBus: true, ct)
            .ConfigureAwait(false);
    }

    private async Task<string> ResolveAdapterAsync(IBluezBus bus, CancellationToken ct)
    {
        var objects = await bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
        return BluezObjectModel.FindAdapterPath(objects, _preferredAdapter)
            ?? throw new BluetoothUnavailableException("no Bluetooth adapter found");
    }

    private void EnsurePlatform()
    {
        if (!_platformSupported)
            throw new BluetoothUnavailableException("BlueZ is only available on Linux");
    }

    private static async Task DisposeQuietlyAsync(IBluezBus? bus)
    {
        if (bus is null)
            return;
        try
        {
            await bus.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}
