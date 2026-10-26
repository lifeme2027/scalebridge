using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Protocols;

/// <summary>
/// 小米体脂秤广告服务数据 / 体重通知帧解析器。
/// 实验性（仅按规范/公开资料实现，未真机验证）。字节布局见 NOTES.md「厂商协议来源（时尚1）」。
/// 任何输入（含截断、乱码）都不会抛异常，而是返回带 <see cref="VendorParseErrorCode"/> 的失败结果。
/// </summary>
public static class XiaomiScaleParser
{
    /// <summary>Frame A（Mi Scale / 0x181D）固定长度。</summary>
    public const int MiScaleFrameSize = 10;
    /// <summary>Frame B（Mi Body Composition Scale 2 / 0x181B）固定长度。</summary>
    public const int BodyCompositionScale2FrameSize = 13;

    public const ushort WeightScaleServiceUuid = 0x181D;
    public const ushort BodyCompositionServiceUuid = 0x181B;

    // Frame A status
    private const byte MiPounds = 1 << 0;
    private const byte MiJin = 1 << 4;
    private const byte MiStabilized = 1 << 5;
    private const byte MiWeightRemoved = 1 << 7;

    // Frame B control byte 0 / 1
    private const byte BcPounds = 1 << 0;
    private const byte BcImpedance = 1 << 1;
    private const byte BcStabilized = 1 << 5;
    private const byte BcJin = 1 << 6;
    private const byte BcWeightRemoved = 1 << 7;

    /// <summary>1 斤 = 500 g = 0.5 kg。</summary>
    private const decimal KilogramsPerJin = 0.5m;

    /// <summary>解析 Mi Scale（Frame A）10 字节载荷。更长的载荷解析前 10 字节并报告 <see cref="VendorParseWarningCode.TrailingBytes"/>。</summary>
    public static VendorParseResult<VendorWeightReading> ParseMiScaleFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MiScaleFrameSize)
            return VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.TooShort,
                $"Mi Scale frame needs {MiScaleFrameSize} bytes, got {payload.Length}.");

        var r = new ByteReader(payload);
        if (!r.TryReadByte(out var status) || !r.TryReadUInt16(out var rawWeight) || !r.TryReadBytes(GattDateTime.Size, out var ts))
            return VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.TooShort,
                $"Mi Scale frame needs {MiScaleFrameSize} bytes, got {payload.Length}.");

        if (!TryResolveUnit((status & MiPounds) != 0, (status & MiJin) != 0, out var unit))
            return Ambiguous();

        var warnings = new List<VendorParseWarningCode>();
        var timestamp = ReadTimestamp(ts, warnings);
        if (r.Remaining > 0) warnings.Add(VendorParseWarningCode.TrailingBytes);

        var (original, kg) = ToKilograms(rawWeight, unit);
        return VendorParseResult<VendorWeightReading>.Ok(new VendorWeightReading(
            VendorProtocol.XiaomiMiScale, rawWeight, unit, original, kg, timestamp,
            ImpedanceRaw: null,
            Stabilized: (status & MiStabilized) != 0,
            WeightRemoved: (status & MiWeightRemoved) != 0), warnings);
    }

    /// <summary>解析 Mi Body Composition Scale 2（Frame B）13 字节载荷。</summary>
    public static VendorParseResult<VendorWeightReading> ParseBodyCompositionScale2Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < BodyCompositionScale2FrameSize)
            return VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.TooShort,
                $"Mi Body Composition Scale 2 frame needs {BodyCompositionScale2FrameSize} bytes, got {payload.Length}.");

        var r = new ByteReader(payload);
        if (!r.TryReadByte(out var ctrl0) || !r.TryReadByte(out var ctrl1)
            || !r.TryReadBytes(GattDateTime.Size, out var ts)
            || !r.TryReadUInt16(out var impedance) || !r.TryReadUInt16(out var rawWeight))
            return VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.TooShort,
                $"Mi Body Composition Scale 2 frame needs {BodyCompositionScale2FrameSize} bytes, got {payload.Length}.");

        // 斤优先于磅；两者同时置位则无法判定。
        if (!TryResolveUnit((ctrl0 & BcPounds) != 0, (ctrl1 & BcJin) != 0, out var unit))
            return Ambiguous();

        var warnings = new List<VendorParseWarningCode>();
        var timestamp = ReadTimestamp(ts, warnings);
        if (r.Remaining > 0) warnings.Add(VendorParseWarningCode.TrailingBytes);

        int? impedanceRaw = (ctrl1 & BcImpedance) != 0 ? impedance : null;
        var (original, kg) = ToKilograms(rawWeight, unit);
        return VendorParseResult<VendorWeightReading>.Ok(new VendorWeightReading(
            VendorProtocol.XiaomiBodyCompositionScale2, rawWeight, unit, original, kg, timestamp,
            impedanceRaw,
            Stabilized: (ctrl1 & BcStabilized) != 0,
            WeightRemoved: (ctrl1 & BcWeightRemoved) != 0), warnings);
    }

    /// <summary>
    /// 按 16 位服务 UUID 分发：0x181D → Frame A，0x181B → Frame B，其余 <see cref="VendorParseErrorCode.UnsupportedService"/>。
    /// </summary>
    public static VendorParseResult<VendorWeightReading> ParseServiceData(ushort serviceUuid16, ReadOnlySpan<byte> payload) =>
        serviceUuid16 switch
        {
            WeightScaleServiceUuid => ParseMiScaleFrame(payload),
            BodyCompositionServiceUuid => ParseBodyCompositionScale2Frame(payload),
            _ => VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.UnsupportedService,
                $"Service 0x{serviceUuid16:X4} is not a supported Xiaomi scale service."),
        };

    private static bool TryResolveUnit(bool pounds, bool jin, out VendorWeightUnit unit)
    {
        if (pounds && jin)
        {
            unit = default;
            return false;
        }
        unit = jin ? VendorWeightUnit.Jin : pounds ? VendorWeightUnit.Lb : VendorWeightUnit.Kg;
        return true;
    }

    private static VendorParseResult<VendorWeightReading> Ambiguous() =>
        VendorParseResult<VendorWeightReading>.Fail(VendorParseErrorCode.AmbiguousUnit,
            "Unit flags are ambiguous (pounds and jin both set).");

    private static DateTime? ReadTimestamp(ReadOnlySpan<byte> data, List<VendorParseWarningCode> warnings)
    {
        var timestamp = GattDateTime.TryParse(data);
        if (timestamp is null) warnings.Add(VendorParseWarningCode.InvalidTimestamp);
        return timestamp;
    }

    /// <summary>kg：raw/200；lb：raw/100 再换算；斤：raw/100 再 ×0.5。</summary>
    private static (decimal Original, decimal Kilograms) ToKilograms(int raw, VendorWeightUnit unit)
    {
        switch (unit)
        {
            case VendorWeightUnit.Lb:
                var pounds = raw / 100m;
                return (pounds, UnitConversion.PoundsToKilograms(pounds));
            case VendorWeightUnit.Jin:
                var jin = raw / 100m;
                return (jin, jin * KilogramsPerJin);
            default:
                var kg = raw / 200m;
                return (kg, kg);
        }
    }
}
