using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezScannerTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Hci1 = "/org/bluez/hci1";

    [Fact]
    public async Task Scan_EmitsExistingDevices_FiltersClientSide_IgnoresOtherAdapter()
    {
        var bus = new FakeBluezBus();
        var weight = GattUuids.WeightScaleService;
        var kept = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:10", name: "Keep", rssi: -20, uuids: [BluezTestData.Uuid(weight)]);
        var skipped = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:11", name: "Skip", uuids: [BluezTestData.Uuid(GattUuids.BodyCompositionService)]);
        var other = BluezTestData.Device(Hci1, "AA:BB:CC:DD:EE:12", name: "Other", uuids: [BluezTestData.Uuid(weight)]);
        bus.Objects.Add(kept);
        bus.Objects.Add(skipped);
        bus.Objects.Add(other);
        var time = new FixedTimeProvider();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var seen = new List<AdvertisedDevice>();
        await foreach (var device in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions { ServiceFilter = [weight] }, time, cts.Token))
        {
            seen.Add(device);
            bus.RaisePropertiesChanged(new BluezPropertiesChanged(
                kept.Path,
                BluezNames.Device1,
                BluezTestData.Props(("RSSI", (short)-30)),
                []));
            if (seen.Count >= 2)
                break;
        }

        Assert.Equal(2, seen.Count);
        Assert.All(seen, d => Assert.Equal("AA:BB:CC:DD:EE:10", d.Id));
        Assert.Equal(-20, seen[0].Rssi);
        Assert.Equal(-30, seen[1].Rssi);
        Assert.Equal("Keep", seen[0].Name);
        Assert.False(seen[0].IsDemo);
        Assert.Equal(time.GetLocalNow(), seen[0].SeenAt);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Contains(bus.Calls, c => c.StartsWith("StopDiscovery:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watchers_AreRegisteredBeforeStartDiscovery()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
        {
        }

        var start = bus.Calls.FindIndex(c => c.StartsWith("StartDiscovery:", StringComparison.Ordinal));
        Assert.True(start > 0);
        Assert.True(bus.Calls.FindIndex(c => c == "WatchInterfacesAdded") < start);
        Assert.True(bus.Calls.FindIndex(c => c == "WatchInterfacesRemoved") < start);
        Assert.True(bus.Calls.FindIndex(c => c == "WatchPropertiesChanged:*") < start);
        Assert.True(bus.Calls.FindIndex(c => c.StartsWith("SetDiscoveryFilter:", StringComparison.Ordinal)) < start);
        Assert.True(bus.Calls.FindIndex(c => c == "GetManagedObjects") > start);
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task SetDiscoveryFilter_NotSupported_IsTolerated()
    {
        var bus = new FakeBluezBus();
        bus.Failures["SetDiscoveryFilter"] = new BluezDBusException("org.bluez.Error.NotSupported", "filter");
        var device = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:20", name: "Scale");
        bus.Objects.Add(device);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        AdvertisedDevice? seen = null;
        await foreach (var item in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
        {
            seen = item;
            break;
        }

        Assert.NotNull(seen);
        Assert.Equal("AA:BB:CC:DD:EE:20", seen!.Id);
        Assert.Contains(bus.Calls, c => c.StartsWith("StartDiscovery:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartDiscovery_InProgress_DoesNotStopDiscovery()
    {
        var bus = new FakeBluezBus();
        bus.Failures["StartDiscovery"] = new BluezDBusException("org.bluez.Error.InProgress", "busy");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
        {
        }

        Assert.Contains(bus.Calls, c => c.StartsWith("StartDiscovery:", StringComparison.Ordinal));
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("StopDiscovery:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task StartDiscovery_ServiceUnknown_MapsToBluetoothUnavailable()
    {
        var bus = new FakeBluezBus();
        bus.Failures["StartDiscovery"] = new BluezDBusException("org.freedesktop.DBus.Error.ServiceUnknown", "missing");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<BluetoothUnavailableException>(async () =>
        {
            await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
            {
            }
        });

        Assert.DoesNotContain("AA:BB", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/dev_", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("StopDiscovery:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetManagedObjects_Failure_IsMappedAndDiscoveryStopped()
    {
        var bus = new FakeBluezBus();
        bus.Failures["GetManagedObjects"] = new BluezDBusException("org.freedesktop.DBus.Error.NoReply", "slow");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<BleTimeoutException>(async () =>
        {
            await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
            {
            }
        });

        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Contains(bus.Calls, c => c.StartsWith("StopDiscovery:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_StopsDiscovery_AndClearsWatchers()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
        {
        }

        Assert.Contains(bus.Calls, c => c.StartsWith("StopDiscovery:", StringComparison.Ordinal));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task ConnectionClosed_SurfacesBleException()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();
        bus.RaiseConnectionClosed(new BluezDBusException("org.freedesktop.DBus.Error.NoServer", "closed"));

        var ex = await Assert.ThrowsAnyAsync<BleException>(() => pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsType<BluetoothUnavailableException>(ex);
        Assert.DoesNotContain("/dev_", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfacesRemoved_DropsDevice_SoLaterRssiIsNotEmitted()
    {
        var bus = new FakeBluezBus();
        var device = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:30", name: "Scale", rssi: -40);
        bus.Objects.Add(device);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = 0;
        await foreach (var _ in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
        {
            seen++;
            bus.RaiseInterfacesRemoved(device.Path, [BluezNames.Device1]);
            bus.RaisePropertiesChanged(new BluezPropertiesChanged(
                device.Path,
                BluezNames.Device1,
                BluezTestData.Props(("RSSI", (short)-5), ("Name", "Scale")),
                []));
            break;
        }

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task InterfacesAdded_EmitsDevice_AliasAloneDoesNot()
    {
        var bus = new FakeBluezBus();
        var device = BluezTestData.Device(
            Hci0,
            "aa:bb:cc:dd:ee:40",
            rssi: -12,
            serviceData: new Dictionary<string, byte[]> { [BluezTestData.Uuid(GattUuids.WeightScaleService)] = [1] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<AdvertisedDevice>();
        var scan = Task.Run(async () =>
        {
            await foreach (var item in BluezScanner.ScanAsync(bus, Hci0, new ScanOptions(), new FixedTimeProvider(), cts.Token))
            {
                seen.Add(item);
                if (seen.Count >= 2)
                    break;
            }
        });

        var ready = await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartDiscovery:", StringComparison.Ordinal)), TimeSpan.FromSeconds(1));
        Assert.True(ready);
        bus.RaiseInterfacesAdded(device);
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Device1,
            BluezTestData.Props(("Alias", "Nope")),
            []));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Device1,
            BluezTestData.Props(("Name", "FromName")),
            []));
        await scan.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, seen.Count);
        Assert.Equal(GattUuids.WeightScaleService, Assert.Single(seen[0].ServiceUuids));
        Assert.Null(seen[0].Name);
        Assert.Equal("FromName", seen[1].Name);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
                return true;
            await Task.Delay(10);
        }

        return ready();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
    }
}
