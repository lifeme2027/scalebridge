using ScaleBridge.Core.Ble;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezConnectorTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Mac = "AA:BB:CC:DD:EE:01";

    [Fact]
    public async Task Watcher_IsRegisteredBeforeConnect()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var conn = await ConnectAsync(bus, TimeSpan.FromMilliseconds(500), ownsBus: false, cts.Token);
        await conn.DisposeAsync();

        var watch = bus.Calls.FindIndex(c => c.StartsWith("WatchPropertiesChanged:", StringComparison.Ordinal));
        var connect = bus.Calls.FindIndex(c => c.StartsWith("ConnectDevice:", StringComparison.Ordinal));
        Assert.True(watch >= 0);
        Assert.True(connect > watch);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task ServicesResolved_SignalAfterConnect_Completes()
    {
        var bus = new FakeBluezBus();
        var device = Device(servicesResolved: false);
        bus.Objects.Add(device);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var connecting = ConnectAsync(bus, TimeSpan.FromMilliseconds(500), ownsBus: false, cts.Token);
        await WaitFor(() => bus.Calls.Count(c => c == "GetManagedObjects") >= 2, cts.Token);
        bus.RaisePropertiesChanged(Change(device.Path, ("ServicesResolved", true)));

        var conn = await connecting;
        Assert.Equal(Mac, conn.DeviceId);
        Assert.Equal(ConnectionState.Connected, conn.State);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task ServicesResolved_AlreadyTrueOnSnapshot_DoesNotWaitForSignal()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var conn = await ConnectAsync(bus, TimeSpan.FromMilliseconds(300), ownsBus: false, cts.Token);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("DisconnectDevice:", StringComparison.Ordinal));
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task ServicesResolved_SignalBetweenConnectAndSnapshot_IsKept()
    {
        var bus = new FakeBluezBus();
        var device = Device(servicesResolved: false);
        bus.Objects.Add(device);
        bus.Gates["ConnectDevice"] = _ =>
        {
            bus.RaisePropertiesChanged(Change(device.Path, ("ServicesResolved", true)));
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var conn = await ConnectAsync(bus, TimeSpan.FromMilliseconds(400), ownsBus: false, cts.Token);
        Assert.Equal(ConnectionState.Connected, conn.State);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Timeout_DisconnectsAndReleasesWatcher()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: false));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<BleTimeoutException>(() =>
            ConnectAsync(bus, TimeSpan.FromMilliseconds(200), ownsBus: true, cts.Token));

        Assert.Contains("connect timed out", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Mac, ex.Message, StringComparison.Ordinal);
        Assert.Contains(bus.Calls, c => c.StartsWith("DisconnectDevice:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Timeout_OnDeviceConnectedByAnotherClient_DoesNotDisconnectIt()
    {
        var bus = new FakeBluezBus();
        var plain = Device(servicesResolved: false);
        var props = new Dictionary<string, object?>(plain.Interfaces[BluezNames.Device1]) { ["Connected"] = true };
        bus.Objects.Add(new BluezObject(
            plain.Path,
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { [BluezNames.Device1] = props }));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<BleTimeoutException>(() =>
            ConnectAsync(bus, TimeSpan.FromMilliseconds(200), ownsBus: false, cts.Token));

        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("DisconnectDevice:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task CallerCancel_ThrowsCanceledAndDisconnects()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: false));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var connecting = ConnectAsync(bus, TimeSpan.FromMilliseconds(500), ownsBus: false, call.Token);
        await WaitFor(() => bus.Calls.Count(c => c == "GetManagedObjects") >= 2, cts.Token);
        call.Cancel();

        var ex = await Record.ExceptionAsync(() => connecting);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Contains(bus.Calls, c => c.StartsWith("DisconnectDevice:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.False(bus.Disposed);
    }

    [Fact]
    public async Task ConnectedFalseWhileWaiting_ThrowsDeviceDisconnected()
    {
        var bus = new FakeBluezBus();
        var device = Device(servicesResolved: false);
        bus.Objects.Add(device);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var connecting = ConnectAsync(bus, TimeSpan.FromMilliseconds(500), ownsBus: false, cts.Token);
        await WaitFor(() => bus.Calls.Count(c => c == "GetManagedObjects") >= 2, cts.Token);
        bus.RaisePropertiesChanged(Change(device.Path, ("Connected", false)));

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => connecting);
        Assert.DoesNotContain(Mac, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task MissingDevice_ThrowsWithoutMac()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<BleException>(() =>
            ConnectAsync(bus, TimeSpan.FromMilliseconds(300), ownsBus: false, cts.Token));

        Assert.Equal(BleErrorKind.Other, ex.Kind);
        Assert.Equal("device unknown; scan first", ex.Message);
        Assert.DoesNotContain(Mac, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.False(bus.Disposed);
    }

    [Fact]
    public async Task InvalidDeviceId_ThrowsWithoutEchoingId()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        const string bad = "not-a-mac";

        var ex = await Assert.ThrowsAsync<BleException>(() =>
            BluezConnector.ConnectAsync(bus, Hci0, bad, TimeSpan.FromMilliseconds(200), new FixedTimeProvider(), true, cts.Token));

        Assert.Equal(BleErrorKind.Other, ex.Kind);
        Assert.Equal("invalid device id", ex.Message);
        Assert.DoesNotContain(bad, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(bus.Disposed);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public async Task AlreadyConnected_IsTolerated()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: true));
        bus.Failures["ConnectDevice"] = new BluezDBusException("org.bluez.Error.AlreadyConnected", "busy");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var conn = await ConnectAsync(bus, TimeSpan.FromMilliseconds(400), ownsBus: false, cts.Token);
        Assert.Equal(ConnectionState.Connected, conn.State);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task NotReady_MapsToUnavailable_AndOwnsBusFalseLeavesBus()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: false));
        bus.Failures["ConnectDevice"] = new BluezDBusException("org.bluez.Error.NotReady", "down " + Mac);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<BluetoothUnavailableException>(() =>
            ConnectAsync(bus, TimeSpan.FromMilliseconds(400), ownsBus: false, cts.Token));

        Assert.Contains("NotReady", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Mac, ex.Message, StringComparison.Ordinal);
        Assert.False(bus.Disposed);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task ServiceUnknown_MapsAndDisposesOwnedBus()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(Device(servicesResolved: true));
        bus.Failures["ConnectDevice"] = new BluezDBusException("org.freedesktop.DBus.Error.ServiceUnknown", "no bluez");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<BluetoothUnavailableException>(() =>
            ConnectAsync(bus, TimeSpan.FromMilliseconds(400), ownsBus: true, cts.Token));

        Assert.Contains("ServiceUnknown", ex.Message, StringComparison.Ordinal);
        Assert.True(bus.Disposed);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    private static Task<BluezConnection> ConnectAsync(FakeBluezBus bus, TimeSpan timeout, bool ownsBus, CancellationToken ct) =>
        BluezConnector.ConnectAsync(bus, Hci0, Mac, timeout, new FixedTimeProvider(), ownsBus, ct);

    private static BluezObject Device(bool servicesResolved) =>
        BluezTestData.Device(Hci0, Mac, name: "Scale", servicesResolved: servicesResolved);

    private static BluezPropertiesChanged Change(string path, params (string Key, object? Value)[] props) =>
        new(path, BluezNames.Device1, BluezTestData.Props(props), []);

    private static async Task WaitFor(Func<bool> ready, CancellationToken ct)
    {
        while (!ready())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
    }
}
