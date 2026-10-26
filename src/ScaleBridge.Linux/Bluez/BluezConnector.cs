using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>按 BlueZ Device1 建立连接，成功后才交出 BluezConnection。</summary>
internal static class BluezConnector
{
    /// <summary>订阅属性后再 Connect，在超时内等到 ServicesResolved。</summary>
    public static async Task<BluezConnection> ConnectAsync(
        IBluezBus bus,
        string adapterPath,
        string deviceId,
        TimeSpan timeout,
        TimeProvider time,
        bool ownsBus,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(time);

        string devicePath;
        try
        {
            devicePath = BluezObjectModel.DeviceIdToPath(adapterPath, deviceId);
        }
        catch (ArgumentException)
        {
            await DisposeBusAsync(bus, ownsBus).ConfigureAwait(false);
            throw new BleException(BleErrorKind.Other, "invalid device id");
        }

        var canonicalId = BluezObjectModel.PathToDeviceId(devicePath);
        IDisposable? watcher = null;
        var wait = new ConnectWait();
        var hooked = false;
        var preConnected = false;

        void OnChanged(BluezPropertiesChanged change)
        {
            try
            {
                wait.OnChanged(change);
            }
            catch
            {
                // Handlers must not throw or block.
            }
        }

        void OnClosed(Exception? error)
        {
            try
            {
                wait.OnClosed(error);
            }
            catch
            {
            }
        }

        try
        {
            bus.ConnectionClosed += OnClosed;
            hooked = true;

            watcher = await bus.WatchPropertiesChangedAsync(devicePath, OnChanged, ct).ConfigureAwait(false);
            var objects = await bus.GetManagedObjectsAsync(ct).ConfigureAwait(false);
            var device = FindDevice(objects, devicePath);
            if (device is null)
                throw new BleException(BleErrorKind.Other, "device unknown; scan first");

            wait.Seed(device);
            preConnected = device.Interfaces is not null
                && device.Interfaces.TryGetValue(BluezNames.Device1, out var initial)
                && BluezProps.TryGetBool(initial, "Connected", out var wasConnected)
                && wasConnected;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            try
            {
                await bus.ConnectDeviceAsync(devicePath, deadline.Token).ConfigureAwait(false);
            }
            catch (BluezDBusException ex) when (IsAlreadyConnected(ex))
            {
            }

            var again = await bus.GetManagedObjectsAsync(deadline.Token).ConfigureAwait(false);
            wait.ApplySnapshot(FindDevice(again, devicePath));
            await wait.WaitUntilResolvedAsync(deadline.Token).ConfigureAwait(false);

            var connection = new BluezConnection(bus, devicePath, canonicalId, time, ownsBus, watcher);
            wait.Bind(connection);
            watcher = null;
            return connection;
        }
        catch (Exception ex)
        {
            try
            {
                watcher?.Dispose();
            }
            catch
            {
            }

            // 连接前已被别的客户端连上的设备，失败时不替它断开。
            if (!preConnected)
                await BestEffortDisconnectAsync(bus, devicePath).ConfigureAwait(false);
            await DisposeBusAsync(bus, ownsBus).ConfigureAwait(false);
            throw Translate(ex, ct);
        }
        finally
        {
            if (hooked)
                bus.ConnectionClosed -= OnClosed;
        }
    }

    private static Exception Translate(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return ex;
            return new BleTimeoutException("connect timed out.");
        }

        if (ex is BleException)
            return ex;

        return BluezErrorMapper.Map(ex, "connect");
    }

    private static async Task BestEffortDisconnectAsync(IBluezBus bus, string devicePath)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await bus.DisconnectDeviceAsync(devicePath, stop.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task DisposeBusAsync(IBluezBus bus, bool ownsBus)
    {
        if (!ownsBus)
            return;
        try
        {
            await bus.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static BluezObject? FindDevice(IReadOnlyList<BluezObject> objects, string devicePath)
    {
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (!string.Equals(obj.Path, devicePath, StringComparison.Ordinal))
                continue;
            if (obj.Interfaces is not null && obj.Interfaces.ContainsKey(BluezNames.Device1))
                return obj;
        }

        return null;
    }

    private static bool IsAlreadyConnected(BluezDBusException ex) =>
        string.Equals(ex.ErrorName, "org.bluez.Error.AlreadyConnected", StringComparison.Ordinal);

    private sealed class ConnectWait
    {
        private readonly object _gate = new();
        private TaskCompletionSource _pulse = NewPulse();
        private bool _servicesResolved;
        private bool _connectedFalse;
        private bool _busClosed;
        private Exception? _closeError;
        private BluezConnection? _connection;

        public void Seed(BluezObject device)
        {
            lock (_gate)
            {
                if (TryBool(device, "ServicesResolved", out var resolved) && resolved)
                    _servicesResolved = true;
            }
        }

        public void ApplySnapshot(BluezObject? device)
        {
            if (device is null)
                return;

            lock (_gate)
            {
                if (TryBool(device, "ServicesResolved", out var resolved) && resolved)
                    _servicesResolved = true;
                if (TryBool(device, "Connected", out var connected) && !connected)
                    _connectedFalse = true;
            }
        }

        public void OnChanged(BluezPropertiesChanged change)
        {
            if (!string.Equals(change.Interface, BluezNames.Device1, StringComparison.Ordinal))
                return;
            if (change.Changed is null)
                return;

            BluezConnection? drop = null;
            lock (_gate)
            {
                var pulse = false;
                if (change.Changed.TryGetValue("Connected", out var connected) && connected is bool isConnected && !isConnected)
                {
                    _connectedFalse = true;
                    drop = _connection;
                    pulse = true;
                }

                if (change.Changed.TryGetValue("ServicesResolved", out var resolved) && resolved is bool servicesResolved && servicesResolved)
                {
                    _servicesResolved = true;
                    pulse = true;
                }

                if (pulse)
                    PulseLocked();
            }

            drop?.MarkDropped();
        }

        public void OnClosed(Exception? error)
        {
            BluezConnection? drop;
            lock (_gate)
            {
                _busClosed = true;
                _closeError = error;
                drop = _connection;
                PulseLocked();
            }

            drop?.MarkDropped();
        }

        public void Bind(BluezConnection connection)
        {
            var drop = false;
            lock (_gate)
            {
                _connection = connection;
                drop = _connectedFalse || _busClosed;
            }

            if (drop)
                connection.MarkDropped();
        }

        public async Task WaitUntilResolvedAsync(CancellationToken ct)
        {
            while (true)
            {
                Task pulse;
                lock (_gate)
                {
                    ThrowIfTerminal();
                    if (_servicesResolved)
                        return;
                    pulse = _pulse.Task;
                }

                await pulse.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        private void ThrowIfTerminal()
        {
            if (_busClosed)
                throw CloseException();
            if (_connectedFalse)
                throw new DeviceDisconnectedException("The device disconnected.");
        }

        private Exception CloseException()
        {
            if (_closeError is null)
                return new BluetoothUnavailableException("connect failed (connection closed).");
            try
            {
                return BluezErrorMapper.Map(_closeError, "connect");
            }
            catch (ArgumentException)
            {
                return new BluetoothUnavailableException("connect failed (connection closed).");
            }
        }

        private void PulseLocked()
        {
            var current = _pulse;
            _pulse = NewPulse();
            current.TrySetResult();
        }

        private static TaskCompletionSource NewPulse() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static bool TryBool(BluezObject device, string key, out bool value)
        {
            value = false;
            if (device.Interfaces is null || !device.Interfaces.TryGetValue(BluezNames.Device1, out var props))
                return false;
            return BluezProps.TryGetBool(props, key, out value);
        }
    }
}
