using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Core.Session;

namespace ScaleBridge.Fake.Tests;

/// <summary>
/// L08：设备同时订阅 Weight Scale 与 Body Composition 两个特征。一路因断开而结束时，另一路已到达、还没处理的通知不能被丢掉，
/// 并且「测量」事件要先于「断开」状态事件。
/// </summary>
public class SessionDrainTests
{
    /// <summary>
    /// 脚本化的连接：Weight 流立刻以「设备断开」结束；Body Composition 流稍晚（模拟系统还在把缓冲的通知交付上来）
    /// 交付两条通知，然后同样以断开结束。修复前，Weight 流一结束会话就取消另一路，这两条会丢。
    /// </summary>
    private sealed class TwoStreamAdapter : IBleAdapter
    {
        public int BcmLateMs = 60;
        public bool BcmNeverEnds;
        public string Id => "test";
        public string DisplayName => "two-stream";
        public bool IsDemo => true;
        public Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default) => Task.FromResult(BleAvailability.Available);
        public IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Task.FromResult<IBleConnection>(new Conn(this, deviceId));

        private sealed class Conn : IBleConnection
        {
            private readonly TwoStreamAdapter _o;
            public Conn(TwoStreamAdapter o, string id) { _o = o; DeviceId = id; }
            public string DeviceId { get; }
            public ConnectionState State => ConnectionState.Connected;
            public event EventHandler<ConnectionStateChange>? StateChanged { add { } remove { } }

            public Task<IReadOnlyList<GattServiceInfo>> DiscoverServicesAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<GattServiceInfo>>(new[]
                {
                    new GattServiceInfo(GattUuids.WeightScaleService, new[] { new GattCharacteristicInfo(GattUuids.WeightMeasurement, GattCharacteristicProperties.Indicate) }),
                    new GattServiceInfo(GattUuids.BodyCompositionService, new[] { new GattCharacteristicInfo(GattUuids.BodyCompositionMeasurement, GattCharacteristicProperties.Indicate) }),
                });

            public Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct = default) => throw new NotSupportedException();

            public async IAsyncEnumerable<GattNotification> SubscribeAsync(Guid service, Guid characteristic,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                if (characteristic == GattUuids.WeightMeasurement)
                {
                    await Task.Yield();
                    throw new DeviceDisconnectedException("scripted drop");
                }
                await Task.Delay(_o.BcmLateMs, ct).ConfigureAwait(false);
                yield return new GattNotification(service, characteristic, Convert.FromHexString("7E0FB900EA070A1A072D0C01201C9C01401FC02B08200314B036D606"), DateTimeOffset.UtcNow);
                yield return new GattNotification(service, characteristic, Convert.FromHexString("060CC900EA070A1A073203012837D606"), DateTimeOffset.UtcNow);
                if (_o.BcmNeverEnds) await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                throw new DeviceDisconnectedException("scripted drop");
            }

            public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task<List<string>> RunAsync(TwoStreamAdapter adapter, TimeSpan? timeout = null)
    {
        var log = new List<string>();
        var session = new ScaleSession(adapter, "AA:BB", "two-stream");
        session.StateChanged += (_, c) => { lock (log) log.Add("state:" + c.State); };
        session.MeasurementReceived += (_, m) => { lock (log) log.Add("measurement"); };
        session.IssueRaised += (_, i) => { lock (log) log.Add("issue:" + i.Kind); };
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        await session.RunAsync(cts.Token);
        return log;
    }

    [Fact]
    public async Task NotificationsStillInFlightOnTheOtherCharacteristic_AreDeliveredBeforeTheDrop()
    {
        var log = await RunAsync(new TwoStreamAdapter());
        Assert.Equal(new[] { "state:Connecting", "state:Connected", "measurement", "measurement", "state:Dropped" }, log);
    }

    [Fact]
    public async Task SiblingStreamThatNeverEnds_DoesNotHangTheDrop_AndStillDeliversWhatItHad()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var log = await RunAsync(new TwoStreamAdapter { BcmLateMs = 0, BcmNeverEnds = true });
        Assert.Equal("state:Dropped", log[^1]);
        Assert.Equal(2, log.Count(x => x == "measurement"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "the drain grace period must be bounded");
    }

    [Fact]
    public async Task UserCancel_StillStopsImmediately_WithoutWaitingForTheGracePeriod()
    {
        var adapter = new TwoStreamAdapter { BcmLateMs = 5000 };
        var session = new ScaleSession(adapter, "AA:BB", "x");
        var states = new List<ConnectionState>();
        session.StateChanged += (_, c) => { lock (states) states.Add(c.State); };
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);
        // Weight 流立刻失败 → 会话进入排空等待；这时用户取消应立刻结束而不是等满宽限期
        await Task.Delay(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1000), $"cancel took {sw.Elapsed}");
        // Weight 流确实已经断开了，所以最终状态仍如实报告为 Dropped；关键是没有被宽限期拖住
        Assert.Contains(states[^1], new[] { ConnectionState.Dropped, ConnectionState.Disconnected });
    }

    [Fact]
    public async Task ScenarioWithBothCharacteristics_StillEndsInDropped_WithAllMeasurementsFirst()
    {
        // 回归：整个 Fake 场景库里同时有两路特征的设备，行为保持不变
        var sc = ScaleBridge.Ble.Fake.FakeScenarioLibrary.Get("mid-connection-disconnect");
        var adapter = new ScaleBridge.Ble.Fake.FakeBleAdapter(sc, delayScale: 0);
        var log = new List<string>();
        var session = new ScaleSession(adapter, sc.Devices[0].Id, sc.Devices[0].Name);
        session.StateChanged += (_, c) => log.Add("state:" + c.State);
        session.MeasurementReceived += (_, _) => log.Add("measurement");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.RunAsync(cts.Token);
        Assert.Equal("state:Dropped", log[^1]);
        Assert.Contains("measurement", log);
        Assert.True(log.LastIndexOf("measurement") < log.Count - 1);
    }
}
