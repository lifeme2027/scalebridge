using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezConnectionTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Mac = "aa:bb:cc:dd:ee:02";
    private const string MacUpper = "AA:BB:CC:DD:EE:02";

    [Fact]
    public async Task DiscoverServices_MapsStandardScaleFlags()
    {
        var (bus, conn, _, _) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var services = await conn.DiscoverServicesAsync(cts.Token);
        var weight = Assert.Single(services, s => s.Uuid == GattUuids.WeightScaleService);
        var body = Assert.Single(services, s => s.Uuid == GattUuids.BodyCompositionService);
        Assert.Equal(GattCharacteristicProperties.Indicate, Flag(weight, GattUuids.WeightMeasurement));
        Assert.Equal(GattCharacteristicProperties.Read, Flag(weight, GattUuids.WeightScaleFeature));
        Assert.Equal(GattCharacteristicProperties.Indicate, Flag(body, GattUuids.BodyCompositionMeasurement));
        Assert.Equal(GattCharacteristicProperties.Read, Flag(body, GattUuids.BodyCompositionFeature));

        await conn.DisposeAsync();
        Assert.False(bus.Disposed);
    }

    [Fact]
    public async Task Read_ByUuid_CopiesBytes_AndMissingCharacteristicFails()
    {
        var (bus, conn, devicePath, _) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var path = BluezObjectModel.FindCharacteristicPath(
            bus.Objects, devicePath, GattUuids.WeightScaleService, GattUuids.WeightScaleFeature);
        Assert.NotNull(path);
        var source = new byte[] { 1, 2, 3 };
        bus.Values[path] = source;

        var first = await conn.ReadAsync(GattUuids.WeightScaleService, GattUuids.WeightScaleFeature, cts.Token);
        Assert.Equal(source, first);
        Assert.NotSame(source, first);
        first[0] = 9;
        var second = await conn.ReadAsync(GattUuids.WeightScaleService, GattUuids.WeightScaleFeature, cts.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, second);
        Assert.Equal(1, source[0]);

        var missing = await Assert.ThrowsAsync<BleException>(() =>
            conn.ReadAsync(GattUuids.WeightScaleService, Guid.NewGuid(), cts.Token));
        Assert.Equal(BleErrorKind.NoSupportedService, missing.Kind);
        Assert.Equal("characteristic not found", missing.Message);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Subscribe_DeliversNotification_AndEndsSilentlyWhenDropped()
    {
        var (bus, conn, devicePath, time) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var path = BluezObjectModel.FindCharacteristicPath(
            bus.Objects, devicePath, GattUuids.WeightScaleService, GattUuids.WeightMeasurement);
        Assert.NotNull(path);

        var drops = 0;
        var sawThrow = false;
        conn.StateChanged += (_, change) =>
        {
            if (change.State == ConnectionState.Dropped)
                drops++;
            if (drops == 1)
                throw new InvalidOperationException("subscriber");
        };
        conn.StateChanged += (_, change) =>
        {
            if (change.State == ConnectionState.Dropped)
                sawThrow = true;
        };

        await using var enumerator = conn.SubscribeAsync(
            GattUuids.WeightScaleService, GattUuids.WeightMeasurement, cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();
        await WaitFor(() => bus.Calls.Any(c => c.StartsWith("StartNotify:", StringComparison.Ordinal)), cts.Token);
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            path,
            BluezNames.GattCharacteristic1,
            BluezTestData.Props(("Value", new byte[] { 4, 5 })),
            []));

        Assert.True(await pending);
        var note = enumerator.Current;
        Assert.Equal(GattUuids.WeightScaleService, note.Service);
        Assert.Equal(GattUuids.WeightMeasurement, note.Characteristic);
        Assert.Equal(new byte[] { 4, 5 }, note.Value);
        Assert.Equal(time.GetLocalNow(), note.ReceivedAt);

        bus.RaisePropertiesChanged(Change(devicePath, ("Connected", false)));
        bus.RaisePropertiesChanged(Change(devicePath, ("Connected", false)));
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(1, drops);
        Assert.True(sawThrow);
        Assert.Equal(ConnectionState.Dropped, conn.State);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Subscribe_MissingCharacteristic_ThrowsWhenEnumerated()
    {
        var (_, conn, _, _) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<BleException>(async () =>
        {
            await foreach (var _ in conn.SubscribeAsync(GattUuids.WeightScaleService, Guid.NewGuid(), cts.Token))
            {
            }
        });
        Assert.Equal(BleErrorKind.NoSupportedService, ex.Kind);
        Assert.Equal("characteristic not found", ex.Message);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_IsIdempotent_AndSwallowsNotConnected()
    {
        var (bus, conn, _, _) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var transitions = 0;
        conn.StateChanged += (_, _) => transitions++;

        await conn.DisconnectAsync(cts.Token);
        bus.Failures["DisconnectDevice"] = new BluezDBusException("org.bluez.Error.NotConnected", Mac);
        await conn.DisconnectAsync(cts.Token);

        Assert.Equal(ConnectionState.Disconnected, conn.State);
        Assert.Equal(1, transitions);
        Assert.Equal(2, bus.Calls.Count(c => c.StartsWith("DisconnectDevice:", StringComparison.Ordinal)));
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_StopsSubscription_AndStopNotifyIsCalled()
    {
        var (bus, conn, devicePath, _) = await ConnectScaleAsync(ownsBus: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var path = BluezObjectModel.FindCharacteristicPath(
            bus.Objects, devicePath, GattUuids.BodyCompositionService, GattUuids.BodyCompositionMeasurement);
        Assert.NotNull(path);

        await using var enumerator = conn.SubscribeAsync(
            GattUuids.BodyCompositionService, GattUuids.BodyCompositionMeasurement, cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();
        await WaitFor(() => bus.Calls.Any(c => c.StartsWith("StartNotify:", StringComparison.Ordinal)), cts.Token);

        await conn.DisposeAsync();
        await conn.DisposeAsync();

        Assert.False(await pending);
        Assert.Contains(bus.Calls, c => c.StartsWith("StopNotify:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.True(bus.Disposed);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => conn.DiscoverServicesAsync(cts.Token));
    }

    [Fact]
    public async Task AfterDropped_OperationsThrowDeviceDisconnected()
    {
        var (bus, conn, devicePath, _) = await ConnectScaleAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bus.RaisePropertiesChanged(Change(devicePath, ("Connected", false)));
        Assert.Equal(ConnectionState.Dropped, conn.State);

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => conn.DiscoverServicesAsync(cts.Token));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() =>
            conn.ReadAsync(GattUuids.WeightScaleService, GattUuids.WeightScaleFeature, cts.Token));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(async () =>
        {
            await foreach (var _ in conn.SubscribeAsync(GattUuids.WeightScaleService, GattUuids.WeightMeasurement, cts.Token))
            {
            }
        });

        await conn.DisposeAsync();
        Assert.False(bus.Disposed);
    }

    [Fact]
    public async Task DisposeAfterDropped_DoesNotReportDisconnectedAgain()
    {
        var (bus, conn, devicePath, _) = await ConnectScaleAsync();
        var states = new List<ConnectionState>();
        conn.StateChanged += (_, change) => states.Add(change.State);

        bus.RaisePropertiesChanged(Change(devicePath, ("Connected", false)));
        await conn.DisposeAsync();

        Assert.Equal(new[] { ConnectionState.Dropped }, states);
        Assert.Equal(ConnectionState.Dropped, conn.State);
    }

    [Fact]
    public async Task Dispose_WithoutOwnership_LeavesBus()
    {
        var (bus, conn, _, _) = await ConnectScaleAsync(ownsBus: false);
        await conn.DisposeAsync();
        Assert.False(bus.Disposed);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    private static async Task<(FakeBluezBus Bus, BluezConnection Conn, string DevicePath, TimeProvider Time)> ConnectScaleAsync(bool ownsBus = false)
    {
        var bus = new FakeBluezBus();
        foreach (var obj in BluezTestData.StandardScale(Hci0, Mac))
            bus.Objects.Add(obj);
        var time = new FixedTimeProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var conn = await BluezConnector.ConnectAsync(bus, Hci0, Mac, TimeSpan.FromMilliseconds(400), time, ownsBus, cts.Token);
        Assert.Equal(MacUpper, conn.DeviceId);
        var devicePath = BluezObjectModel.DeviceIdToPath(Hci0, Mac);
        return (bus, conn, devicePath, time);
    }

    private static GattCharacteristicProperties Flag(GattServiceInfo service, Guid characteristic) =>
        Assert.Single(service.Characteristics, c => c.Uuid == characteristic).Properties;

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
