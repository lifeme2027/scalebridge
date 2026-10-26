using ScaleBridge.Ble.Fake;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Core.Session;

namespace ScaleBridge.Fake.Tests;

/// <summary>用 Fake 适配器（无延时）跑完整会话：不需要任何蓝牙硬件。</summary>
public class SessionFlowTests
{
    private sealed class Run
    {
        public List<Measurement> Measurements { get; } = new();
        public List<SessionIssue> Issues { get; } = new();
        public List<ConnectionStateChange> States { get; } = new();
        public ScaleSession Session = null!;
    }

    private static async Task<Run> RunScenarioAsync(string scenarioId, int deviceIndex = 0, int stopAfterMeasurements = 0, int stopAfterIssues = 0, int attempt = 1, FakeScenario? scenario = null)
    {
        var sc = scenario ?? FakeScenarioLibrary.Get(scenarioId);
        var adapter = new FakeBleAdapter(sc, delayScale: 0);
        var dev = sc.Devices[deviceIndex];
        var run = new Run();
        for (var i = 0; i < attempt; i++)
        {
            run = i == 0 ? run : new Run();
            await DriveAsync(adapter, dev, run, stopAfterMeasurements, stopAfterIssues);
        }
        return run;
    }

    private static async Task DriveAsync(FakeBleAdapter adapter, FakeDevice dev, Run run, int stopMeasurements, int stopIssues)
    {
        var session = new ScaleSession(adapter, dev.Id, dev.Name);
        run.Session = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        session.StateChanged += (_, c) => { lock (run) run.States.Add(c); };
        session.MeasurementReceived += (_, m) =>
        {
            lock (run) run.Measurements.Add(m);
            if (stopMeasurements > 0 && run.Measurements.Count >= stopMeasurements && (stopIssues == 0 || run.Issues.Count >= stopIssues)) cts.Cancel();
        };
        session.IssueRaised += (_, i) =>
        {
            lock (run) run.Issues.Add(i);
            if (stopIssues > 0 && run.Issues.Count >= stopIssues && (stopMeasurements == 0 || run.Measurements.Count >= stopMeasurements)) cts.Cancel();
        };
        await session.RunAsync(cts.Token);
    }

    [Fact]
    public async Task Normal_si_delivers_three_exact_kg_weights_marked_demo_and_experimental()
    {
        var run = await RunScenarioAsync("normal-si", stopAfterMeasurements: 3);
        Assert.Equal(new[] { 70.000m, 70.350m, 69.900m }, run.Measurements.Select(m => m.WeightKg!.Value));
        Assert.All(run.Measurements, m =>
        {
            Assert.True(m.IsDemo);
            Assert.True(m.IsExperimental);
            Assert.Equal(UnitSystem.Si, m.OriginalUnit);
            Assert.Equal(MeasurementSource.WeightScale, m.Source);
            Assert.Equal("DE:MO:00:00:00:01", m.DeviceId);
            Assert.Equal("演示体重秤 A", m.DeviceName);
        });
        Assert.Equal(14000, run.Measurements[0].RawWeight);
        Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnected }, run.States.Select(s => s.State));
    }

    [Fact]
    public async Task Imperial_keeps_original_lb_and_converts_exactly()
    {
        var run = await RunScenarioAsync("imperial", stopAfterMeasurements: 3);
        var first = run.Measurements[0];
        Assert.Equal(UnitSystem.Imperial, first.OriginalUnit);
        Assert.Equal(154.32m, first.OriginalWeight);
        Assert.Equal(15432, first.RawWeight);
        Assert.Equal(154.32m * 0.45359237m, first.WeightKg);
    }

    [Fact]
    public async Task Timestamp_user_bmi_height_are_all_carried()
    {
        var run = await RunScenarioAsync("timestamp-user-bmi-height", stopAfterMeasurements: 2);
        var m = run.Measurements[0];
        Assert.Equal(new DateTime(2026, 10, 26, 7, 45, 12), m.DeviceTimestamp);
        Assert.Equal(1, m.UserId);
        Assert.Equal(22.0m, m.Bmi);
        Assert.Equal(1.750m, m.HeightMeters);
        Assert.Equal(67.3m, m.WeightKg);
        Assert.Equal(2, run.Measurements[1].UserId);
        Assert.Equal(81.0m, run.Measurements[1].WeightKg);
    }

    [Fact]
    public async Task Body_composition_fills_the_composition_fields()
    {
        var run = await RunScenarioAsync("body-composition", stopAfterMeasurements: 2);
        var m = run.Measurements[0];
        Assert.Equal(MeasurementSource.BodyComposition, m.Source);
        Assert.Equal(18.5m, m.BodyFatPercent);
        Assert.Equal(41.2m, m.MusclePercent);
        Assert.Equal(40.0m, m.MuscleMassKg);
        Assert.Equal(56.0m, m.FatFreeMassKg);
        Assert.Equal(41.0m, m.BodyWaterMassKg);
        Assert.Equal(7200, m.BasalMetabolismKj);
        Assert.Equal(512.3m, m.ImpedanceOhm);
        Assert.Equal(70.0m, m.WeightKg);
        Assert.Equal(1.75m, m.HeightMeters);
        Assert.NotNull(run.Session.BodyCompositionFeature);
        Assert.Null(run.Session.WeightFeature);
    }

    [Fact]
    public async Task Invalid_0xffff_is_reported_and_not_stored()
    {
        var run = await RunScenarioAsync("invalid-0xffff", stopAfterMeasurements: 1, stopAfterIssues: 1);
        var issue = Assert.Single(run.Issues);
        Assert.Equal(SessionIssueKind.MeasurementUnsuccessful, issue.Kind);
        var m = Assert.Single(run.Measurements);
        Assert.Equal(71.05m, m.WeightKg);
    }

    [Fact]
    public async Task Truncated_packets_are_reported_without_crashing_and_the_session_continues()
    {
        var run = await RunScenarioAsync("truncated-packet", stopAfterMeasurements: 1, stopAfterIssues: 2);
        Assert.Equal(new[] { ParseErrorCode.TooShort, ParseErrorCode.Truncated }, run.Issues.Select(i => i.ErrorCode));
        Assert.All(run.Issues, i => Assert.Equal(SessionIssueKind.ParseError, i.Kind));
        Assert.Equal("0E", run.Issues[0].Hex);
        Assert.Equal(71.75m, Assert.Single(run.Measurements).WeightKg);
    }

    [Fact]
    public async Task Mid_connection_disconnect_ends_the_run_as_dropped_with_reason()
    {
        var run = await RunScenarioAsync("mid-connection-disconnect");
        Assert.Single(run.Measurements);
        var last = run.States[^1];
        Assert.Equal(ConnectionState.Dropped, last.State);
        Assert.Equal(BleErrorKind.DeviceDisconnected, last.ErrorKind);
        Assert.Contains("powered off", last.Reason);
        Assert.Equal(ConnectionState.Dropped, run.Session.State);
    }

    [Fact]
    public async Task Reconnect_second_attempt_uses_the_second_script()
    {
        var sc = FakeScenarioLibrary.Get("reconnect");
        var adapter = new FakeBleAdapter(sc, 0);
        var dev = sc.Devices[0];
        var first = new Run();
        await DriveAsync(adapter, dev, first, 0, 0);
        Assert.Equal(ConnectionState.Dropped, first.States[^1].State);
        Assert.Single(first.Measurements);

        var second = new Run();
        await DriveAsync(adapter, dev, second, 2, 0);
        Assert.Equal(new[] { 70.4m, 70.3m }, second.Measurements.Select(m => m.WeightKg!.Value));
        Assert.Contains(second.States, s => s.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task Cancelling_ends_in_disconnected_not_dropped()
    {
        var run = await RunScenarioAsync("normal-si", stopAfterMeasurements: 1);
        Assert.Equal(ConnectionState.Disconnected, run.States[^1].State);
        Assert.Null(run.States[^1].Reason);
    }

    [Theory]
    [InlineData("timeout", BleErrorKind.Timeout)]
    [InlineData("unavailable", BleErrorKind.BluetoothUnavailable)]
    [InlineData("permission", BleErrorKind.PermissionDenied)]
    public async Task Connect_failures_map_to_error_kinds(string failWith, BleErrorKind kind)
    {
        var json = """
        {"schema":"scalebridge-scenario/1","id":"x","title":{"zh":"x","en":"x"},"description":{"zh":"x","en":"x"},
         "devices":[{"id":"AA","name":"n","services":["181D"],"connections":[{"connectDelayMs":0,"failWith":"__F__","events":[]}]}]}
        """.Replace("__F__", failWith);
        var run = await RunScenarioAsync("x", scenario: FakeScenarioLibrary.Parse(json));
        Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Dropped }, run.States.Select(s => s.State));
        Assert.Equal(kind, run.States[^1].ErrorKind);
        Assert.False(string.IsNullOrEmpty(run.States[^1].Reason));
    }

    [Fact]
    public async Task Device_without_supported_service_is_rejected_with_a_clear_error()
    {
        var json = """
        {"schema":"scalebridge-scenario/1","id":"x","title":{"zh":"x","en":"x"},"description":{"zh":"x","en":"x"},
         "devices":[{"id":"AA","name":"n","services":["180F"],"connections":[{"connectDelayMs":0,"events":[]}]}]}
        """;
        var run = await RunScenarioAsync("x", scenario: FakeScenarioLibrary.Parse(json));
        Assert.Equal(BleErrorKind.NoSupportedService, run.States[^1].ErrorKind);
    }

    [Fact]
    public async Task Weight_scale_feature_is_read_and_decoded()
    {
        var run = await RunScenarioAsync("normal-si", stopAfterMeasurements: 1);
        var f = run.Session.WeightFeature;
        Assert.NotNull(f);
        Assert.Equal(0.005m, f!.WeightResolutionKg);
    }

    [Fact]
    public async Task Scan_returns_all_devices_of_the_multi_device_scenario_with_demo_flag()
    {
        var adapter = new FakeBleAdapter(FakeScenarioLibrary.Get("multi-device"), 0);
        var seen = new Dictionary<string, AdvertisedDevice>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var d in adapter.ScanAsync(new ScanOptions(), cts.Token))
        {
            seen[d.Id] = d;
            if (seen.Count == 3) break;
        }
        Assert.Equal(3, seen.Count);
        Assert.All(seen.Values, d => Assert.True(d.IsDemo));
        Assert.Equal(new[] { ScaleProtocols.WeightScale, ScaleProtocols.WeightScale, ScaleProtocols.BodyComposition },
            seen.Values.OrderBy(d => d.Id).Select(d => DeviceClassifier.Classify(d)).ToArray());
        Assert.True(seen.Values.Select(d => d.Rssi).Distinct().Count() > 1);
    }

    [Fact]
    public async Task Scan_service_filter_hides_non_matching_devices()
    {
        var adapter = new FakeBleAdapter(FakeScenarioLibrary.Get("multi-device"), 0);
        var opts = new ScanOptions { ServiceFilter = new[] { GattUuids.BodyCompositionService } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ids = new HashSet<string>();
        await foreach (var d in adapter.ScanAsync(opts, cts.Token)) ids.Add(d.Id);
        Assert.Equal(new[] { "DE:MO:00:00:00:0A" }, ids.ToArray());
    }

    [Fact]
    public async Task Scan_with_real_delays_reveals_devices_over_time_in_order()
    {
        var adapter = new FakeBleAdapter(FakeScenarioLibrary.Get("multi-device"), delayScale: 0.05);
        var order = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var d in adapter.ScanAsync(new ScanOptions(), cts.Token))
        {
            if (!order.Contains(d.Id)) order.Add(d.Id);
            if (order.Count == 3) break;
        }
        Assert.Equal(new[] { "DE:MO:00:00:00:08", "DE:MO:00:00:00:09", "DE:MO:00:00:00:0A" }, order);
    }

    [Fact]
    public async Task Connecting_to_an_unknown_device_throws_ble_exception()
    {
        var adapter = new FakeBleAdapter(FakeScenarioLibrary.Get("normal-si"), 0);
        await Assert.ThrowsAsync<BleException>(() => adapter.ConnectAsync("nope"));
    }

    [Fact]
    public async Task Adapter_reports_demo_and_available()
    {
        var adapter = new FakeBleAdapter(FakeScenarioLibrary.Get("normal-si"), 0);
        Assert.True(adapter.IsDemo);
        Assert.Equal("fake", adapter.Id);
        Assert.Equal(BleAvailability.Available, await adapter.GetAvailabilityAsync());
    }
}
