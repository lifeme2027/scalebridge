using ScaleBridge.Core.Ble;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

/// <summary>
/// 在私有 dbus-daemon 上用 Tmds.DBus.Protocol 写的假 org.bluez 服务验证 TmdsBluezBus 的线上编解码与错误映射。
/// 这只证明实现与 D-Bus 规范/BlueZ 文档的签名自洽，不等于在真实 BlueZ 上验证过。
/// </summary>
public class TmdsBluezBusIntegrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(BluezDbusHarness Harness, TmdsBluezBus Bus)> StartAsync()
    {
        var harness = await BluezDbusHarness.StartAsync();
        try
        {
            var bus = await TmdsBluezBus.ConnectAsync(harness.Address, CancellationToken.None);
            return (harness, bus);
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    [DbusFact]
    public async Task GetManagedObjects_ParsesAllNormalizedTypes()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        var objects = await bus.GetManagedObjectsAsync(CancellationToken.None);

        Assert.Equal(4, objects.Count);
        var adapter = objects.Single(o => o.Path == FakeBluezService.Adapter).Interfaces[BluezNames.Adapter1];
        Assert.Equal("hci0", adapter["Name"]);
        Assert.Equal(true, adapter["Powered"]);

        var device = objects.Single(o => o.Path == FakeBluezService.Device).Interfaces[BluezNames.Device1];
        Assert.Equal(FakeBluezService.DeviceMac, device["Address"]);
        Assert.Equal((short)-61, device["RSSI"]);
        Assert.Equal(false, device["Connected"]);
        Assert.Equal(true, device["ServicesResolved"]);
        Assert.Equal(FakeBluezService.Adapter, device["Adapter"]);
        Assert.Equal(0x1234u, device["Class"]);
        Assert.False(device.ContainsKey("Weird"));
        Assert.Equal(new[] { FakeBluezService.ServiceUuid }, Assert.IsType<string[]>(device["UUIDs"]));
        var manufacturer = Assert.IsAssignableFrom<IReadOnlyDictionary<ushort, byte[]>>(device["ManufacturerData"]);
        Assert.Equal(new byte[] { 0x10, 0x05 }, manufacturer[0x004C]);
        var serviceData = Assert.IsAssignableFrom<IReadOnlyDictionary<string, byte[]>>(device["ServiceData"]);
        Assert.Equal(new byte[] { 0xAA }, serviceData[FakeBluezService.ServiceUuid]);

        var characteristic = objects.Single(o => o.Path == FakeBluezService.Characteristic).Interfaces[BluezNames.GattCharacteristic1];
        Assert.Equal(FakeBluezService.CharacteristicUuid, characteristic["UUID"]);
        Assert.Equal(new[] { "indicate" }, Assert.IsType<string[]>(characteristic["Flags"]));
    }

    [DbusFact]
    public async Task AdapterAndDeviceCalls_ReachTheService_WithFilterArguments()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        await bus.SetDiscoveryFilterAsync(
            FakeBluezService.Adapter,
            new BluezDiscoveryFilter(["0000181d-0000-1000-8000-00805f9b34fb"], "le", true),
            CancellationToken.None);
        await bus.StartDiscoveryAsync(FakeBluezService.Adapter, CancellationToken.None);
        await bus.StopDiscoveryAsync(FakeBluezService.Adapter, CancellationToken.None);
        await bus.ConnectDeviceAsync(FakeBluezService.Device, CancellationToken.None);
        await bus.DisconnectDeviceAsync(FakeBluezService.Device, CancellationToken.None);
        await bus.StartNotifyAsync(FakeBluezService.Characteristic, CancellationToken.None);
        await bus.StopNotifyAsync(FakeBluezService.Characteristic, CancellationToken.None);

        Assert.Equal(
            new[] { "SetDiscoveryFilter", "StartDiscovery", "StopDiscovery", "Connect", "Disconnect", "StartNotify", "StopNotify" },
            harness.Service.Calls.ToArray());
        Assert.Equal("le", harness.Service.FilterTransport);
        Assert.Equal(true, harness.Service.FilterDuplicateData);
        Assert.Equal(new[] { "0000181d-0000-1000-8000-00805f9b34fb" }, harness.Service.FilterUuids);
    }

    [DbusFact]
    public async Task ReadValue_ReturnsBytes()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await bus.ReadValueAsync(FakeBluezService.Characteristic, CancellationToken.None));
    }

    [DbusFact]
    public async Task DBusErrorReply_BecomesBluezDBusException_WithErrorName()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        var ex = await Assert.ThrowsAsync<BluezDBusException>(
            () => bus.ConnectDeviceAsync(FakeBluezService.FailingDevice, CancellationToken.None));
        Assert.Equal("org.bluez.Error.NotReady", ex.ErrorName);

        var unknown = await Assert.ThrowsAsync<BluezDBusException>(
            () => bus.StartDiscoveryAsync("/org/bluez/hci9", CancellationToken.None));
        Assert.Equal("org.freedesktop.DBus.Error.UnknownMethod", unknown.ErrorName);
    }

    [DbusFact]
    public async Task PropertiesChangedWatcher_DeliversValueAndDevicePropertiesUntilDisposed()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        var values = new TaskCompletionSource<BluezPropertiesChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var valueWatch = await bus.WatchPropertiesChangedAsync(
            FakeBluezService.Characteristic,
            change => values.TrySetResult(change),
            CancellationToken.None);

        var all = new List<BluezPropertiesChanged>();
        var gotDevice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var namespaceWatch = await bus.WatchPropertiesChangedAsync(
            null,
            change =>
            {
                lock (all)
                    all.Add(change);
                if (change.Interface == BluezNames.Device1)
                    gotDevice.TrySetResult();
            },
            CancellationToken.None);

        FakeBluezService.EmitCharacteristicValue(harness.ServiceConnection, [9, 8, 7]);
        FakeBluezService.EmitDeviceConnected(harness.ServiceConnection, FakeBluezService.Device, connected: true);

        var change = await values.Task.WaitAsync(Wait);
        Assert.Equal(FakeBluezService.Characteristic, change.Path);
        Assert.Equal(BluezNames.GattCharacteristic1, change.Interface);
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.IsType<byte[]>(change.Changed["Value"]));
        Assert.Empty(change.Invalidated);

        await gotDevice.Task.WaitAsync(Wait);
        BluezPropertiesChanged device;
        lock (all)
            device = all.First(c => c.Interface == BluezNames.Device1);
        Assert.Equal(FakeBluezService.Device, device.Path);
        Assert.Equal(true, device.Changed["Connected"]);
        Assert.Equal(new[] { "RSSI" }, device.Invalidated);

        namespaceWatch.Dispose();
        namespaceWatch.Dispose();
        int before;
        lock (all)
            before = all.Count;
        FakeBluezService.EmitDeviceConnected(harness.ServiceConnection, FakeBluezService.Device, connected: false);
        await bus.ReadValueAsync(FakeBluezService.Characteristic, CancellationToken.None); // round trip orders the signal
        await Task.Delay(100);
        lock (all)
            Assert.Equal(before, all.Count);
    }

    [DbusFact]
    public async Task ObjectManagerWatchers_DeliverAddedAndRemoved()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        var added = new TaskCompletionSource<BluezObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<(string, IReadOnlyList<string>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var addedWatch = await bus.WatchInterfacesAddedAsync(o => added.TrySetResult(o), CancellationToken.None);
        using var removedWatch = await bus.WatchInterfacesRemovedAsync(
            (path, interfaces) => removed.TrySetResult((path, interfaces)),
            CancellationToken.None);

        FakeBluezService.EmitInterfacesAdded(harness.ServiceConnection);
        FakeBluezService.EmitInterfacesRemoved(harness.ServiceConnection);

        var obj = await added.Task.WaitAsync(Wait);
        Assert.Equal(FakeBluezService.FailingDevice, obj.Path);
        Assert.Equal("FF:FF:FF:FF:FF:FF", obj.Interfaces[BluezNames.Device1]["Address"]);

        var (removedPath, removedInterfaces) = await removed.Task.WaitAsync(Wait);
        Assert.Equal(FakeBluezService.FailingDevice, removedPath);
        Assert.Equal(new[] { "org.bluez.Device1" }, removedInterfaces);
    }

    [DbusFact]
    public async Task ConnectionClosed_IsRaised_WhenDaemonDies_AndCallsFailAsDisconnected()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.ConnectionClosed += ex => closed.TrySetResult(ex);

        harness.KillDaemon();

        Assert.NotNull(await closed.Task.WaitAsync(Wait));
        var failure = await Assert.ThrowsAsync<BluezDBusException>(
            () => bus.StartDiscoveryAsync(FakeBluezService.Adapter, CancellationToken.None));
        Assert.Equal("org.freedesktop.DBus.Error.Disconnected", failure.ErrorName);
    }

    [DbusFact]
    public async Task Dispose_DoesNotRaiseConnectionClosed_AndLaterCallsFail()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;

        var raised = 0;
        bus.ConnectionClosed += _ => Interlocked.Increment(ref raised);
        await bus.DisposeAsync();
        await bus.DisposeAsync();
        await Task.Delay(200);

        Assert.Equal(0, raised);
        await Assert.ThrowsAsync<BluezDBusException>(
            () => bus.StartDiscoveryAsync(FakeBluezService.Adapter, CancellationToken.None));
    }

    [DbusFact]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        var (harness, bus) = await StartAsync();
        await using var _ = harness;
        await using var __ = bus;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.GetManagedObjectsAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.WatchInterfacesAddedAsync(_ => { }, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TmdsBluezBus.ConnectAsync(harness.Address, cts.Token));
    }

    [Fact]
    public async Task Connect_ToMissingBus_ThrowsNoServer()
    {
        var ex = await Assert.ThrowsAsync<BluezDBusException>(
            () => TmdsBluezBus.ConnectAsync("unix:path=/nonexistent/scalebridge-test-bus", CancellationToken.None));
        Assert.Equal("org.freedesktop.DBus.Error.NoServer", ex.ErrorName);
    }

    [DbusFact]
    public async Task Adapter_EndToEnd_AvailabilityScanConnectReadAndNotify()
    {
        var harness = await BluezDbusHarness.StartAsync();
        await using var _ = harness;
        var adapter = new BluezBleAdapter(
            async ct => await TmdsBluezBus.ConnectAsync(harness.Address, ct),
            platformSupported: true);

        Assert.Equal(BleAvailability.Available, await adapter.GetAvailabilityAsync());

        using var scanCts = new CancellationTokenSource(Wait);
        AdvertisedDevice? seen = null;
        await foreach (var device in adapter.ScanAsync(new ScanOptions(), scanCts.Token))
        {
            seen = device;
            break;
        }

        Assert.NotNull(seen);
        Assert.Equal(FakeBluezService.DeviceMac, seen.Id);
        Assert.Equal(-61, seen.Rssi);
        Assert.Equal(new byte[] { 0x10, 0x05 }, seen.ManufacturerData[0x004C]);
        Assert.Contains(Guid.Parse(FakeBluezService.ServiceUuid), seen.ServiceUuids);

        await using var connection = await adapter.ConnectAsync(FakeBluezService.DeviceMac, TimeSpan.FromSeconds(5));
        var services = await connection.DiscoverServicesAsync();
        var service = Assert.Single(services);
        Assert.Equal(Guid.Parse(FakeBluezService.ServiceUuid), service.Uuid);

        var serviceGuid = Guid.Parse(FakeBluezService.ServiceUuid);
        var charGuid = Guid.Parse(FakeBluezService.CharacteristicUuid);
        Assert.Equal(new byte[] { 1, 2, 3 }, await connection.ReadAsync(serviceGuid, charGuid));

        using var subCts = new CancellationTokenSource(Wait);
        var enumerator = connection.SubscribeAsync(serviceGuid, charGuid, subCts.Token).GetAsyncEnumerator(subCts.Token);
        var next = enumerator.MoveNextAsync().AsTask();
        while (!harness.Service.Calls.Contains("StartNotify"))
            await Task.Delay(20, subCts.Token);
        FakeBluezService.EmitCharacteristicValue(harness.ServiceConnection, [5, 6]);

        Assert.True(await next);
        Assert.Equal(new byte[] { 5, 6 }, enumerator.Current.Value);
        await enumerator.DisposeAsync();
    }
}
