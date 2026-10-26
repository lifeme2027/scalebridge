using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests;

public class MeasurementFactoryTests
{
    private static readonly DeviceContext Dev = new("AA:BB", "Scale", IsDemo: false);
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Weight_scale_maps_all_fields_and_is_always_experimental()
    {
        // flags 0x0F: imperial + ts + user + bmi/height; 154.32 lb = 15432 raw
        var bytes = new byte[] { 0x0F, 0x48, 0x3C, 0xEA, 0x07, 10, 26, 8, 30, 59, 2, 0xF5, 0x00, 0xC6, 0x06 };
        var d = WeightScaleParser.ParseMeasurement(bytes).Value!;
        var m = MeasurementFactory.FromWeightScale(d, Dev, Now)!;
        Assert.Equal(15432, m.RawWeight);
        Assert.Equal(154.32m, m.OriginalWeight);
        Assert.Equal(UnitSystem.Imperial, m.OriginalUnit);
        Assert.Equal(154.32m * 0.45359237m, m.WeightKg);
        Assert.Equal(24.5m, m.Bmi);
        Assert.Equal(173.4m * 0.0254m, m.HeightMeters);
        Assert.Equal(2, m.UserId);
        Assert.Equal(new DateTime(2026, 10, 26, 8, 30, 59), m.DeviceTimestamp);
        Assert.Equal(Now, m.ReceivedAt);
        Assert.Equal(MeasurementSource.WeightScale, m.Source);
        Assert.True(m.IsExperimental);
        Assert.False(m.IsDemo);
        Assert.Equal("AA:BB", m.DeviceId);
        Assert.Equal("Scale", m.DeviceName);
    }

    [Fact]
    public void Unsuccessful_weight_measurement_produces_no_record()
    {
        var d = WeightScaleParser.ParseMeasurement(new byte[] { 0, 0xFF, 0xFF }).Value!;
        Assert.Null(MeasurementFactory.FromWeightScale(d, Dev, Now));
    }

    [Fact]
    public void Unsuccessful_body_composition_produces_no_record()
    {
        var d = BodyCompositionParser.ParseMeasurement(new byte[] { 0, 0, 0xFF, 0xFF }).Value!;
        Assert.Null(MeasurementFactory.FromBodyComposition(d, Dev, Now));
    }

    [Fact]
    public void Body_composition_without_weight_has_no_original_unit()
    {
        var d = BodyCompositionParser.ParseMeasurement(new byte[] { 0, 0, 0xD7, 0x00 }).Value!;
        var m = MeasurementFactory.FromBodyComposition(d, Dev, Now)!;
        Assert.Equal(21.5m, m.BodyFatPercent);
        Assert.Null(m.WeightKg);
        Assert.Null(m.OriginalUnit);
        Assert.Null(m.RawWeight);
        Assert.Equal(MeasurementSource.BodyComposition, m.Source);
        Assert.True(m.IsExperimental);
    }

    [Fact]
    public void Body_composition_maps_masses_to_kilograms()
    {
        // flags 0x0420: muscle mass (bit5) + weight (bit10), SI
        var d = BodyCompositionParser.ParseMeasurement(new byte[] { 0x20, 0x04, 0xD7, 0x00, 0x10, 0x27, 0x28, 0x23 }).Value!;
        var m = MeasurementFactory.FromBodyComposition(d, Dev, Now)!;
        Assert.Equal(0x2710 * 0.005m, m.MuscleMassKg);
        Assert.Equal(0x2328 * 0.005m, m.WeightKg);
    }

    [Fact]
    public void Demo_flag_follows_the_device_context()
    {
        var d = WeightScaleParser.ParseMeasurement(new byte[] { 0, 0x10, 0x27 }).Value!;
        Assert.True(MeasurementFactory.FromWeightScale(d, Dev with { IsDemo = true }, Now)!.IsDemo);
    }

    [Fact]
    public void Display_time_prefers_the_device_timestamp()
    {
        var withTs = new Measurement { DeviceTimestamp = new DateTime(2020, 1, 2, 3, 4, 5), ReceivedAt = Now };
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), withTs.DisplayTime);
        var without = new Measurement { ReceivedAt = Now };
        Assert.Equal(Now.LocalDateTime, without.DisplayTime);
    }
}
