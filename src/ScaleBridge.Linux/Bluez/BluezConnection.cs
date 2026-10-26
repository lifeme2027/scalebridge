using System.Runtime.CompilerServices;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>一条 BlueZ GATT 连接。掉线取消内部令牌，订阅流安静结束。</summary>
internal sealed class BluezConnection : IBleConnection
{
    private readonly IBluezBus _bus;
    private readonly string _devicePath;
    private readonly TimeProvider _time;
    private readonly bool _ownsBus;
    private readonly IDisposable _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<Task> _subscriptions = [];
    private ConnectionState _state = ConnectionState.Connected;
    private bool _disposed;

    internal BluezConnection(
        IBluezBus bus,
        string devicePath,
        string deviceId,
        TimeProvider time,
        bool ownsBus,
        IDisposable watcher)
    {
        _bus = bus;
        _devicePath = devicePath;
        DeviceId = deviceId;
        _time = time;
        _ownsBus = ownsBus;
        _watcher = watcher;
        _bus.ConnectionClosed += OnConnectionClosed;
    }

    public string DeviceId { get; }

    public ConnectionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public event EventHandler<ConnectionStateChange>? StateChanged;

    public async Task<IReadOnlyList<GattServiceInfo>> DiscoverServicesAsync(CancellationToken ct = default)
    {
        EnsureUsable();
        try
        {
            var objects = await _bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
            EnsureUsable();
            return BluezObjectModel.GetServices(objects, _devicePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BluezErrorMapper.Map(ex, "discover");
        }
    }

    public async Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct = default)
    {
        EnsureUsable();
        try
        {
            var path = await FindCharacteristicPathAsync(service, characteristic, ct).ConfigureAwait(false);
            var bytes = await _bus.ReadValueAsync(path, ct).ConfigureAwait(false);
            return bytes is null ? [] : (byte[])bytes.Clone();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BluezErrorMapper.Map(ex, "read");
        }
    }

    public async IAsyncEnumerable<GattNotification> SubscribeAsync(
        Guid service,
        Guid characteristic,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureUsable();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || _state != ConnectionState.Connected)
                throw new DeviceDisconnectedException("The device disconnected.");
            _subscriptions.Add(done.Task);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            string path;
            try
            {
                path = await FindCharacteristicPathAsync(service, characteristic, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            await foreach (var value in BluezNotifyStream.SubscribeAsync(_bus, path, linked.Token).ConfigureAwait(false))
            {
                var copy = value is null ? [] : (byte[])value.Clone();
                yield return new GattNotification(service, characteristic, copy, _time.GetLocalNow());
            }
        }
        finally
        {
            lock (_gate)
                _subscriptions.Remove(done.Task);
            done.TrySetResult();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new DeviceDisconnectedException("The device disconnected.");
        }

        await DisconnectCoreAsync(ct, quiet: false).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = _subscriptions.ToArray();
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DisconnectCoreAsync(stop.Token, quiet: true).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            _watcher.Dispose();
        }
        catch
        {
        }

        _bus.ConnectionClosed -= OnConnectionClosed;

        if (_ownsBus)
        {
            try
            {
                await _bus.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            _lifetime.Dispose();
        }
        catch
        {
        }
    }

    internal void MarkDropped() =>
        Publish(ConnectionState.Dropped, "The device disconnected.", BleErrorKind.DeviceDisconnected);

    private async Task DisconnectCoreAsync(CancellationToken ct, bool quiet)
    {
        try
        {
            await _bus.DisconnectDeviceAsync(_devicePath, ct).ConfigureAwait(false);
        }
        catch (BluezDBusException ex) when (string.Equals(ex.ErrorName, "org.bluez.Error.NotConnected", StringComparison.Ordinal))
        {
        }
        catch (Exception) when (quiet)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BluezErrorMapper.Map(ex, "disconnect");
        }

        Publish(ConnectionState.Disconnected, null, null);
    }

    private async Task<string> FindCharacteristicPathAsync(Guid service, Guid characteristic, CancellationToken ct)
    {
        IReadOnlyList<BluezObject> objects;
        try
        {
            objects = await _bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BluezErrorMapper.Map(ex, "read");
        }

        EnsureUsable();
        var path = BluezObjectModel.FindCharacteristicPath(objects, _devicePath, service, characteristic);
        if (path is null)
            throw new BleException(BleErrorKind.NoSupportedService, "characteristic not found");
        return path;
    }

    private void EnsureUsable()
    {
        lock (_gate)
        {
            if (_disposed || _state != ConnectionState.Connected)
                throw new DeviceDisconnectedException("The device disconnected.");
        }
    }

    private void OnConnectionClosed(Exception? error)
    {
        try
        {
            MarkDropped();
        }
        catch
        {
        }
    }

    private void Publish(ConnectionState state, string? reason, BleErrorKind? kind)
    {
        EventHandler<ConnectionStateChange>? handlers;
        var cancel = false;
        lock (_gate)
        {
            if (_state == state)
                return;
            // 掉线后再释放不再补发 Disconnected：一次连接只报告一次终态。
            if (_state == ConnectionState.Dropped && state == ConnectionState.Disconnected)
                return;
            _state = state;
            handlers = StateChanged;
            cancel = state is ConnectionState.Dropped or ConnectionState.Disconnected;
        }

        if (cancel)
        {
            try
            {
                _lifetime.Cancel();
            }
            catch
            {
            }
        }

        if (handlers is null)
            return;

        var change = new ConnectionStateChange(state, reason, kind);
        foreach (var item in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ConnectionStateChange>)item).Invoke(this, change);
            }
            catch
            {
            }
        }
    }
}
