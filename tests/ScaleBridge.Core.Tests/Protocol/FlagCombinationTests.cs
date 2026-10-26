using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests.Protocol;

/// <summary>用独立的「按 Flags 拼包」构造器穷举 Flags 组合，核对解析器按位取字段、字段顺序与字节偏移。</summary>
public class FlagCombinationTests
{
    private static readonly byte[] Ts = { 0xEA, 0x07, 10, 26, 8, 30, 59 };

    // ---- Weight Scale：Flags(1) Weight(2) [TS 7] [UserId 1] [BMI 2 Height 2] ----
    private static byte[] BuildWss(int flags, ushort weight = 14001)
    {
        var b = new List<byte> { (byte)flags, (byte)(weight & 0xFF), (byte)(weight >> 8) };
        if ((flags & 2) != 0) b.AddRange(Ts);
        if ((flags & 4) != 0) b.Add(7);
        if ((flags & 8) != 0) { b.AddRange(new byte[] { 0xF5, 0x00 }); b.AddRange(new byte[] { 0xC6, 0x06 }); } // BMI 24.5, 1734 → 1.734 m / 173.4 in
        return b.ToArray();
    }

    public static IEnumerable<object[]> WssLowFlags() => Enumerable.Range(0, 16).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(WssLowFlags))]
    public void Wss_every_combination_of_the_four_defined_flags(int flags)
    {
        var r = WeightScaleParser.ParseMeasurement(BuildWss(flags));
        Assert.True(r.IsSuccess, r.ErrorMessage);
        Assert.Empty(r.Warnings);
        var d = r.Value!;
        Assert.Equal((flags & 1) != 0 ? UnitSystem.Imperial : UnitSystem.Si, d.Unit);
        Assert.Equal(14001, d.Weight!.Raw);
        Assert.Equal((flags & 2) != 0, d.TimeStamp.HasValue);
        if (d.TimeStamp is { } t) Assert.Equal(new DateTime(2026, 10, 26, 8, 30, 59), t);
        Assert.Equal((flags & 4) != 0 ? 7 : (int?)null, d.UserId);
        Assert.Equal((flags & 8) != 0 ? 24.5m : (decimal?)null, d.Bmi);
        Assert.Equal((flags & 8) != 0, d.Height is not null);
        if (d.Height is { } h) Assert.Equal(1734, h.Raw);
    }

    [Theory]
    [MemberData(nameof(WssLowFlags))]
    public void Wss_every_proper_prefix_is_a_clean_error(int flags)
    {
        var full = BuildWss(flags);
        for (int len = 0; len < full.Length; len++)
        {
            var r = WeightScaleParser.ParseMeasurement(full.AsSpan(0, len));
            Assert.False(r.IsSuccess);
            Assert.Null(r.Value);
            Assert.True(r.ErrorCode is ParseErrorCode.TooShort or ParseErrorCode.Truncated);
            Assert.False(string.IsNullOrEmpty(r.ErrorMessage));
        }
    }

    [Fact]
    public void Wss_reserved_flag_bits_only_warn()
    {
        for (int hi = 1; hi < 16; hi++)
        {
            var r = WeightScaleParser.ParseMeasurement(BuildWss(hi << 4));
            Assert.True(r.IsSuccess);
            Assert.Contains(ParseWarningCode.ReservedBitsSet, r.Warnings);
        }
    }

    [Fact]
    public void Wss_unsuccessful_with_every_flag_combination_never_yields_a_weight()
    {
        for (int flags = 0; flags < 16; flags++)
        {
            var r = WeightScaleParser.ParseMeasurement(BuildWss(flags & ~8, weight: 0xFFFF));
            Assert.True(r.IsSuccess);
            Assert.True(r.Value!.MeasurementUnsuccessful);
            Assert.Null(r.Value.Weight);
        }
    }

    // ---- Body Composition ----
    private static readonly ushort[] BcsValues = { 0, 0, 0, 1000, 0, 420, 5500, 6000, 3500, 4100, 5000, 7000, 1750 };
    // 下标=标志位序号；值：3 基础代谢 4 肌肉率 5 肌肉质量 6 去脂体重 7 软瘦体重 8 水分 9 阻抗 10 体重 11 身高

    private static byte[] BuildBcs(int flags, ushort bodyFat = 215)
    {
        var b = new List<byte> { (byte)(flags & 0xFF), (byte)(flags >> 8), (byte)(bodyFat & 0xFF), (byte)(bodyFat >> 8) };
        if ((flags & 2) != 0) b.AddRange(Ts);
        if ((flags & 4) != 0) b.Add(3);
        for (int bit = 3; bit <= 11; bit++)
            if ((flags & (1 << bit)) != 0) { var v = BcsValues[bit + 1 > 12 ? 12 : bit + 1]; b.Add((byte)(v & 0xFF)); b.Add((byte)(v >> 8)); }
        return b.ToArray();
    }

    private static ushort ValueFor(int bit) => BcsValues[bit + 1];

    [Fact]
    public void Bcs_all_4096_combinations_of_bits_0_to_11_decode_each_field_independently()
    {
        for (int flags = 0; flags < 0x1000; flags++)
        {
            var data = BuildBcs(flags);
            var r = BodyCompositionParser.ParseMeasurement(data);
            Assert.True(r.IsSuccess, $"flags {flags:X3}: {r.ErrorMessage}");
            Assert.Empty(r.Warnings);
            var d = r.Value!;
            var unit = (flags & 1) != 0 ? UnitSystem.Imperial : UnitSystem.Si;
            Assert.Equal(unit, d.Unit);
            Assert.Equal(21.5m, d.BodyFatPercent);
            Assert.Equal((flags & 2) != 0, d.TimeStamp.HasValue);
            Assert.Equal((flags & 4) != 0 ? 3 : (int?)null, d.UserId);
            Assert.Equal((flags & 8) != 0 ? ValueFor(3) : (int?)null, d.BasalMetabolismKj);
            Assert.Equal((flags & 16) != 0 ? ValueFor(4) * 0.1m : (decimal?)null, d.MusclePercent);
            Assert.Equal((flags & 32) != 0 ? (int?)ValueFor(5) : null, d.MuscleMass?.Raw);
            Assert.Equal((flags & 64) != 0 ? (int?)ValueFor(6) : null, d.FatFreeMass?.Raw);
            Assert.Equal((flags & 128) != 0 ? (int?)ValueFor(7) : null, d.SoftLeanMass?.Raw);
            Assert.Equal((flags & 256) != 0 ? (int?)ValueFor(8) : null, d.BodyWaterMass?.Raw);
            Assert.Equal((flags & 512) != 0 ? ValueFor(9) * 0.1m : (decimal?)null, d.ImpedanceOhm);
            Assert.Equal((flags & 1024) != 0 ? (int?)ValueFor(10) : null, d.Weight?.Raw);
            Assert.Equal((flags & 2048) != 0 ? (int?)ValueFor(11) : null, d.Height?.Raw);
            if (d.Weight is { } w)
                Assert.Equal(unit == UnitSystem.Si ? ValueFor(10) * 0.005m : ValueFor(10) * 0.01m * 0.45359237m, w.Kilograms);
        }
    }

    [Fact]
    public void Bcs_every_proper_prefix_of_every_combination_is_a_clean_error()
    {
        for (int flags = 0; flags < 0x1000; flags += 1)
        {
            var full = BuildBcs(flags);
            for (int len = 0; len < full.Length; len++)
            {
                var r = BodyCompositionParser.ParseMeasurement(full.AsSpan(0, len));
                Assert.False(r.IsSuccess, $"flags {flags:X3} len {len}");
                Assert.True(r.ErrorCode is ParseErrorCode.TooShort or ParseErrorCode.Truncated);
            }
        }
    }

    [Fact]
    public void Bcs_multi_packet_bit_is_recognised_for_every_other_combination()
    {
        for (int flags = 0; flags < 0x1000; flags += 7)
        {
            var r = BodyCompositionParser.ParseMeasurement(BuildBcs(flags | 0x1000));
            Assert.False(r.IsSuccess);
            Assert.Equal(ParseErrorCode.MultiPacketNotSupported, r.ErrorCode);
        }
    }

    [Fact]
    public void Bcs_unsuccessful_keeps_only_timestamp_and_user()
    {
        var r = BodyCompositionParser.ParseMeasurement(BuildBcs(0x0FFE, bodyFat: 0xFFFF)); // 声明了全部可选字段
        Assert.True(r.IsSuccess);
        Assert.Contains(ParseWarningCode.FieldsIgnoredMeasurementUnsuccessful, r.Warnings);
        Assert.Contains(ParseWarningCode.TrailingBytes, r.Warnings);
        Assert.Null(r.Value!.Weight);
        Assert.Null(r.Value.MuscleMass);
        Assert.True(r.Value!.MeasurementUnsuccessful);
        Assert.Null(r.Value.BodyFatPercent);
        Assert.NotNull(r.Value.TimeStamp);
        Assert.Equal(3, r.Value.UserId);
    }
}
