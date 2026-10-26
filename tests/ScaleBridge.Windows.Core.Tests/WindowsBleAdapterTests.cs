using System.Runtime.InteropServices;
using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Core.Tests.Support;
using ScaleBridge.Windows.Platform;
using static ScaleBridge.Windows.Core.Tests.Support.TestData;

namespace ScaleBridge.Windows.Core.Tests;

public class WindowsBleAdapterBasicsTests
{
    [Fact]
    public void Identity_IsWindows_NotDemo_AndLabelledExperimental()
    {
        var a = new WindowsBleAdapter(new FakePlatform());
        Assert.Equal("windows", a.Id);
        Assert.False(a.IsDemo);
        Assert.Contains("experimental", a.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullPlatform_IsRejected() => Assert.Throws<ArgumentNullException>(() => new WindowsBleAdapter(null!));

    [Theory]
    [InlineData(WinRadioState.On, BleAvailability.Available)]
    [InlineData(WinRadioState.Off, BleAvailability.Unavailable)]
    [InlineData(WinRadioState.NoAdapter, BleAvailability.Unavailable)]
    [InlineData(WinRadioState.Disabled, BleAvailability.Unavailable)]
    public async Task Availability_FollowsRadioState(WinRadioState state, BleAvailability expected)
    {
        var a = new WindowsBleAdapter(new FakePlatform { Radio = state });
        Assert.Equal(expected, await a.GetAvailabilityAsync());
    }

    [Fact]
    public async Task Availability_AccessDenied_IsPermissionDenied()
    {
        var a = new WindowsBleAdapter(new FakePlatform { RadioThrows = new UnauthorizedAccessException() });
        Assert.Equal(BleAvailability.PermissionDenied, await a.GetAvailabilityAsync());
    }

    [Fact]
    public async Task Availability_OtherFailure_IsUnavailable_NotAnException()
    {
        var a = new WindowsBleAdapter(new FakePlatform { RadioThrows = new COMException("x", WinErrorMapper.ErrorNotReady) });
        Assert.Equal(BleAvailability.Unavailable, await a.GetAvailabilityAsync());
    }

    [Fact]
    public async Task Availability_HungQuery_TimesOutToUnavailable()
    {
        var time = new ManualTimeProvider();
        var p = new FakePlatform { RadioOverride = _ => new TaskCompletionSource<WinRadioState>().Task };
        var a = new WindowsBleAdapter(p, time: time);
        var t = a.GetAvailabilityAsync();
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BleAvailability.Unavailable, await Within(t));
    }

    [Fact]
    public async Task Availability_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var a = new WindowsBleAdapter(new FakePlatform());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.GetAvailabilityAsync(cts.Token));
    }
}

public class WindowsBleAdapterScanTests
{
    private static WinAdvertisement Ad(ulong addr, string? name = null, Guid[]? services = null, int rssi = -55) =>
        new(addr, name, rssi, services ?? Array.Empty<Guid>(), Array.Empty<WinManufacturerData>());

    private static async Task<List<AdvertisedDevice>> Collect(IAsyncEnumerable<AdvertisedDevice> stream, Func<Task> produce, int count)
    {
        var list = new List<AdvertisedDevice>();
        using var cts = new CancellationTokenSource(5000);
        var producer = Task.Run(async () => { await Task.Delay(20); await produce(); });
        await foreach (var d in stream.WithCancellation(cts.Token))
        {
            list.Add(d);
            if (list.Count == count) break;
        }
        await producer;
        return list;
    }

    [Fact]
    public async Task Scan_MergesAdvertisementAndScanResponse()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var got = await Collect(a.ScanAsync(new ScanOptions()), () =>
        {
            p.Watcher.Raise(Ad(1, services: new[] { WeightService }));
            p.Watcher.Raise(Ad(1, name: "Scale 1"));
            return Task.CompletedTask;
        }, 2);
        Assert.Null(got[0].Name);
        Assert.Equal("Scale 1", got[1].Name);
        Assert.Contains(WeightService, got[1].ServiceUuids);
        Assert.All(got, d => Assert.False(d.IsDemo));
    }

    [Fact]
    public async Task Scan_AppliesServiceFilter_OnMergedServices()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var got = await Collect(a.ScanAsync(new ScanOptions { ServiceFilter = new[] { WeightService } }), () =>
        {
            p.Watcher.Raise(Ad(2, "other"));                                   // 被过滤
            p.Watcher.Raise(Ad(1, services: new[] { Guid.NewGuid() }));        // 被过滤
            p.Watcher.Raise(Ad(3, "scale", new[] { WeightService }));          // 通过
            return Task.CompletedTask;
        }, 1);
        Assert.Equal("00:00:00:00:00:03", got[0].Id);
    }

    [Fact]
    public async Task Scan_StartsWatcher_AndCleansUpOnConsumerCancel()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(async () => { await foreach (var _ in a.ScanAsync(new ScanOptions(), cts.Token)) { } });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (p.Watcher.StartCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Equal(1, p.Watcher.StartCount);
        cts.Cancel();
        await Within(run); // 取消 = 正常结束
        Assert.Equal(1, p.Watcher.StopCount);
        Assert.Equal(1, p.Watcher.DisposeCount);
        Assert.Equal(0, p.Watcher.ReceivedHandlerCount);
    }

    [Fact]
    public async Task Scan_RadioOff_ThrowsBluetoothUnavailable_WithoutStartingWatcher()
    {
        var p = new FakePlatform { Radio = WinRadioState.Off };
        var a = new WindowsBleAdapter(p);
        await Assert.ThrowsAsync<BluetoothUnavailableException>(async () =>
        {
            await foreach (var _ in a.ScanAsync(new ScanOptions())) { }
        });
        Assert.Equal(0, p.Watcher.StartCount);
    }

    [Fact]
    public async Task Scan_StartFailure_IsMapped_AndWatcherDisposed()
    {
        var p = new FakePlatform();
        p.Watcher.StartThrows = new UnauthorizedAccessException();
        var a = new WindowsBleAdapter(p);
        await Assert.ThrowsAsync<BlePermissionException>(async () =>
        {
            await foreach (var _ in a.ScanAsync(new ScanOptions())) { }
        });
        Assert.Equal(1, p.Watcher.DisposeCount);
    }

    [Fact]
    public async Task Scan_AbortedBySystem_SurfacesMappedError()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var run = Task.Run(async () => { await foreach (var _ in a.ScanAsync(new ScanOptions())) { } });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (p.Watcher.StartCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        p.Watcher.RaiseStopped(WinBluetoothError.RadioNotAvailable); // 扫描途中用户关了蓝牙
        await Assert.ThrowsAsync<BluetoothUnavailableException>(() => Within(run));
        Assert.Equal(1, p.Watcher.DisposeCount);
    }

    [Fact]
    public async Task Scan_NormalStop_EndsStreamWithoutError()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var run = Task.Run(async () => { await foreach (var _ in a.ScanAsync(new ScanOptions())) { } });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (p.Watcher.StartCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        p.Watcher.RaiseStopped(WinBluetoothError.Success);
        await Within(run);
    }

    [Fact]
    public async Task Scan_Rssi127_IsReportedAsUnknown()
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var got = await Collect(a.ScanAsync(new ScanOptions()), () => { p.Watcher.Raise(Ad(1, rssi: 127)); return Task.CompletedTask; }, 1);
        Assert.Null(got[0].Rssi);
    }
}

public class WindowsBleAdapterConnectTests
{
    [Fact]
    public async Task Connect_OpensParsedAddress_AndReturnsLiveConnection()
    {
        var (device, _, _, _) = WeightScale();
        var p = new FakePlatform { DeviceToOpen = device };
        var a = new WindowsBleAdapter(p);
        await using var conn = await a.ConnectAsync("AA:BB:CC:DD:EE:FF");
        Assert.Equal(new[] { 0xAABBCCDDEEFFUL }, p.OpenedAddresses);
        Assert.Equal("AA:BB:CC:DD:EE:FF", conn.DeviceId);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.Single(await conn.DiscoverServicesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mac")]
    [InlineData("AA:BB:CC")]
    public async Task Connect_InvalidId_FailsBeforeTouchingPlatform(string id)
    {
        var p = new FakePlatform();
        var a = new WindowsBleAdapter(p);
        var ex = await Assert.ThrowsAsync<BleException>(() => a.ConnectAsync(id));
        Assert.Equal(BleErrorKind.Other, ex.Kind);
        Assert.Empty(p.OpenedAddresses);
    }

    [Fact]
    public async Task Connect_RadioOff_ThrowsBluetoothUnavailable()
    {
        var p = new FakePlatform { Radio = WinRadioState.Off, DeviceToOpen = new FakeDevice() };
        var a = new WindowsBleAdapter(p);
        await Assert.ThrowsAsync<BluetoothUnavailableException>(() => a.ConnectAsync("AA:BB:CC:DD:EE:FF"));
        Assert.Empty(p.OpenedAddresses);
    }

    [Fact]
    public async Task Connect_UnknownDevice_IsDeviceDisconnected()
    {
        var a = new WindowsBleAdapter(new FakePlatform { DeviceToOpen = null });
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => a.ConnectAsync("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public async Task Connect_Hung_TimesOutAtRequestedTimeout_AndDisposesLateDevice()
    {
        var time = new ManualTimeProvider();
        var open = new TaskCompletionSource<IWinGattDevice?>();
        var p = new FakePlatform { OpenOverride = (_, _) => open.Task };
        var a = new WindowsBleAdapter(p, time: time);
        var t = a.ConnectAsync("AA:BB:CC:DD:EE:FF", TimeSpan.FromSeconds(8));
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(7));
        Assert.False(t.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<BleTimeoutException>(() => Within(t));

        var late = new FakeDevice();
        open.SetResult(late); // WinRT 在超时之后才把设备交回来 → 必须释放，否则一直占着连接
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (late.DisposeCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Equal(1, late.DisposeCount);
    }

    [Fact]
    public async Task Connect_DefaultTimeout_IsTenSeconds()
    {
        var time = new ManualTimeProvider();
        var p = new FakePlatform { OpenOverride = (_, _) => new TaskCompletionSource<IWinGattDevice?>().Task };
        var a = new WindowsBleAdapter(p, time: time);
        var t = a.ConnectAsync("AA:BB:CC:DD:EE:FF");
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(9));
        Assert.False(t.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<BleTimeoutException>(() => Within(t));
    }

    [Fact]
    public async Task Connect_PlatformException_IsMapped()
    {
        var p = new FakePlatform { OpenOverride = (_, _) => throw new COMException("x", WinErrorMapper.EAccessDenied) };
        var a = new WindowsBleAdapter(p);
        await Assert.ThrowsAsync<BlePermissionException>(() => a.ConnectAsync("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public async Task Connect_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var a = new WindowsBleAdapter(new FakePlatform { DeviceToOpen = new FakeDevice() });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.ConnectAsync("AA:BB:CC:DD:EE:FF", ct: cts.Token));
    }

    [Fact]
    public async Task ReconnectAfterDrop_UsesNewConnection_AndOldOneStaysDropped()
    {
        var (first, _, _, _) = WeightScale();
        var (second, _, m2, _) = WeightScale();
        var p = new FakePlatform { DeviceToOpen = first };
        var a = new WindowsBleAdapter(p);

        var c1 = await a.ConnectAsync("AA:BB:CC:DD:EE:FF");
        first.RaiseConnection(false);
        Assert.Equal(ConnectionState.Dropped, c1.State);
        await c1.DisposeAsync();
        Assert.Equal(1, first.DisposeCount);

        p.DeviceToOpen = second;
        await using var c2 = await a.ConnectAsync("AA:BB:CC:DD:EE:FF");
        Assert.Equal(ConnectionState.Connected, c2.State);
        Assert.Equal(ConnectionState.Dropped, c1.State);
        var services = await c2.DiscoverServicesAsync();
        Assert.Single(services);
        Assert.Equal(2, p.OpenedAddresses.Count);
        Assert.Equal(0, m2.HandlerCount);
    }

    [Fact]
    public async Task EndToEnd_ConnectSubscribeReceiveDisconnect()
    {
        var (device, _, m, _) = WeightScale();
        var a = new WindowsBleAdapter(new FakePlatform { DeviceToOpen = device });
        var conn = await a.ConnectAsync("AA:BB:CC:DD:EE:FF");
        var received = new TaskCompletionSource<byte[]>();
        var run = Task.Run(async () =>
        {
            await foreach (var n in conn.SubscribeAsync(WeightService, WeightMeasurement)) received.TrySetResult(n.Value);
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (m.HandlerCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        m.Emit(new byte[] { 0x02, 0x10, 0x27 });
        Assert.Equal(new byte[] { 0x02, 0x10, 0x27 }, await Within(received.Task));
        await conn.DisconnectAsync();
        await Within(run);
        Assert.Equal(1, device.DisposeCount);
    }
}
