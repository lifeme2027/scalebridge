using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.Core.Tests;

public class WinConnectionStateMachineTests
{
    [Fact]
    public void Connected_To_Dropped_ReportsOnce()
    {
        var sm = new WinConnectionStateMachine();
        var first = sm.TryMarkDropped("lost");
        Assert.Equal(ConnectionState.Dropped, first!.State);
        Assert.Equal(BleErrorKind.DeviceDisconnected, first.ErrorKind);
        Assert.Null(sm.TryMarkDropped("again"));
        Assert.Equal(ConnectionState.Dropped, sm.State);
    }

    [Fact]
    public void Disconnected_AfterDropped_IsSwallowed()
    {
        var sm = new WinConnectionStateMachine();
        sm.TryMarkDropped("lost");
        Assert.Null(sm.TryMarkDisconnected());
        Assert.Equal(ConnectionState.Dropped, sm.State);
    }

    [Fact]
    public void Dropped_AfterDisconnected_IsSwallowed()
    {
        var sm = new WinConnectionStateMachine();
        Assert.NotNull(sm.TryMarkDisconnected("bye"));
        Assert.Null(sm.TryMarkDropped("late event"));
        Assert.True(sm.IsTerminal);
    }

    [Fact]
    public void Connecting_To_Connected_OnlyOnce()
    {
        var sm = new WinConnectionStateMachine(ConnectionState.Connecting);
        Assert.NotNull(sm.TryMarkConnected());
        Assert.Null(sm.TryMarkConnected());
        Assert.False(sm.IsTerminal);
    }

    [Fact]
    public void ConcurrentTerminalTransitions_YieldExactlyOneChange()
    {
        var sm = new WinConnectionStateMachine();
        var winners = 0;
        Parallel.For(0, 64, i =>
        {
            var c = i % 2 == 0 ? sm.TryMarkDropped("x") : sm.TryMarkDisconnected("y");
            if (c is not null) Interlocked.Increment(ref winners);
        });
        Assert.Equal(1, winners);
    }
}

public class WinAdvertisementCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WinAdvertisement Ad(ulong addr, string? name = null, int rssi = -60, Guid[]? services = null, params WinManufacturerData[] m) =>
        new(addr, name, rssi, services ?? Array.Empty<Guid>(), m);

    [Fact]
    public void NameFromScanResponse_IsKeptAfterNamelessAdvertisement()
    {
        var c = new WinAdvertisementCache();
        c.Merge(Ad(1, "Scale"), T0);
        var d = c.Merge(Ad(1, null), T0.AddSeconds(1));
        Assert.Equal("Scale", d.Name);
    }

    [Fact]
    public void Services_AreUnionedWithoutDuplicates()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var c = new WinAdvertisementCache();
        c.Merge(Ad(1, services: new[] { a }), T0);
        var d = c.Merge(Ad(1, services: new[] { a, b }), T0);
        Assert.Equal(new[] { a, b }, d.ServiceUuids);
    }

    [Fact]
    public void Rssi127_MeansNotAvailable_AndKeepsPreviousValue()
    {
        var c = new WinAdvertisementCache();
        Assert.Null(c.Merge(Ad(1, rssi: 127), T0).Rssi);
        c.Merge(Ad(1, rssi: -70), T0);
        Assert.Equal(-70, c.Merge(Ad(1, rssi: 127), T0).Rssi);
    }

    [Fact]
    public void ManufacturerData_LatestPerCompanyWins_AndIsCopied()
    {
        var c = new WinAdvertisementCache();
        var payload = new byte[] { 1, 2 };
        var d1 = c.Merge(Ad(1, m: new WinManufacturerData(0x1234, payload)), T0);
        payload[0] = 99; // 调用方之后修改原数组，不应影响已返回的快照
        Assert.Equal(new byte[] { 1, 2 }, d1.ManufacturerData[0x1234]);
        var d2 = c.Merge(Ad(1, m: new WinManufacturerData(0x1234, new byte[] { 7 })), T0);
        Assert.Equal(new byte[] { 7 }, d2.ManufacturerData[0x1234]);
    }

    [Fact]
    public void Snapshots_AreIndependentCopies()
    {
        var c = new WinAdvertisementCache();
        var d1 = c.Merge(Ad(1, m: new WinManufacturerData(1, new byte[] { 1 })), T0);
        d1.ManufacturerData[1][0] = 55;
        var d2 = c.Merge(Ad(1), T0);
        Assert.Equal((byte)1, d2.ManufacturerData[1][0]);
    }

    [Fact]
    public void Capacity_EvictsLeastRecentlySeen()
    {
        var c = new WinAdvertisementCache(capacity: 2);
        c.Merge(Ad(1, "one"), T0);
        c.Merge(Ad(2, "two"), T0.AddSeconds(1));
        c.Merge(Ad(1), T0.AddSeconds(2));          // 1 比 2 新
        c.Merge(Ad(3, "three"), T0.AddSeconds(3)); // 应淘汰 2
        Assert.Equal(2, c.Count);
        Assert.Equal("one", c.Merge(Ad(1), T0.AddSeconds(4)).Name);
        Assert.Null(c.Merge(Ad(2), T0.AddSeconds(5)).Name); // 2 被淘汰后名字丢失
    }

    [Fact]
    public void DeviceId_IsMacStyle_AndNotDemo()
    {
        var d = new WinAdvertisementCache().Merge(Ad(0xAABBCCDDEEFF), T0);
        Assert.Equal("AA:BB:CC:DD:EE:FF", d.Id);
        Assert.False(d.IsDemo);
    }
}
