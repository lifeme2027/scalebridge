using ScaleBridge.Core.Protocol;
using ScaleBridge.Core.Protocols;

namespace ScaleBridge.Core.Tests;

/// <summary>小米帧解析器的手写用例：单位、标志、时间戳、长度与乱码都不抛异常。</summary>
public class VendorProtocolTests
{
    private static readonly DateTime Stamp = new(2026, 10, 26, 8, 30, 15, DateTimeKind.Unspecified);

    private static byte[] Mi(byte status, ushort raw, byte[]? timestamp = null, byte[]? extra = null)
    {
        timestamp ??= GattDateTime.Encode(Stamp);
        var bytes = new byte[10 + (extra?.Length ?? 0)];
        bytes[0] = status;
        bytes[1] = (byte)raw;
        bytes[2] = (byte)(raw >> 8);
        timestamp.CopyTo(bytes, 3);
        extra?.CopyTo(bytes, 10);
        return bytes;
    }

    private static byte[] Bc(byte ctrl0, byte ctrl1, ushort raw, ushort impedance = 0, byte[]? timestamp = null, byte[]? extra = null)
    {
        timestamp ??= GattDateTime.Encode(Stamp);
        var bytes = new byte[13 + (extra?.Length ?? 0)];
        bytes[0] = ctrl0;
        bytes[1] = ctrl1;
        timestamp.CopyTo(bytes, 2);
        bytes[9] = (byte)impedance;
        bytes[10] = (byte)(impedance >> 8);
        bytes[11] = (byte)raw;
        bytes[12] = (byte)(raw >> 8);
        extra?.CopyTo(bytes, 13);
        return bytes;
    }

    [Fact]
    public void Reading_is_marked_experimental() =>
        Assert.True(VendorWeightReading.IsExperimental);

    [Theory]
    [InlineData(0x20, 14000, VendorWeightUnit.Kg, "70", "70")] // bit5 stable, neither unit bit
    [InlineData(0x22, 14000, VendorWeightUnit.Kg, "70", "70")] // bit1 set, still kg
    [InlineData(0x22, 1, VendorWeightUnit.Kg, "0.005", "0.005")]
    [InlineData(0x22, 0, VendorWeightUnit.Kg, "0", "0")]
    [InlineData(0x22, 65535, VendorWeightUnit.Kg, "327.675", "327.675")]
    [InlineData(0x21, 15432, VendorWeightUnit.Lb, "154.32", "69.9983745384")]
    [InlineData(0x30, 14000, VendorWeightUnit.Jin, "140", "70")]
    [InlineData(0x30, 12345, VendorWeightUnit.Jin, "123.45", "61.725")]
    public void MiScale_units(byte status, int raw, VendorWeightUnit unit, string original, string kg)
    {
        var result = XiaomiScaleParser.ParseMiScaleFrame(Mi(status, (ushort)raw));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
        var v = result.Value!;
        Assert.Equal(VendorProtocol.XiaomiMiScale, v.Protocol);
        Assert.Equal(raw, v.RawWeight);
        Assert.Equal(unit, v.RawUnit);
        Assert.Equal(decimal.Parse(original, System.Globalization.CultureInfo.InvariantCulture), v.OriginalWeight);
        Assert.Equal(decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture), v.WeightKg);
        Assert.Null(v.ImpedanceRaw);
        Assert.Equal(Stamp, v.Timestamp);
    }

    [Fact]
    public void MiScale_ignored_status_bits_produce_no_warning()
    {
        // bits 1,2,3,6 未记录，置位也不告警；bit5 稳定。
        var result = XiaomiScaleParser.ParseMiScaleFrame(Mi(0x6E, 200));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
        Assert.Equal(VendorWeightUnit.Kg, result.Value!.RawUnit);
        Assert.Equal(1m, result.Value.OriginalWeight);
        Assert.True(result.Value.IsFinal);
    }

    [Theory]
    [InlineData(0x02, false, false, false)] // 未稳定
    [InlineData(0x22, true, false, true)]
    [InlineData(0xA2, true, true, false)] // 稳定但离秤
    [InlineData(0x80, false, true, false)]
    public void MiScale_flag_combinations(byte status, bool stabilized, bool removed, bool final)
    {
        var v = XiaomiScaleParser.ParseMiScaleFrame(Mi(status, 14000)).Value!;
        Assert.Equal(stabilized, v.Stabilized);
        Assert.Equal(removed, v.WeightRemoved);
        Assert.Equal(final, v.IsFinal);
        Assert.Equal(Stamp, v.Timestamp); // 未稳定时仍然返回合法时间戳
    }

    [Fact]
    public void MiScale_ambiguous_unit()
    {
        var result = XiaomiScaleParser.ParseMiScaleFrame(Mi(0x31, 14000)); // bit0 + bit4
        Assert.False(result.IsSuccess);
        Assert.Equal(VendorParseErrorCode.AmbiguousUnit, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MiScale_invalid_and_zero_timestamp()
    {
        var bad = GattDateTime.Encode(Stamp);
        bad[2] = 13; // month
        var invalid = XiaomiScaleParser.ParseMiScaleFrame(Mi(0x22, 14000, bad));
        Assert.True(invalid.IsSuccess);
        Assert.Null(invalid.Value!.Timestamp);
        Assert.Equal(new[] { VendorParseWarningCode.InvalidTimestamp }, invalid.Warnings);

        var zero = XiaomiScaleParser.ParseMiScaleFrame(Mi(0x22, 14000, new byte[7]));
        Assert.Null(zero.Value!.Timestamp);
        Assert.Contains(VendorParseWarningCode.InvalidTimestamp, zero.Warnings);
    }

    [Fact]
    public void MiScale_trailing_bytes_and_invalid_timestamp_together()
    {
        var result = XiaomiScaleParser.ParseMiScaleFrame(Mi(0x22, 14000, new byte[7], new byte[] { 0xAA }));
        Assert.True(result.IsSuccess);
        Assert.Equal(70m, result.Value!.WeightKg);
        Assert.Equal(
            new[] { VendorParseWarningCode.InvalidTimestamp, VendorParseWarningCode.TrailingBytes },
            result.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void MiScale_too_short_never_throws(int length)
    {
        var data = new byte[length];
        var result = XiaomiScaleParser.ParseMiScaleFrame(data);
        Assert.False(result.IsSuccess);
        Assert.Equal(VendorParseErrorCode.TooShort, result.ErrorCode);
    }

    [Fact]
    public void MiScale_every_short_length()
    {
        for (var n = 0; n < XiaomiScaleParser.MiScaleFrameSize; n++)
        {
            var result = XiaomiScaleParser.ParseMiScaleFrame(new byte[n]);
            Assert.Equal(VendorParseErrorCode.TooShort, result.ErrorCode);
        }
    }

    [Theory]
    [InlineData(0x02, 0x22, VendorWeightUnit.Kg, 512)]
    [InlineData(0x03, 0x22, VendorWeightUnit.Lb, 480)] // ctrl0 bit1 ignored
    [InlineData(0x02, 0x62, VendorWeightUnit.Jin, 480)]
    [InlineData(0x02, 0x20, VendorWeightUnit.Kg, null)] // impedance bit clear; field bytes are nonzero below
    [InlineData(0x02, 0x26, VendorWeightUnit.Kg, 1)] // ctrl1 bit2 ignored
    public void BodyComposition_units_and_impedance(byte ctrl0, byte ctrl1, VendorWeightUnit unit, int? impedance)
    {
        ushort raw = unit == VendorWeightUnit.Lb ? (ushort)15432 : (ushort)14000;
        ushort field = impedance is null ? (ushort)0x0309 : (ushort)impedance.Value;
        var result = XiaomiScaleParser.ParseBodyCompositionScale2Frame(Bc(ctrl0, ctrl1, raw, field));
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(result.Warnings);
        var v = result.Value!;
        Assert.Equal(VendorProtocol.XiaomiBodyCompositionScale2, v.Protocol);
        Assert.Equal(unit, v.RawUnit);
        Assert.Equal(impedance, v.ImpedanceRaw);
        Assert.True(v.Stabilized);
        Assert.False(v.WeightRemoved);
        Assert.True(v.IsFinal);
        if (unit == VendorWeightUnit.Kg) Assert.Equal(70m, v.WeightKg);
        if (unit == VendorWeightUnit.Lb) Assert.Equal(154.32m, v.OriginalWeight);
        if (unit == VendorWeightUnit.Jin) Assert.Equal(70m, v.WeightKg);
    }

    [Fact]
    public void BodyComposition_impedance_field_ignored_when_bit_clear()
    {
        var v = XiaomiScaleParser.ParseBodyCompositionScale2Frame(Bc(0x02, 0x20, 14000, 777)).Value!;
        Assert.Null(v.ImpedanceRaw);
        Assert.Equal(70m, v.WeightKg);
    }

    [Theory]
    [InlineData(0x02, 0x02, false, false, false)]
    [InlineData(0x02, 0x22, true, false, true)]
    [InlineData(0x02, 0xA2, true, true, false)]
    [InlineData(0x02, 0x80, false, true, false)]
    public void BodyComposition_flag_combinations(byte ctrl0, byte ctrl1, bool stabilized, bool removed, bool final)
    {
        var v = XiaomiScaleParser.ParseBodyCompositionScale2Frame(Bc(ctrl0, ctrl1, 14000, 300)).Value!;
        Assert.Equal(stabilized, v.Stabilized);
        Assert.Equal(removed, v.WeightRemoved);
        Assert.Equal(final, v.IsFinal);
        Assert.Equal(stabilized || (ctrl1 & (1 << 1)) != 0 ? 300 : (int?)null, v.ImpedanceRaw);
        Assert.Equal(Stamp, v.Timestamp);
    }

    [Fact]
    public void BodyComposition_ambiguous_unit_beats_bad_timestamp()
    {
        var result = XiaomiScaleParser.ParseBodyCompositionScale2Frame(Bc(0x03, 0x62, 14000, timestamp: new byte[7], extra: new byte[] { 1 }));
        Assert.Equal(VendorParseErrorCode.AmbiguousUnit, result.ErrorCode);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BodyComposition_invalid_timestamp_and_trailing()
    {
        var ts = GattDateTime.Encode(Stamp);
        ts[3] = 32; // day
        var result = XiaomiScaleParser.ParseBodyCompositionScale2Frame(Bc(0x02, 0x22, 14001, 65535, ts, new byte[] { 0, 1 }));
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Timestamp);
        Assert.Equal(65535, result.Value.ImpedanceRaw);
        Assert.Equal(70.005m, result.Value.WeightKg);
        Assert.Equal(
            new[] { VendorParseWarningCode.InvalidTimestamp, VendorParseWarningCode.TrailingBytes },
            result.Warnings);
    }

    [Fact]
    public void BodyComposition_every_short_length()
    {
        for (var n = 0; n < XiaomiScaleParser.BodyCompositionScale2FrameSize; n++)
        {
            var result = XiaomiScaleParser.ParseBodyCompositionScale2Frame(new byte[n]);
            Assert.Equal(VendorParseErrorCode.TooShort, result.ErrorCode);
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public void Dispatcher_routes_known_services_and_rejects_others()
    {
        var mi = Mi(0x22, 14000);
        var bc = Bc(0x02, 0x22, 14000, 512);
        Assert.Equal(VendorProtocol.XiaomiMiScale, XiaomiScaleParser.ParseServiceData(0x181D, mi).Value!.Protocol);
        Assert.Equal(VendorProtocol.XiaomiBodyCompositionScale2, XiaomiScaleParser.ParseServiceData(0x181B, bc).Value!.Protocol);
        Assert.Equal(VendorParseErrorCode.TooShort, XiaomiScaleParser.ParseServiceData(0x181D, mi.AsSpan(0, 3)).ErrorCode);
        Assert.Equal(VendorParseErrorCode.TooShort, XiaomiScaleParser.ParseServiceData(0x181B, Array.Empty<byte>()).ErrorCode);

        var other = XiaomiScaleParser.ParseServiceData(0x180F, new byte[] { 1, 2, 3 });
        Assert.Equal(VendorParseErrorCode.UnsupportedService, other.ErrorCode);
        Assert.False(other.IsSuccess);
    }

    [Fact]
    public void Random_payloads_of_length_0_to_20_never_throw()
    {
        var rng = new Random(20261026);
        for (var len = 0; len <= 20; len++)
        {
            for (var n = 0; n < 12; n++)
            {
                var buf = new byte[len];
                rng.NextBytes(buf);
                var mi = XiaomiScaleParser.ParseMiScaleFrame(buf);
                var bc = XiaomiScaleParser.ParseBodyCompositionScale2Frame(buf);
                var viaA = XiaomiScaleParser.ParseServiceData(0x181D, buf);
                var viaB = XiaomiScaleParser.ParseServiceData(0x181B, buf);
                var other = XiaomiScaleParser.ParseServiceData(0, buf);

                Assert.NotNull(mi);
                Assert.NotNull(bc);
                Assert.Equal(mi.ErrorCode, viaA.ErrorCode);
                Assert.Equal(bc.ErrorCode, viaB.ErrorCode);
                Assert.Equal(VendorParseErrorCode.UnsupportedService, other.ErrorCode);
                if (len < 10) Assert.Equal(VendorParseErrorCode.TooShort, mi.ErrorCode);
                else Assert.NotEqual(VendorParseErrorCode.TooShort, mi.ErrorCode);
                if (len < 13) Assert.Equal(VendorParseErrorCode.TooShort, bc.ErrorCode);
                else Assert.NotEqual(VendorParseErrorCode.TooShort, bc.ErrorCode);
                if (mi.IsSuccess) Assert.NotNull(mi.Value);
                if (bc.IsSuccess) Assert.NotNull(bc.Value);
            }
        }
    }
}
