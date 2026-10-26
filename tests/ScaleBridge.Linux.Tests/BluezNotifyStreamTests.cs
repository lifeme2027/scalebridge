using ScaleBridge.Core.Ble;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezNotifyStreamTests
{
    private const string Path = "/org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF/service0001/char0002";

    [Fact]
    public async Task Watcher_IsRegisteredBeforeStartNotify()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await foreach (var _ in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
        {
        }

        var start = bus.Calls.FindIndex(c => c.StartsWith("StartNotify:", StringComparison.Ordinal));
        Assert.True(start > 0);
        Assert.True(bus.Calls.FindIndex(c => c == "WatchPropertiesChanged:" + Path) < start);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Equal(1, StopCount(bus));
    }

    [Fact]
    public async Task Values_AreDeliveredInOrder_AndCopied()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Task.Run(async () =>
        {
            await foreach (var item in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
            {
                seen.Add(item);
                if (seen.Count >= 3)
                    break;
            }
        });

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        var first = new byte[] { 1, 2 };
        var empty = Array.Empty<byte>();
        var third = new byte[] { 3 };
        bus.RaisePropertiesChanged(ValueChange(Path, first));
        first[0] = 9;
        bus.RaisePropertiesChanged(ValueChange(Path, empty));
        bus.RaisePropertiesChanged(ValueChange(Path, third));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 1, 2 }, seen[0]);
        Assert.Empty(seen[1]);
        Assert.Equal(new byte[] { 3 }, seen[2]);
        Assert.NotSame(first, seen[0]);
        Assert.NotSame(empty, seen[1]);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Equal(1, StopCount(bus));
    }

    [Fact]
    public async Task InvalidatedValue_ReadsFallback_BetweenNeighbours()
    {
        var bus = new FakeBluezBus();
        bus.Values[Path] = [9];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Collect(bus, seen, 3, cts.Token);

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(ValueChange(Path, [1]));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(Path, BluezNames.GattCharacteristic1, BluezTestData.Props(), ["Value"]));
        bus.RaisePropertiesChanged(ValueChange(Path, [2]));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 1 }, seen[0]);
        Assert.Equal(new byte[] { 9 }, seen[1]);
        Assert.Equal(new byte[] { 2 }, seen[2]);
        Assert.Contains(bus.Calls, c => c == "ReadValue:" + Path);
    }

    [Fact]
    public async Task WrongTypedValue_FallsBackToRead()
    {
        var bus = new FakeBluezBus();
        bus.Values[Path] = [4, 5];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Collect(bus, seen, 2, cts.Token);

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            Path,
            BluezNames.GattCharacteristic1,
            BluezTestData.Props(("Value", "nope")),
            []));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            Path,
            BluezNames.GattCharacteristic1,
            BluezTestData.Props(("Value", null)),
            []));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 4, 5 }, seen[0]);
        Assert.Equal(new byte[] { 4, 5 }, seen[1]);
        Assert.Equal(2, bus.Calls.Count(c => c == "ReadValue:" + Path));
    }

    [Fact]
    public async Task OtherPath_OtherInterface_AndNotifying_AreIgnored()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Collect(bus, seen, 1, cts.Token);

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(ValueChange(Path + "/other", [7]));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            Path,
            BluezNames.Device1,
            BluezTestData.Props(("Value", new byte[] { 8 })),
            []));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(
            Path,
            BluezNames.GattCharacteristic1,
            BluezTestData.Props(("Notifying", true)),
            []));
        bus.RaisePropertiesChanged(ValueChange(Path, [6]));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 6 }, Assert.Single(seen));
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("ReadValue:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartNotify_NotPermitted_MapsAndDoesNotStop()
    {
        var bus = new FakeBluezBus();
        bus.Failures["StartNotify"] = new BluezDBusException("org.bluez.Error.NotPermitted", "no");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<BlePermissionException>(async () =>
        {
            await foreach (var _ in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
            {
            }
        });

        Assert.DoesNotContain(Path, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Equal(0, StopCount(bus));
        Assert.True(bus.Calls.FindIndex(c => c.StartsWith("WatchPropertiesChanged:", StringComparison.Ordinal)) >= 0);
    }

    [Fact]
    public async Task StartNotify_InProgress_IsTolerated()
    {
        var bus = new FakeBluezBus();
        bus.Failures["StartNotify"] = new BluezDBusException("org.bluez.Error.InProgress", "busy");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Collect(bus, seen, 1, cts.Token);

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(ValueChange(Path, [1]));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 1 }, Assert.Single(seen));
        Assert.Equal(1, StopCount(bus));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task Cancellation_StopsNotifyOnce_AndClearsWatchers()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await foreach (var _ in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
        {
        }

        Assert.Equal(1, StopCount(bus));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task EarlyBreak_AfterOneItem_CleansUp()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var seen = new List<byte[]>();
        var run = Task.Run(async () =>
        {
            await foreach (var item in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
            {
                seen.Add(item);
                break;
            }
        });

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(ValueChange(Path, [1, 1]));
        bus.RaisePropertiesChanged(ValueChange(Path, [2, 2]));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 1, 1 }, Assert.Single(seen));
        Assert.Equal(1, StopCount(bus));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task StopNotify_Failure_IsSwallowed()
    {
        var bus = new FakeBluezBus();
        bus.Failures["StopNotify"] = new InvalidOperationException("stop failed");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await foreach (var _ in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
        {
        }

        Assert.Equal(1, StopCount(bus));
        Assert.Equal(0, bus.ActiveWatchers);
    }

    [Fact]
    public async Task ConnectionClosed_SurfacesBleException()
    {
        var bus = new FakeBluezBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();
        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaiseConnectionClosed(new BluezDBusException("org.freedesktop.DBus.Error.NoServer", "closed"));

        var ex = await Assert.ThrowsAnyAsync<BleException>(() => pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsType<BluetoothUnavailableException>(ex);
        Assert.DoesNotContain(Path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadValue_Failure_DuringFallback_IsMapped()
    {
        var bus = new FakeBluezBus();
        bus.Failures["ReadValue"] = new BluezDBusException("org.bluez.Error.NotConnected", "gone");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? caught = null;
        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in BluezNotifyStream.SubscribeAsync(bus, Path, cts.Token))
                {
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        Assert.True(await WaitUntilAsync(() => bus.Calls.Exists(c => c.StartsWith("StartNotify:", StringComparison.Ordinal))));
        bus.RaisePropertiesChanged(new BluezPropertiesChanged(Path, BluezNames.GattCharacteristic1, BluezTestData.Props(), ["Value"]));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<DeviceDisconnectedException>(caught);
        Assert.DoesNotContain(Path, caught!.Message, StringComparison.Ordinal);
        Assert.Equal(0, bus.ActiveWatchers);
        Assert.Equal(1, StopCount(bus));
    }

    private static Task Collect(FakeBluezBus bus, List<byte[]> seen, int count, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await foreach (var item in BluezNotifyStream.SubscribeAsync(bus, Path, ct))
            {
                seen.Add(item);
                if (seen.Count >= count)
                    break;
            }
        });

    private static int StopCount(FakeBluezBus bus) =>
        bus.Calls.Count(c => c.StartsWith("StopNotify:", StringComparison.Ordinal));

    private static BluezPropertiesChanged ValueChange(string path, byte[] value) =>
        new(path, BluezNames.GattCharacteristic1, BluezTestData.Props(("Value", value)), []);

    private static async Task<bool> WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
                return true;
            await Task.Delay(10);
        }

        return ready();
    }
}
