using ScaleBridge.Ble.Fake;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Fake.Tests;

public class ScenarioLibraryTests
{
    private static readonly string[] Expected =
    {
        "body-composition", "imperial", "invalid-0xffff", "mid-connection-disconnect", "multi-device",
        "normal-si", "reconnect", "timestamp-user-bmi-height", "truncated-packet",
    };

    [Fact]
    public void Library_contains_exactly_the_required_scenarios()
    {
        Assert.Equal(Expected, FakeScenarioLibrary.All.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Scenarios_are_labelled_and_self_consistent()
    {
        foreach (var s in FakeScenarioLibrary.All)
        {
            Assert.Equal("scalebridge-scenario/1", s.Schema);
            Assert.False(string.IsNullOrWhiteSpace(s.Title.Zh));
            Assert.False(string.IsNullOrWhiteSpace(s.Title.En));
            Assert.False(string.IsNullOrWhiteSpace(s.Description.Zh));
            Assert.NotEmpty(s.Devices);
            Assert.Equal(s.Devices.Count, s.Devices.Select(d => d.Id).Distinct().Count());
            foreach (var d in s.Devices)
            {
                Assert.NotEmpty(d.Connections);
                Assert.NotEmpty(d.Services);
            }
        }
    }

    [Fact]
    public void Every_scripted_packet_parses_as_its_expect_annotation_says()
    {
        var checkedPackets = 0;
        foreach (var s in FakeScenarioLibrary.All)
        foreach (var d in s.Devices)
        foreach (var c in d.Connections)
        foreach (var e in c.Events.Where(e => e.Type == "indicate"))
        {
            var bytes = Convert.FromHexString(e.Hex!);
            var ch = GattUuids.TryParse(e.Characteristic, out var g) ? g : throw new Exception("bad uuid");
            Assert.NotNull(e.Expect);
            if (ch == GattUuids.WeightMeasurement)
            {
                var r = WeightScaleParser.ParseMeasurement(bytes);
                AssertOutcome(e.Expect!, r.IsSuccess, r.ErrorCode, r.Value?.MeasurementUnsuccessful ?? false, $"{s.Id}/{e.Hex}");
            }
            else
            {
                Assert.Equal(GattUuids.BodyCompositionMeasurement, ch);
                var r = BodyCompositionParser.ParseMeasurement(bytes);
                AssertOutcome(e.Expect!, r.IsSuccess, r.ErrorCode, r.Value?.MeasurementUnsuccessful ?? false, $"{s.Id}/{e.Hex}");
            }
            checkedPackets++;
        }
        Assert.True(checkedPackets >= 20, "scenarios should script a meaningful number of packets");
    }

    [Fact]
    public void Feature_values_in_scenarios_parse()
    {
        foreach (var d in FakeScenarioLibrary.All.SelectMany(s => s.Devices))
            foreach (var (k, v) in d.Features)
            {
                GattUuids.TryParse(k, out var g);
                var bytes = Convert.FromHexString(v);
                if (g == GattUuids.WeightScaleFeature) Assert.True(WeightScaleParser.ParseFeature(bytes).IsSuccess);
                else if (g == GattUuids.BodyCompositionFeature) Assert.True(BodyCompositionParser.ParseFeature(bytes).IsSuccess);
                else Assert.Fail("unexpected feature characteristic " + k);
            }
    }

    [Fact]
    public void Get_falls_back_to_default_for_unknown_id()
    {
        Assert.Equal(FakeScenarioLibrary.DefaultId, FakeScenarioLibrary.Get("nope").Id);
        Assert.Null(FakeScenarioLibrary.Find("nope"));
        Assert.Equal("imperial", FakeScenarioLibrary.Find("IMPERIAL")?.Id);
    }

    private static void AssertOutcome(string expect, bool success, ParseErrorCode code, bool unsuccessful, string what)
    {
        switch (expect)
        {
            case "ok": Assert.True(success && !unsuccessful, what); break;
            case "unsuccessful": Assert.True(success && unsuccessful, what); break;
            default:
                Assert.StartsWith("error:", expect);
                Assert.False(success, what);
                Assert.Equal(expect["error:".Length..], code.ToString());
                break;
        }
    }
}
