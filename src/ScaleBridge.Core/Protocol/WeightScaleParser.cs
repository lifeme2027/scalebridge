using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Protocol;

/// <summary>
/// Weight Scale Service（0x181D）两个特征的解析器：Weight Scale Feature（0x2A9E）、Weight Measurement（0x2A9D）。
/// 独立实现；字段布局依据 Bluetooth SIG Weight Scale Service v1.0.1（见 NOTES.md）。
/// 任何输入（含截断、乱码）都不会抛异常，而是返回带 <see cref="ParseErrorCode"/> 的失败结果。
/// </summary>
public static class WeightScaleParser
{
    public const int FeatureSize = 4;
    /// <summary>Flags(1) + Weight(2)。</summary>
    public const int MinMeasurementSize = 3;

    // Feature 位
    private const uint FeatTimeStamp = 1u << 0;
    private const uint FeatMultipleUsers = 1u << 1;
    private const uint FeatBmi = 1u << 2;
    private const int FeatWeightResShift = 3; // 4 位
    private const int FeatHeightResShift = 7; // 3 位
    private const uint FeatReservedMask = 0xFFFFFC00u; // 位 10–31

    // Measurement Flags 位
    private const byte FlagImperial = 1 << 0;
    private const byte FlagTimeStamp = 1 << 1;
    private const byte FlagUserId = 1 << 2;
    private const byte FlagBmiHeight = 1 << 3;
    private const byte FlagReservedMask = 0xF0;

    public static ParseResult<WeightScaleFeature> ParseFeature(ReadOnlySpan<byte> data)
    {
        if (data.Length < FeatureSize)
            return ParseResult<WeightScaleFeature>.Fail(ParseErrorCode.TooShort,
                $"Weight Scale Feature needs {FeatureSize} bytes, got {data.Length}.");

        var r = new ByteReader(data);
        r.TryReadUInt32(out var raw);
        var warnings = new List<ParseWarningCode>();
        if (data.Length > FeatureSize) warnings.Add(ParseWarningCode.TrailingBytes);
        if ((raw & FeatReservedMask) != 0) warnings.Add(ParseWarningCode.ReservedBitsSet);

        var weightRes = (int)((raw >> FeatWeightResShift) & 0xF);
        var heightRes = (int)((raw >> FeatHeightResShift) & 0x7);
        if (FeatureResolutions.IsReservedMass(weightRes) || FeatureResolutions.IsReservedHeight(heightRes))
            warnings.Add(ParseWarningCode.ReservedResolutionCode);

        var value = new WeightScaleFeature(raw,
            (raw & FeatTimeStamp) != 0, (raw & FeatMultipleUsers) != 0, (raw & FeatBmi) != 0,
            weightRes, heightRes);
        return ParseResult<WeightScaleFeature>.Ok(value, warnings);
    }

    public static ParseResult<WeightScaleMeasurementData> ParseMeasurement(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinMeasurementSize)
            return ParseResult<WeightScaleMeasurementData>.Fail(ParseErrorCode.TooShort,
                $"Weight Measurement needs at least {MinMeasurementSize} bytes (Flags + Weight), got {data.Length}.");

        var r = new ByteReader(data);
        r.TryReadByte(out var flags);
        r.TryReadUInt16(out var rawWeight);
        var warnings = new List<ParseWarningCode>();
        if ((flags & FlagReservedMask) != 0) warnings.Add(ParseWarningCode.ReservedBitsSet);

        var unit = (flags & FlagImperial) != 0 ? UnitSystem.Imperial : UnitSystem.Si;
        var unsuccessful = rawWeight == 0xFFFF;
        MassValue? weight = unsuccessful ? null : UnitConversion.DecodeMass(rawWeight, unit);

        DateTime? timeStamp = null;
        if ((flags & FlagTimeStamp) != 0)
        {
            if (!r.TryReadBytes(GattDateTime.Size, out var ts))
                return Truncated("Time Stamp", GattDateTime.Size, r);
            timeStamp = GattDateTime.TryParse(ts);
            if (timeStamp is null) warnings.Add(ParseWarningCode.InvalidTimestamp);
        }

        int? userId = null;
        if ((flags & FlagUserId) != 0)
        {
            if (!r.TryReadByte(out var uid)) return Truncated("User ID", 1, r);
            userId = uid == 0xFF ? null : uid; // 0xFF = unknown user
        }

        decimal? bmi = null;
        LengthValue? height = null;
        if ((flags & FlagBmiHeight) != 0)
        {
            if (unsuccessful)
            {
                // 规范：Measurement Unsuccessful 时，除 Time Stamp 与 User ID 外的可选字段应被禁用。
                warnings.Add(ParseWarningCode.FieldsIgnoredMeasurementUnsuccessful);
            }
            else
            {
                if (!r.TryReadUInt16(out var rawBmi)) return Truncated("BMI", 2, r);
                if (!r.TryReadUInt16(out var rawHeight)) return Truncated("Height", 2, r);
                if (rawBmi == 0xFFFF) warnings.Add(ParseWarningCode.OptionalFieldMarkedUnknown);
                else bmi = rawBmi * 0.1m;
                if (rawHeight == 0xFFFF)
                {
                    if (!warnings.Contains(ParseWarningCode.OptionalFieldMarkedUnknown))
                        warnings.Add(ParseWarningCode.OptionalFieldMarkedUnknown);
                }
                else height = UnitConversion.DecodeHeight(rawHeight, unit);
            }
        }

        if (r.Remaining > 0) warnings.Add(ParseWarningCode.TrailingBytes);

        return ParseResult<WeightScaleMeasurementData>.Ok(
            new WeightScaleMeasurementData(flags, unit, unsuccessful, weight, timeStamp, userId, bmi, height),
            warnings);
    }

    private static ParseResult<WeightScaleMeasurementData> Truncated(string field, int need, ByteReader r) =>
        ParseResult<WeightScaleMeasurementData>.Fail(ParseErrorCode.Truncated,
            $"Weight Measurement truncated: field '{field}' needs {need} byte(s) at offset {r.Position}, only {r.Remaining} left.");
}
