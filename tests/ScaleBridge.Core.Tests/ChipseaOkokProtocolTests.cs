using ScaleBridge.Core.Protocols;

namespace ScaleBridge.Core.Tests;

/// <summary>Chipsea / OKOK 广播帧手写用例。期望值是独立字面量，不复用解析器的换算。</summary>
public class ChipseaOkokProtocolTests
{
    private static byte Xor(byte[] data, int start, int endExclusive)
    {
        byte sum = 0;
        for (var i = start; i < endExclusive; i++) sum ^= data[i];
        return sum;
    }

    /// <summary>19 字节 v1.1。checksumFromMagic 为 true 时校验和含 0xCA。</summary>
    private static byte[] V11(byte locked, byte deviceType, ushort raw, byte props, bool checksumFromMagic = false, byte[]? extra = null)
    {
        var bytes = new byte[19 + (extra?.Length ?? 0)];
        bytes[0] = 0xCA;
        bytes[1] = 0x11;
        bytes[2] = 0x0F;
        bytes[3] = locked;
        bytes[4] = deviceType;
        bytes[5] = (byte)(raw >> 8);
        bytes[6] = (byte)raw;
        bytes[7] = 0x11;
        bytes[8] = 0x22;
        bytes[9] = 0x33;
        bytes[10] = 0x44;
        bytes[11] = props;
        bytes[18] = Xor(bytes, checksumFromMagic ? 0 : 1, 18);
        extra?.CopyTo(bytes, 19);
        return bytes;
    }

    private static byte[] V20(byte finalFlag, ushort raw, ushort impedance, byte[]? extra = null, byte seq = 0x31)
    {
        var bytes = new byte[15 + (extra?.Length ?? 0)];
        bytes[0] = 0xCA;
        bytes[1] = 0x20;
        bytes[2] = 0x0B;
        bytes[7] = 0x01;
        bytes[8] = finalFlag;
        bytes[9] = seq;
        bytes[10] = (byte)(raw >> 8);
        bytes[11] = (byte)raw;
        bytes[12] = (byte)(impedance >> 8);
        bytes[13] = (byte)impedance;
        bytes[14] = Xor(bytes, 1, 14);
        extra?.CopyTo(bytes, 15);
        return bytes;
    }

    private static void AssertMass(VendorWeightReading v, VendorProtocol protocol, int raw, VendorWeightUnit unit, decimal original, decimal kg, bool stabilized)
    {
        Assert.Equal(protocol, v.Protocol);
        Assert.Equal(raw, v.RawWeight);
        Assert.Equal(unit, v.RawUnit);
        Assert.Equal(original, v.OriginalWeight);
        Assert.Equal(kg, v.WeightKg);
        Assert.Null(v.Timestamp);
        Assert.False(v.WeightRemoved);
        Assert.Equal(stabilized, v.Stabilized);
        Assert.Equal(stabilized, v.IsFinal);
    }

    [Theory]
    [InlineData(0x00, 731, VendorWeightUnit.Kg, "73.1", "73.1")] // 1 decimal
    [InlineData(0x02, 75, VendorWeightUnit.Kg, "75", "75")] // 0 decimals
    [InlineData(0x04, 7310, VendorWeightUnit.Kg, "73.1", "73.1")] // 2 decimals
    [InlineData(0x04, 12345, VendorWeightUnit.Kg, "123.45", "123.45")]
    [InlineData(0x04, 1, VendorWeightUnit.Kg, "0.01", "0.01")]
    [InlineData(0x00, 0, VendorWeightUnit.Kg, "0", "0")]
    [InlineData(0x00, 65535, VendorWeightUnit.Kg, "6553.5", "6553.5")]
    [InlineData(0x08, 1500, VendorWeightUnit.Jin, "150", "75")] // jin, 1 decimal
    [InlineData(0x0A, 3, VendorWeightUnit.Jin, "3", "1.5")] // jin, 0 decimals
    [InlineData(0x0C, 15025, VendorWeightUnit.Jin, "150.25", "75.125")] // jin, 2 decimals
    [InlineData(0x10, 500, VendorWeightUnit.Lb, "50", "22.6796185")] // lb, 1 decimal
    [InlineData(0x12, 150, VendorWeightUnit.Lb, "150", "68.0388555")] // lb, 0 decimals
    [InlineData(0x14, 15025, VendorWeightUnit.Lb, "150.25", "68.1522535925")] // lb, 2 decimals
    [InlineData(0x14, 1, VendorWeightUnit.Lb, "0.01", "0.0045359237")]
    public void V11_unit_and_decimal_combinations(byte props, int raw, VendorWeightUnit unit, string original, string kg)
    {
        var result = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, (ushort)raw, props));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
        Assert.Null(result.Value!.ImpedanceRaw);
        AssertMass(result.Value, VendorProtocol.ChipseaOkok11, raw, unit,
            decimal.Parse(original, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture),
            stabilized: true);
    }

    [Fact]
    public void V11_unlocked_and_other_lock_values_are_not_final()
    {
        var unlocked = ChipseaOkokParser.ParseV11Broadcast(V11(0x00, 0x01, 500, 0x00)).Value!;
        Assert.False(unlocked.Stabilized);
        Assert.False(unlocked.IsFinal);
        Assert.Equal(50m, unlocked.OriginalWeight);

        var other = ChipseaOkokParser.ParseV11Broadcast(V11(0x02, 0x01, 500, 0x00)).Value!;
        Assert.False(other.Stabilized);
        Assert.Equal(50m, other.WeightKg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V11_both_checksum_rules_accepted(bool includeMagic)
    {
        var result = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 0x02DB, 0x00, includeMagic));
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(result.Warnings);
        AssertMass(result.Value!, VendorProtocol.ChipseaOkok11, 731, VendorWeightUnit.Kg, 73.1m, 73.1m, true);
    }

    [Fact]
    public void V11_bad_checksum()
    {
        var frame = V11(0x01, 0x01, 500, 0x00);
        frame[18] ^= 0xFF;
        var result = ChipseaOkokParser.ParseV11Broadcast(frame);
        Assert.Equal(VendorParseErrorCode.BadChecksum, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(0xCB, 0x11, 0x0F)]
    [InlineData(0xCA, 0x12, 0x0F)]
    [InlineData(0xCA, 0x11, 0x0E)]
    public void V11_bad_header(byte magic, byte version, byte length)
    {
        var frame = V11(0x01, 0x01, 500, 0x00);
        frame[0] = magic;
        frame[1] = version;
        frame[2] = length;
        frame[18] = Xor(frame, 1, 18);
        var result = ChipseaOkokParser.ParseV11Broadcast(frame);
        Assert.Equal(VendorParseErrorCode.BadHeader, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void V11_undefined_decimal_bits()
    {
        var result = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 731, 0x06));
        Assert.Equal(VendorParseErrorCode.InvalidValue, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void V11_stone_pound_ignores_decimal_bits()
    {
        // 4 st + 13.0 lb = 69 lb；小数位 10（props 0x1A）不影响。
        var plain = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 0x0482, 0x18)).Value!;
        var withDecimals = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 0x0482, 0x1A)).Value!;
        AssertMass(plain, VendorProtocol.ChipseaOkok11, 1154, VendorWeightUnit.StoneLb, 69m, 31.29787353m, true);
        AssertMass(withDecimals, VendorProtocol.ChipseaOkok11, 1154, VendorWeightUnit.StoneLb, 69m, 31.29787353m, true);
        Assert.Null(plain.ImpedanceRaw);

        // 0 st 9.5 lb
        var poundsOnly = ChipseaOkokParser.ParseV11Broadcast(V11(0x00, 0x01, 0x005F, 0x18)).Value!;
        AssertMass(poundsOnly, VendorProtocol.ChipseaOkok11, 95, VendorWeightUnit.StoneLb, 9.5m, 4.309127515m, false);

        // 1 st 0.1 lb = 14.1 lb
        var mixed = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 0x0101, 0x1E)).Value!;
        AssertMass(mixed, VendorProtocol.ChipseaOkok11, 257, VendorWeightUnit.StoneLb, 14.1m, 6.395652417m, true);

        // 255 st + 25.5 lb
        var max = ChipseaOkokParser.ParseV11Broadcast(V11(0x01, 0x01, 0xFFFF, 0x18)).Value!;
        Assert.Equal(65535, max.RawWeight);
        Assert.Equal(3595.5m, max.OriginalWeight);
        Assert.Equal(1630.891366335m, max.WeightKg);
    }

    [Fact]
    public void V11_reserved_bits_device_type_and_trailing_mac_ignored()
    {
        var frame = V11(0x01, 0x02, 731, 0xE1, extra: new byte[] { 0xC8, 0xB2, 0x1E, 0x15, 0x6E, 0xDB });
        frame[12] = 0x01;
        frame[17] = 0x06;
        frame[18] = Xor(frame, 1, 18);
        var result = ChipseaOkokParser.ParseV11Broadcast(frame);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
        Assert.Equal(73.1m, result.Value!.WeightKg);
        Assert.Equal(VendorWeightUnit.Kg, result.Value.RawUnit);
    }

    [Fact]
    public void V11_every_short_length_never_throws()
    {
        for (var n = 0; n < ChipseaOkokParser.V11FrameSize; n++)
        {
            var result = ChipseaOkokParser.ParseV11Broadcast(new byte[n]);
            Assert.Equal(VendorParseErrorCode.TooShort, result.ErrorCode);
            Assert.Null(result.Value);
        }
    }

    [Theory]
    [InlineData(null, VendorWeightUnit.Kg, "81.2", "81.2", true)]
    [InlineData(VendorWeightUnit.Kg, VendorWeightUnit.Kg, "81.2", "81.2", false)]
    [InlineData(VendorWeightUnit.Lb, VendorWeightUnit.Lb, "81.2", "36.831700444", false)]
    [InlineData(VendorWeightUnit.Jin, VendorWeightUnit.Jin, "81.2", "40.6", false)]
    public void V20_context_units(VendorWeightUnit? assumed, VendorWeightUnit unit, string original, string kg, bool unitAssumed)
    {
        var result = ChipseaOkokParser.ParseV20Broadcast(V20(0x01, 812, 4871), assumed);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        AssertMass(result.Value!, VendorProtocol.ChipseaOkok20, 812, unit,
            decimal.Parse(original, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture),
            stabilized: true);
        Assert.Equal(4871, result.Value!.ImpedanceRaw);
        if (unitAssumed)
            Assert.Equal(new[] { VendorParseWarningCode.UnitAssumed }, result.Warnings);
        else
            Assert.Empty(result.Warnings);
    }

    [Fact]
    public void V20_unfinal_zero_and_non_one_flag()
    {
        var unfinal = ChipseaOkokParser.ParseV20Broadcast(V20(0x00, 0, 0)).Value!;
        Assert.False(unfinal.Stabilized);
        Assert.False(unfinal.IsFinal);
        Assert.Equal(0m, unfinal.WeightKg);
        Assert.Equal(0, unfinal.ImpedanceRaw);
        Assert.Contains(VendorParseWarningCode.UnitAssumed, ChipseaOkokParser.ParseV20Broadcast(V20(0x00, 0, 0)).Warnings);

        var other = ChipseaOkokParser.ParseV20Broadcast(V20(0x02, 10, 1), VendorWeightUnit.Kg).Value!;
        Assert.False(other.Stabilized);
        Assert.Equal(1m, other.OriginalWeight);
        Assert.Equal(1, other.ImpedanceRaw);
    }

    [Fact]
    public void V20_stone_lb_context_rejected()
    {
        var result = ChipseaOkokParser.ParseV20Broadcast(V20(0x01, 812, 4871), VendorWeightUnit.StoneLb);
        Assert.Equal(VendorParseErrorCode.InvalidValue, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void V20_bad_checksum_and_header()
    {
        var badSum = V20(0x01, 812, 4871);
        badSum[14] ^= 0x01;
        Assert.Equal(VendorParseErrorCode.BadChecksum, ChipseaOkokParser.ParseV20Broadcast(badSum).ErrorCode);

        var badMagic = V20(0x01, 812, 4871);
        badMagic[0] = 0xC0;
        badMagic[14] = Xor(badMagic, 1, 14);
        Assert.Equal(VendorParseErrorCode.BadHeader, ChipseaOkokParser.ParseV20Broadcast(badMagic).ErrorCode);

        var badVersion = V20(0x01, 812, 4871);
        badVersion[1] = 0x21;
        badVersion[14] = Xor(badVersion, 1, 14);
        Assert.Equal(VendorParseErrorCode.BadHeader, ChipseaOkokParser.ParseV20Broadcast(badVersion).ErrorCode);

        var badLen = V20(0x01, 812, 4871);
        badLen[2] = 0x0A;
        badLen[14] = Xor(badLen, 1, 14);
        Assert.Equal(VendorParseErrorCode.BadHeader, ChipseaOkokParser.ParseV20Broadcast(badLen).ErrorCode);
    }

    [Fact]
    public void V20_trailing_bytes_ignored_and_short_lengths_never_throw()
    {
        var result = ChipseaOkokParser.ParseV20Broadcast(V20(0x01, 812, 4871, extra: new byte[] { 1, 2, 3, 4, 5, 6 }));
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { VendorParseWarningCode.UnitAssumed }, result.Warnings);
        Assert.Equal(81.2m, result.Value!.WeightKg);

        for (var n = 0; n < ChipseaOkokParser.V20FrameSize; n++)
        {
            var shortResult = ChipseaOkokParser.ParseV20Broadcast(new byte[n], VendorWeightUnit.Lb);
            Assert.Equal(VendorParseErrorCode.TooShort, shortResult.ErrorCode);
            Assert.Null(shortResult.Value);
        }
    }

    [Fact]
    public void Dispatcher_routes_and_rejects()
    {
        var v11 = ChipseaOkokParser.ParseManufacturerData(V11(0x01, 0x01, 500, 0x10));
        Assert.Equal(VendorProtocol.ChipseaOkok11, v11.Value!.Protocol);
        Assert.Equal(VendorWeightUnit.Lb, v11.Value.RawUnit);
        Assert.Equal(22.6796185m, v11.Value.WeightKg);

        var v20 = ChipseaOkokParser.ParseManufacturerData(V20(0x01, 1790, 4871), VendorWeightUnit.Lb);
        Assert.Equal(VendorProtocol.ChipseaOkok20, v20.Value!.Protocol);
        Assert.Equal(179m, v20.Value.OriginalWeight);
        Assert.Equal(81.19303423m, v20.Value.WeightKg);
        Assert.Empty(v20.Warnings);

        var assumed = ChipseaOkokParser.ParseManufacturerData(V20(0x00, 10, 0));
        Assert.Equal(new[] { VendorParseWarningCode.UnitAssumed }, assumed.Warnings);
        Assert.Equal(VendorWeightUnit.Kg, assumed.Value!.RawUnit);

        Assert.Equal(VendorParseErrorCode.InvalidValue,
            ChipseaOkokParser.ParseManufacturerData(V20(0x01, 10, 0), VendorWeightUnit.StoneLb).ErrorCode);
        Assert.Equal(VendorParseErrorCode.BadHeader,
            ChipseaOkokParser.ParseManufacturerData(new byte[] { 0xCA, 0x30, 0, 0 }).ErrorCode);
        Assert.Equal(VendorParseErrorCode.BadHeader,
            ChipseaOkokParser.ParseManufacturerData(new byte[] { 0xC0, 0x20 }).ErrorCode);

        for (var n = 0; n < 2; n++)
            Assert.Equal(VendorParseErrorCode.TooShort, ChipseaOkokParser.ParseManufacturerData(new byte[n]).ErrorCode);
    }

    [Fact]
    public void Random_bytes_length_0_to_40_never_throw()
    {
        var rng = new Random(20261026);
        for (var n = 0; n <= 40; n++)
        {
            var buf = new byte[n];
            rng.NextBytes(buf);
            var a = ChipseaOkokParser.ParseV11Broadcast(buf);
            var b = ChipseaOkokParser.ParseV20Broadcast(buf);
            var c = ChipseaOkokParser.ParseV20Broadcast(buf, VendorWeightUnit.Jin);
            var d = ChipseaOkokParser.ParseV20Broadcast(buf, VendorWeightUnit.StoneLb);
            var e = ChipseaOkokParser.ParseManufacturerData(buf);
            var f = ChipseaOkokParser.ParseManufacturerData(buf, VendorWeightUnit.Lb);
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotNull(c);
            Assert.NotNull(d);
            Assert.NotNull(e);
            Assert.NotNull(f);
        }
    }
}
