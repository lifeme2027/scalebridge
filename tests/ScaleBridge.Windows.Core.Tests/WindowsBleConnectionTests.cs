using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Core.Tests.Support;
using ScaleBridge.Windows.Platform;
using static ScaleBridge.Windows.Core.Tests.Support.TestData;

namespace ScaleBridge.Windows.Core.Tests;

public class WindowsBleConnectionDiscoveryTests
{
    [Fact]
    public async Task Discover_ReturnsServicesAndCharacteristics_WithCoreFlagValues()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        var services = await c.DiscoverServicesAsync();
        var s = Assert.Single(services);
        Assert.Equal(WeightService, s.Uuid);
        Assert.Contains(s.Characteristics, x => x.Uuid == WeightMeasurement && x.Properties == GattCharacteristicProperties.Indicate);
        Assert.Contains(s.Characteristics, x => x.Uuid == FeatureChar && x.Properties == GattCharacteristicProperties.Read);
    }

    [Fact]
    public async Task Discover_FindsBothScaleAndBodyCompositionServices()
    {
        var (device, _, _, _) = WeightScale();
        device.With(new FakeService(BodyService, new FakeCharacteristic(BodyMeasurement, GattCharacteristicProperties.Indicate)));
        await using var c = Connect(device);
        var services = await c.DiscoverServicesAsync();
        Assert.Equal(2, services.Count);
    }

    [Fact]
    public async Task Discover_Twice_QueriesDeviceOnlyOnce()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        await c.DiscoverServicesAsync();
        var queries = device.ServiceQueries;
        await c.DiscoverServicesAsync();
        Assert.Equal(queries, device.ServiceQueries);
    }

    [Fact]
    public async Task Discover_EmptyResult_IsNotCached_SoRetryWorks()
    {
        var device = new FakeDevice();
        await using var c = Connect(device);
        Assert.Empty(await c.DiscoverServicesAsync());
        device.With(new FakeService(WeightService, new FakeCharacteristic(WeightMeasurement, GattCharacteristicProperties.Indicate)));
        Assert.Single(await c.DiscoverServicesAsync());
    }

    [Fact]
    public async Task Discover_Unreachable_ThrowsDeviceDisconnected()
    {
        var device = new FakeDevice
        {
            ServicesOverride = (_, _) => Task.FromResult(WinGattResult<IReadOnlyList<IWinGattService>>.Fail(WinGattStatus.Unreachable)),
        };
        await using var c = Connect(device);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.DiscoverServicesAsync());
    }

    [Fact]
    public async Task Discover_Watchdog_FiresOnHungWinRtCall()
    {
        var time = new ManualTimeProvider();
        var device = new FakeDevice { ServicesOverride = (_, _) => new TaskCompletionSource<WinGattResult<IReadOnlyList<IWinGattService>>>().Task };
        await using var c = Connect(device, time);
        var t = c.DiscoverServicesAsync();
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(9));
        var ex = await Assert.ThrowsAsync<BleTimeoutException>(() => Within(t));
        Assert.Contains("Discover", ex.Message);
    }

    [Fact]
    public async Task Discover_CharacteristicFailure_DisposesServicesFound()
    {
        var (device, service, _, _) = WeightScale();
        service.CharacteristicsOverride = _ => Task.FromResult(WinGattResult<IReadOnlyList<IWinGattCharacteristic>>.Fail(WinGattStatus.AccessDenied));
        await using var c = Connect(device);
        await Assert.ThrowsAsync<BlePermissionException>(() => c.DiscoverServicesAsync());
        Assert.Equal(1, service.DisposeCount);
    }

    [Fact]
    public async Task Discover_Cancelled_ThrowsOperationCanceled()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.DiscoverServicesAsync(cts.Token));
    }
}

public class WindowsBleConnectionReadTests
{
    [Fact]
    public async Task Read_ReturnsBytes_AsIndependentCopy()
    {
        var (device, _, _, feature) = WeightScale();
        await using var c = Connect(device);
        var v1 = await c.ReadAsync(WeightService, FeatureChar);
        v1[0] = 0xFF;
        var v2 = await c.ReadAsync(WeightService, FeatureChar);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, v2);
        Assert.Equal((byte)1, feature.ReadValue[0]);
    }

    [Fact]
    public async Task Read_UnknownCharacteristic_IsNoSupportedService()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        var ex = await Assert.ThrowsAsync<BleException>(() => c.ReadAsync(WeightService, Guid.NewGuid()));
        Assert.Equal(BleErrorKind.NoSupportedService, ex.Kind);
    }

    [Fact]
    public async Task Read_OfIndicateOnlyCharacteristic_IsRejectedClearly()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        var ex = await Assert.ThrowsAsync<BleException>(() => c.ReadAsync(WeightService, WeightMeasurement));
        Assert.Equal(BleErrorKind.Other, ex.Kind);
        Assert.Contains("read", ex.Message);
    }

    [Fact]
    public async Task Read_ProtocolErrorNeedsPairing_MapsToPermission()
    {
        var (device, _, _, feature) = WeightScale();
        feature.ReadOverride = _ => Task.FromResult(WinGattResult<byte[]>.Fail(WinGattStatus.ProtocolError, 0x0F));
        await using var c = Connect(device);
        await Assert.ThrowsAsync<BlePermissionException>(() => c.ReadAsync(WeightService, FeatureChar));
    }

    [Fact]
    public async Task Read_Hung_TimesOut()
    {
        var time = new ManualTimeProvider();
        var (device, _, _, feature) = WeightScale();
        await using var c = Connect(device, time);
        await c.DiscoverServicesAsync();
        feature.ReadOverride = _ => new TaskCompletionSource<WinGattResult<byte[]>>().Task;
        var t = c.ReadAsync(WeightService, FeatureChar);
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAsync<BleTimeoutException>(() => Within(t));
    }
}

public class WindowsBleConnectionSubscribeTests
{
    private static async Task<List<GattNotification>> Take(IAsyncEnumerable<GattNotification> stream, int count, int ms = 5000)
    {
        var list = new List<GattNotification>();
        using var cts = new CancellationTokenSource(ms);
        await foreach (var n in stream.WithCancellation(cts.Token))
        {
            list.Add(n);
            if (list.Count == count) break;
        }
        return list;
    }

    private static async Task WaitForHandlers(FakeCharacteristic ch, int expected = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ch.HandlerCount < expected && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Equal(expected, ch.HandlerCount);
    }

    [Fact]
    public async Task Subscribe_DeliversNotifications_WithServiceAndCharacteristic()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 2);
        await WaitForHandlers(m);
        m.Emit(new byte[] { 1, 2, 3 });
        m.Emit(new byte[] { 4, 5 });
        var got = await take;
        Assert.Equal(new byte[] { 1, 2, 3 }, got[0].Value);
        Assert.Equal(new byte[] { 4, 5 }, got[1].Value);
        Assert.Equal(WeightService, got[0].Service);
        Assert.Equal(WeightMeasurement, got[0].Characteristic);
    }

    [Fact]
    public async Task Subscribe_NotificationBytes_SurviveAReusedBuffer()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 3);
        await WaitForHandlers(m);
        var shared = new byte[4]; // 模拟 WinRT 复用同一块 IBuffer
        for (byte i = 1; i <= 3; i++)
        {
            Array.Fill(shared, i);
            m.Emit(shared);
        }
        Array.Fill(shared, (byte)0xEE); // 回调结束后缓冲被覆盖
        var got = await take;
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, got[0].Value);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, got[1].Value);
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, got[2].Value);
    }

    [Fact]
    public async Task Subscribe_PrefersIndicate_WhenBothSupported()
    {
        var m = new FakeCharacteristic(WeightMeasurement, GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate);
        var device = new FakeDevice().With(new FakeService(WeightService, m));
        await using var c = Connect(device);
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await WaitForHandlers(m);
        m.Emit(new byte[] { 9 });
        await take;
        Assert.Equal(WinCccdValue.Indicate, m.CccdWrites[0]);
        Assert.DoesNotContain(WinCccdValue.Notify, m.CccdWrites);
    }

    [Fact]
    public async Task Subscribe_UsesNotify_WhenOnlyNotify()
    {
        var m = new FakeCharacteristic(WeightMeasurement, GattCharacteristicProperties.Notify);
        var device = new FakeDevice().With(new FakeService(WeightService, m));
        await using var c = Connect(device);
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await WaitForHandlers(m);
        m.Emit(new byte[] { 9 });
        await take;
        Assert.Equal(WinCccdValue.Notify, m.CccdWrites[0]);
    }

    [Fact]
    public async Task Subscribe_ReadOnlyCharacteristic_IsRejected()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        await Assert.ThrowsAsync<BleException>(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, FeatureChar)) { }
        });
    }

    [Fact]
    public async Task DuplicateSubscribe_WritesCccdOnce_AndBothReceive()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        var a = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await WaitForHandlers(m);
        var b = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !(m.HandlerCount == 1 && m.CccdWrites.Count == 1)) await Task.Delay(1);
        await Task.Delay(30); // 给第二个订阅者挂上的时间
        m.Emit(new byte[] { 7 });
        Assert.Equal(new byte[] { 7 }, (await a)[0].Value);
        Assert.Equal(new byte[] { 7 }, (await b)[0].Value);
        lock (m.CccdWrites) Assert.Equal(1, m.CccdWrites.Count(x => x == WinCccdValue.Indicate)); // 两个订阅者只写一次 CCCD
    }

    [Fact]
    public async Task Unsubscribe_LastSubscriber_DisablesCccd_AndUnhooksHandler()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await WaitForHandlers(m);
        m.Emit(new byte[] { 1 });
        await take; // Take 提前 break → 迭代器被释放 → 取消订阅
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && m.HandlerCount != 0) await Task.Delay(1);
        Assert.Equal(0, m.HandlerCount);
        Assert.Equal(new[] { WinCccdValue.Indicate, WinCccdValue.None }, m.CccdWrites);
    }

    [Fact]
    public async Task Unsubscribe_OneOfTwo_KeepsCccdEnabled()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        using var cts = new CancellationTokenSource();
        var stayer = Take(c.SubscribeAsync(WeightService, WeightMeasurement, cts.Token), 2);
        await WaitForHandlers(m);
        var leaver = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await Task.Delay(50);
        m.Emit(new byte[] { 1 });
        await leaver; // 第二个订阅者离开
        await Task.Delay(50);
        Assert.Equal(1, m.HandlerCount);
        Assert.Equal(new[] { WinCccdValue.Indicate }, m.CccdWrites);
        m.Emit(new byte[] { 2 });
        Assert.Equal(2, (await stayer).Count);
    }

    [Fact]
    public async Task Resubscribe_AfterUnsubscribe_WritesCccdAgain()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        for (var round = 0; round < 2; round++)
        {
            var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
            await WaitForHandlers(m);
            m.Emit(new byte[] { (byte)round });
            Assert.Equal((byte)round, (await take)[0].Value[0]);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && m.HandlerCount != 0) await Task.Delay(1);
        }
        Assert.Equal(new[] { WinCccdValue.Indicate, WinCccdValue.None, WinCccdValue.Indicate, WinCccdValue.None }, m.CccdWrites);
    }

    [Fact]
    public async Task CccdWriteFailure_MapsError_AndUnhooksHandler()
    {
        var (device, _, m, _) = WeightScale();
        m.CccdOverride = (_, _) => Task.FromResult(WinGattResult<bool>.Fail(WinGattStatus.ProtocolError, 0x05));
        await using var c = Connect(device);
        await Assert.ThrowsAsync<BlePermissionException>(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
        Assert.Equal(0, m.HandlerCount);
    }

    [Fact]
    public async Task CccdWrite_Hung_TimesOut_AndUnhooks()
    {
        var time = new ManualTimeProvider();
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device, time);
        await c.DiscoverServicesAsync();
        m.CccdOverride = (_, _) => new TaskCompletionSource<WinGattResult<bool>>().Task;
        var t = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 1);
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAsync<BleTimeoutException>(() => Within(t));
        Assert.Equal(0, m.HandlerCount);
    }

    [Fact]
    public async Task ConsumerCancellation_EndsStreamNormally()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        using var cts = new CancellationTokenSource();
        var count = 0;
        var run = Task.Run(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement, cts.Token)) count++;
        });
        await WaitForHandlers(m);
        m.Emit(new byte[] { 1 });
        cts.Cancel();
        await Within(run);
        Assert.InRange(count, 0, 1);
    }

    [Fact]
    public async Task SlowConsumer_DoesNotBlockCallbackThread_AndKeepsNewest()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device, options: new WindowsBleOptions { NotificationBuffer = 3 });
        var take = Take(c.SubscribeAsync(WeightService, WeightMeasurement), 3);
        await WaitForHandlers(m);
        // 在消费者读取前先灌入 10 条：回调线程不阻塞，缓冲只保留最新 3 条（可能已被读走一部分）
        for (byte i = 1; i <= 10; i++) m.Emit(new[] { i });
        var got = await take;
        Assert.Equal(3, got.Count);
        Assert.True(got[^1].Value[0] >= 8 || got[^1].Value[0] == 3, "应保留较新的通知");
    }
}

public class WindowsBleConnectionLifecycleTests
{
    private static async Task WaitForHandlers(FakeCharacteristic ch)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ch.HandlerCount < 1 && DateTime.UtcNow < deadline) await Task.Delay(1);
    }

    [Fact]
    public async Task Drop_PublishesDroppedOnce_AndFailsActiveStream()
    {
        var (device, _, m, _) = WeightScale();
        await using var c = Connect(device);
        var changes = new List<ConnectionStateChange>();
        c.StateChanged += (_, e) => { lock (changes) changes.Add(e); };
        var run = Task.Run(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
        await WaitForHandlers(m);
        device.RaiseConnection(false);
        device.RaiseConnection(false); // 重复事件
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => Within(run));
        lock (changes)
        {
            var only = Assert.Single(changes);
            Assert.Equal(ConnectionState.Dropped, only.State);
            Assert.Equal(BleErrorKind.DeviceDisconnected, only.ErrorKind);
        }
        Assert.Equal(ConnectionState.Dropped, c.State);
    }

    [Fact]
    public async Task Drop_ThenOperations_FailWithDeviceDisconnected()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        await c.DiscoverServicesAsync();
        device.RaiseConnection(false);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.ReadAsync(WeightService, FeatureChar));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
    }

    [Fact]
    public async Task Drop_ThenDispose_DoesNotPublishSecondTerminalState()
    {
        var (device, _, _, _) = WeightScale();
        var c = Connect(device);
        var changes = new List<ConnectionState>();
        c.StateChanged += (_, e) => changes.Add(e.State);
        device.RaiseConnection(false);
        await c.DisposeAsync();
        Assert.Equal(new[] { ConnectionState.Dropped }, changes);
    }

    [Fact]
    public async Task ReconnectingEventAfterDrop_DoesNotResurrectTheConnection()
    {
        var (device, _, _, _) = WeightScale();
        await using var c = Connect(device);
        device.RaiseConnection(false);
        device.RaiseConnection(true);
        Assert.Equal(ConnectionState.Dropped, c.State);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_PublishesDisconnectedOnce_AndReleasesDevice()
    {
        var (device, service, _, _) = WeightScale();
        var c = Connect(device);
        await c.DiscoverServicesAsync();
        var changes = new List<ConnectionState>();
        c.StateChanged += (_, e) => changes.Add(e.State);
        await c.DisposeAsync();
        await c.DisposeAsync();
        await c.DisconnectAsync();
        Assert.Equal(new[] { ConnectionState.Disconnected }, changes);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, service.DisposeCount);
        Assert.Equal(0, device.ConnectionHandlerCount);
    }

    [Fact]
    public async Task ConcurrentDispose_ReleasesExactlyOnce()
    {
        var (device, _, _, _) = WeightScale();
        var c = Connect(device);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => c.DisposeAsync().AsTask())));
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(ConnectionState.Disconnected, c.State);
    }

    [Fact]
    public async Task Operations_AfterDispose_ThrowDeviceDisconnected()
    {
        var (device, _, _, _) = WeightScale();
        var c = Connect(device);
        await c.DisposeAsync();
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.DiscoverServicesAsync());
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.ReadAsync(WeightService, FeatureChar));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
    }

    [Fact]
    public async Task Dispose_EndsActiveStreamNormally_NotAsError()
    {
        var (device, _, m, _) = WeightScale();
        var c = Connect(device);
        var run = Task.Run(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
        await WaitForHandlers(m);
        await c.DisposeAsync();
        await Within(run); // 不抛异常即通过
        Assert.Equal(0, m.HandlerCount);
    }

    [Fact]
    public async Task Dispose_WhileCccdWriteIsPending_DoesNotDeadlock_AndAbortsSubscribe()
    {
        var time = new ManualTimeProvider();
        var (device, _, m, _) = WeightScale();
        var c = Connect(device, time);
        await c.DiscoverServicesAsync();
        var started = new TaskCompletionSource();
        m.CccdOverride = (_, ct) => { started.TrySetResult(); return new TaskCompletionSource<WinGattResult<bool>>().Task; };
        var sub = Task.Run(async () =>
        {
            await foreach (var _ in c.SubscribeAsync(WeightService, WeightMeasurement)) { }
        });
        await Within(started.Task);
        var dispose = c.DisposeAsync().AsTask();
        // 释放会取消正在等待的看门狗，因此不需要推进时间也能完成（不会等满 9 秒的 CCCD 超时）
        await Within(dispose);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => Within(sub));
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(0, m.HandlerCount);
    }

    [Fact]
    public async Task DropWhileDiscovering_SurfacesDeviceDisconnected()
    {
        var gate = new TaskCompletionSource<WinGattResult<IReadOnlyList<IWinGattService>>>();
        var device = new FakeDevice { ServicesOverride = (_, _) => gate.Task };
        await using var c = Connect(device);
        var t = c.DiscoverServicesAsync();
        await Task.Delay(30);
        device.RaiseConnection(false);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => Within(t));
    }

    [Fact]
    public async Task ThrowingStateChangedHandler_DoesNotBreakTheConnection()
    {
        var (device, _, _, _) = WeightScale();
        var c = Connect(device);
        var seen = 0;
        c.StateChanged += (_, _) => throw new InvalidOperationException("subscriber bug");
        c.StateChanged += (_, _) => seen++;
        device.RaiseConnection(false);
        await c.DisposeAsync();
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Dispose_WhenWinRtDisposeThrows_IsSwallowed()
    {
        var device = new ThrowingDisposeDevice();
        var c = Connect(device);
        await c.DisposeAsync(); // 不应抛出
    }

    private sealed class ThrowingDisposeDevice : IWinGattDevice
    {
        public ulong BluetoothAddress => 1;
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<WinGattResult<IReadOnlyList<IWinGattService>>> GetServicesForUuidAsync(Guid serviceUuid, CancellationToken ct) =>
            Task.FromResult(WinGattResult<IReadOnlyList<IWinGattService>>.Ok(Array.Empty<IWinGattService>()));
        public void Dispose() => throw new InvalidOperationException("WinRT dispose failed");
    }
}
