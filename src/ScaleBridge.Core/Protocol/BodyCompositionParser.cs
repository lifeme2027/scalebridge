using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Protocol;

/// <summary>
/// Body Composition Service（0x181B）两个特征的解析器：Body Composition Feature（0x2A9B）、Body Composition Measurement（0x2A9C）。
/// 独立实现；字段布局依据 Bluetooth SIG Body Composition Service v1.0.1（见 NOTES.md）。
/// 多包测量（Flags 位 12）阶段1只识别并返回 <see cref="ParseErrorCode.MultiPacketNotSupported"/>，不崩溃。
/// </summary>
public static class BodyCompositionParser
{
    public const int FeatureSize = 4;
    /// <summary>Flags(2) + Body Fat Percentage(2)。</summary>
    public const int MinMeasurementSize = 4;

    // Feature 位
    private const int FeatMassResShift = 11;   // 4 位
    private const int FeatHeightResShift = 15; // 3 位
    private const uint FeatReservedMask = 0xFFFC0000u; // 位 18–31

    // Measurement Flags 位
    private const ushort FlagImperial = 1 << 0;
    private const ushort FlagTimeStamp = 1 << 1;
    private const ushort FlagUserId = 1 << 2;
    private const ushort FlagBasalMetabolism = 1 << 3;
    private const ushort FlagMusclePercentage = 1 << 4;
    private const ushort FlagMuscleMass = 1 << 5;
    private const ushort FlagFatFreeMass = 1 << 6;
    private const ushort FlagSoftLeanMass = 1 << 7;
    private const ushort FlagBodyWaterMass = 1 << 8;
    private const ushort FlagImpedance = 1 << 9;
    private const ushort FlagWeight = 1 << 10;
    private const ushort FlagHeight = 1 << 11;
    private const ushort FlagMultiplePacket = 1 << 12;
    private const ushort FlagReservedMask = 0xE000; // 位 13–15

    public static ParseResult<BodyCompositionFeature> ParseFeature(ReadOnlySpan<byte> data)
    {
        if (data.Length < FeatureSize)
            return ParseResult<BodyCompositionFeature>.Fail(ParseErrorCode.TooShort,
                $"Body Composition Feature needs {FeatureSize} bytes, got {data.Length}.");

        var r = new ByteReader(data);
        r.TryReadUInt32(out var raw);
        var warnings = new List<ParseWarningCode>();
        if (data.Length > FeatureSize) warnings.Add(ParseWarningCode.TrailingBytes);
        if ((raw & FeatReservedMask) != 0) warnings.Add(ParseWarningCode.ReservedBitsSet);

        bool Bit(int n) => (raw & (1u << n)) != 0;
        var massRes = (int)((raw >> FeatMassResShift) & 0xF);
        var heightRes = (int)((raw >> FeatHeightResShift) & 0x7);
        if (FeatureResolutions.IsReservedMass(massRes) || FeatureResolutions.IsReservedHeight(heightRes))
            warnings.Add(ParseWarningCode.ReservedResolutionCode);

        var value = new BodyCompositionFeature(raw,
            Bit(0), Bit(1), Bit(2), Bit(3), Bit(4), Bit(5), Bit(6), Bit(7), Bit(8), Bit(9), Bit(10),
            massRes, heightRes);
        return ParseResult<BodyCompositionFeature>.Ok(value, warnings);
    }

    public static ParseResult<BodyCompositionMeasurementData> ParseMeasurement(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return ParseResult<BodyCompositionMeasurementData>.Fail(ParseErrorCode.TooShort,
                $"Body Composition Measurement needs at least {MinMeasurementSize} bytes (Flags + Body Fat Percentage), got {data.Length}.");

        var r = new ByteReader(data);
        r.TryReadUInt16(out var flags);

        // 多包测量：续包不含 Time Stamp / User ID，字段被拆分到两个包里；阶段1不拼包，直接识别后拒绝。
        if ((flags & FlagMultiplePacket) != 0)
            return ParseResult<BodyCompositionMeasurementData>.Fail(ParseErrorCode.MultiPacketNotSupported,
                "Body Composition Measurement with the Multiple Packet Measurement flag is not supported yet (phase 1).");

        if (data.Length < MinMeasurementSize)
            return ParseResult<BodyCompositionMeasurementData>.Fail(ParseErrorCode.TooShort,
                $"Body Composition Measurement needs at least {MinMeasurementSize} bytes (Flags + Body Fat Percentage), got {data.Length}.");

        r.TryReadUInt16(out var rawBodyFat);
        var warnings = new List<ParseWarningCode>();
        if ((flags & FlagReservedMask) != 0) warnings.Add(ParseWarningCode.ReservedBitsSet);

        var unit = (flags & FlagImperial) != 0 ? UnitSystem.Imperial : UnitSystem.Si;
        var unsuccessful = rawBodyFat == 0xFFFF;
        decimal? bodyFat = unsuccessful ? null : rawBodyFat * 0.1m;

        DateTime? timeStamp = null;
        if ((flags & FlagTimeStamp) != 0)
        {
            if (!r.TryReadBytes(GattDateTime.Size, out var ts)) return Truncated("Time Stamp", GattDateTime.Size, r);
            timeStamp = GattDateTime.TryParse(ts);
            if (timeStamp is null) warnings.Add(ParseWarningCode.InvalidTimestamp);
        }

        int? userId = null;
        if ((flags & FlagUserId) != 0)
        {
            if (!r.TryReadByte(out var uid)) return Truncated("User ID", 1, r);
            userId = uid == 0xFF ? null : uid;
        }

        const ushort OptionalMask = FlagBasalMetabolism | FlagMusclePercentage | FlagMuscleMass | FlagFatFreeMass |
                                    FlagSoftLeanMass | FlagBodyWaterMass | FlagImpedance | FlagWeight | FlagHeight;
        if (unsuccessful)
        {
            if ((flags & OptionalMask) != 0) warnings.Add(ParseWarningCode.FieldsIgnoredMeasurementUnsuccessful);
            if (r.Remaining > 0) warnings.Add(ParseWarningCode.TrailingBytes);
            return ParseResult<BodyCompositionMeasurementData>.Ok(
                new BodyCompositionMeasurementData(flags, unit, true, null, timeStamp, userId,
                    null, null, null, null, null, null, null, null, null), warnings);
        }

        int? basal = null;
        decimal? musclePct = null, impedance = null;
        MassValue? muscleMass = null, fatFree = null, softLean = null, bodyWater = null, weight = null;
        LengthValue? height = null;

        // 读一个 uint16 可选字段；0xFFFF → 未知 (null) + 警告
        bool ReadU16(ushort flagBit, string name, ref ByteReader reader, out ushort? value, out ParseResult<BodyCompositionMeasurementData>? failure)
        {
            value = null;
            failure = null;
            if ((flags & flagBit) == 0) return true;
            if (!reader.TryReadUInt16(out var v))
            {
                failure = Truncated(name, 2, reader);
                return false;
            }
            if (v == 0xFFFF)
            {
                if (!warnings.Contains(ParseWarningCode.OptionalFieldMarkedUnknown))
                    warnings.Add(ParseWarningCode.OptionalFieldMarkedUnknown);
                return true;
            }
            value = v;
            return true;
        }

        ParseResult<BodyCompositionMeasurementData>? fail;
        ushort? u;

        if (!ReadU16(FlagBasalMetabolism, "Basal Metabolism", ref r, out u, out fail)) return fail!;
        if (u is { } bm) basal = bm;
        if (!ReadU16(FlagMusclePercentage, "Muscle Percentage", ref r, out u, out fail)) return fail!;
        if (u is { } mp) musclePct = mp * 0.1m;
        if (!ReadU16(FlagMuscleMass, "Muscle Mass", ref r, out u, out fail)) return fail!;
        if (u is { } mm) muscleMass = UnitConversion.DecodeMass(mm, unit);
        if (!ReadU16(FlagFatFreeMass, "Fat Free Mass", ref r, out u, out fail)) return fail!;
        if (u is { } ffm) fatFree = UnitConversion.DecodeMass(ffm, unit);
        if (!ReadU16(FlagSoftLeanMass, "Soft Lean Mass", ref r, out u, out fail)) return fail!;
        if (u is { } slm) softLean = UnitConversion.DecodeMass(slm, unit);
        if (!ReadU16(FlagBodyWaterMass, "Body Water Mass", ref r, out u, out fail)) return fail!;
        if (u is { } bwm) bodyWater = UnitConversion.DecodeMass(bwm, unit);
        if (!ReadU16(FlagImpedance, "Impedance", ref r, out u, out fail)) return fail!;
        if (u is { } imp) impedance = imp * 0.1m;
        if (!ReadU16(FlagWeight, "Weight", ref r, out u, out fail)) return fail!;
        if (u is { } w) weight = UnitConversion.DecodeMass(w, unit);
        if (!ReadU16(FlagHeight, "Height", ref r, out u, out fail)) return fail!;
        if (u is { } h) height = UnitConversion.DecodeHeight(h, unit);

        if (r.Remaining > 0) warnings.Add(ParseWarningCode.TrailingBytes);

        return ParseResult<BodyCompositionMeasurementData>.Ok(
            new BodyCompositionMeasurementData(flags, unit, false, bodyFat, timeStamp, userId, basal, musclePct,
                muscleMass, fatFree, softLean, bodyWater, impedance, weight, height), warnings);
    }

    private static ParseResult<BodyCompositionMeasurementData> Truncated(string field, int need, ByteReader r) =>
        ParseResult<BodyCompositionMeasurementData>.Fail(ParseErrorCode.Truncated,
            $"Body Composition Measurement truncated: field '{field}' needs {need} byte(s) at offset {r.Position}, only {r.Remaining} left.");
}
