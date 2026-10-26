using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezBleAdapterTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Mac = "AA:BB:CC:DD:EE:20";

    [Fact]
    public void Identity_IsLinuxRealAdapter()
    {
        var adapter = Create(new FakeBluezBus());
        Assert.Equal("linux", adapter.Id);
        Assert.False(adapter.IsDemo);
        Assert.Contains("BlueZ", adapter.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Availability_NotLinux_IsUnavailableWithoutOpeningBus()
    {
        var opened = 0;
        var adapter = new BluezBleAdapter(
            _ => { opened++; return Task.FromResult<IBluezBus>(new FakeBluezBus()); },
            platformSupported: false);

        Assert.Equal(BleAvailability.Unavailable, await adapter.GetAvailabilityAsync());
        Assert.Equal(0, opened);
        await Assert.ThrowsAsync<BluetoothUnavailableException>(() => adapter.ConnectAsync(Mac));
        await Assert.ThrowsAsync<BluetoothUnavailableException>(async () =>
        {
            await foreach (var _ in adapter.ScanAsync(new ScanOptions()))
            {
            }
        });
    }

    [Fact]
    public async Task Availability_PoweredAdapter_IsAvailable_AndBusIsDisposed()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));

        Assert.Equal(BleAvailability.Available, await Create(bus).GetAvailabilityAsync());
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Availability_NoAdapter_OrUnpowered_IsUnavailable()
    {
        Assert.Equal(BleAvailability.Unavailable, await Create(new FakeBluezBus()).GetAvailabilityAsync());

        var off = new FakeBluezBus();
        off.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: false));
        Assert.Equal(BleAvailability.Unavailable, await Create(off).GetAvailabilityAsync());
    }

    [Fact]
    public async Task Availability_PreferredAdapterMissing_IsUnavailable()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        Assert.Equal(BleAvailability.Unavailable, await Create(bus, preferred: "hci7").GetAvailabilityAsync());
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.NoServer", BleAvailability.Unavailable)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", BleAvailability.PermissionDenied)]
    [InlineData("org.bluez.Error.Failed", BleAvailability.Unavailable)]
    public async Task Availability_MapsBusErrors(string errorName, BleAvailability expected)
    {
        var bus = new FakeBluezBus();
        bus.Failures["GetManagedObjects"] = new BluezDBusException(errorName, "x");
        Assert.Equal(expected, await Create(bus).GetAvailabilityAsync());
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Availability_BusFactoryFailure_IsMapped()
    {
        var adapter = new BluezBleAdapter(
            _ => throw new BluezDBusException("org.freedesktop.DBus.Error.AccessDenied", "denied"),
            platformSupported: true);
        Assert.Equal(BleAvailability.PermissionDenied, await adapter.GetAvailabilityAsync());
    }

    [Fact]
    public async Task Availability_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(bus).GetAvailabilityAsync(cts.Token));
    }

    [Fact]
    public async Task Scan_NoAdapter_ThrowsUnavailable_AndDisposesBus()
    {
        var bus = new FakeBluezBus();
        await Assert.ThrowsAsync<BluetoothUnavailableException>(async () =>
        {
            await foreach (var _ in Create(bus).ScanAsync(new ScanOptions()))
            {
            }
        });
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Scan_EmitsDevices_AndDisposesBusWhenStopped()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        bus.Objects.Add(BluezTestData.Device(Hci0, Mac, name: "Scale", rssi: -40, uuids: [BluezTestData.Uuid(GattUuids.WeightScaleService)]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var seen = new List<AdvertisedDevice>();
        await foreach (var device in Create(bus).ScanAsync(new ScanOptions(), cts.Token))
        {
            seen.Add(device);
            break;
        }

        Assert.Single(seen);
        Assert.Equal(Mac, seen[0].Id);
        Assert.True(bus.Disposed);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task Scan_CancelledBeforeStart_EndsSilentlyAndDisposesBus()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await foreach (var _ in Create(bus).ScanAsync(new ScanOptions(), cts.Token))
        {
            Assert.Fail("no device expected");
        }

        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Scan_BusFactoryFailure_ThrowsMappedBleException()
    {
        var adapter = new BluezBleAdapter(
            _ => throw new BluezDBusException("org.freedesktop.DBus.Error.NoServer", "no socket"),
            platformSupported: true);

        await Assert.ThrowsAsync<BluetoothUnavailableException>(async () =>
        {
            await foreach (var _ in adapter.ScanAsync(new ScanOptions()))
            {
            }
        });
    }

    [Fact]
    public async Task Connect_Succeeds_AndConnectionOwnsBus()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        foreach (var o in BluezTestData.StandardScale(Hci0, Mac))
            bus.Objects.Add(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await using var connection = await Create(bus).ConnectAsync(Mac, TimeSpan.FromSeconds(1), cts.Token);

        Assert.Equal(Mac, connection.DeviceId);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.False(bus.Disposed);
        await connection.DisposeAsync();
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Connect_NoAdapter_ThrowsUnavailable_AndDisposesBus()
    {
        var bus = new FakeBluezBus();
        await Assert.ThrowsAsync<BluetoothUnavailableException>(() => Create(bus).ConnectAsync(Mac));
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Connect_UnknownDevice_FailsAndDisposesBus()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<BleException>(() => Create(bus).ConnectAsync(Mac, TimeSpan.FromMilliseconds(200), cts.Token));
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Connect_NullTimeout_UsesDefaultAndStillResolves()
    {
        var bus = new FakeBluezBus();
        bus.Objects.Add(BluezTestData.Adapter(Hci0, "hci0", powered: true));
        foreach (var o in BluezTestData.StandardScale(Hci0, Mac))
            bus.Objects.Add(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await using var connection = await Create(bus).ConnectAsync(Mac, null, cts.Token);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Connect_BusFactoryCancelled_Throws()
    {
        var adapter = new BluezBleAdapter(_ => throw new OperationCanceledException(), platformSupported: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ConnectAsync(Mac));
    }

    private static BluezBleAdapter Create(FakeBluezBus bus, string? preferred = null) =>
        new(_ => Task.FromResult<IBluezBus>(bus), new FixedTimeProvider(), preferred, platformSupported: true);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
    }
}
