using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Protocols;

/// <summary>
/// Chipsea / OKOK 厂商广播（manufacturer data，从 0xCA 起）解析器。
/// 实验性（仅按规范/公开资料实现，未真机验证）。字节布局见 NOTES.md「厂商协议来源（时尚1）」。
/// v1.1 校验和未定论：厂商文档为版本字节起到数据域末尾的 XOR，一份公开抓包则从 0xCA 起 XOR，两者都接受。
/// v2.0 帧不携带单位，由调用方用 <c>assumedUnit</c> 说明；缺省按千克并报告 <see cref="VendorParseWarningCode.UnitAssumed"/>。
/// 公开资料称实机的锁定字节即使重量已稳定也可能保持 0；仅字节值 0x01 视为稳定。
/// 未实现：C0 变体、连接后 0x12/0x13 通知帧、体成分。任何输入都不会抛异常。
/// </summary>
public static class ChipseaOkokParser
{
    /// <summary>v1.1 广播帧固定长度（0xCA 到校验和，含）。</summary>
    public const int V11FrameSize = 19;
    /// <summary>v2.0 广播帧固定长度（0xCA 到校验和，含）。</summary>
    public const int V20FrameSize = 15;

    private const byte Magic = 0xCA;
    private const byte Version11 = 0x11;
    private const byte Version20 = 0x20;
    private const byte DomainLength11 = 0x0F;
    private const byte DomainLength20 = 0x0B;

    /// <summary>1 斤 = 500 g = 0.5 kg。</summary>
    private const decimal KilogramsPerJin = 0.5m;
    /// <summary>1 英石 = 14 磅。</summary>
    private const decimal PoundsPerStone = 14m;

    /// <summary>
    /// 解析 v1.1 广播帧（版本 0x11，数据域长度 0x0F，19 字节）。
    /// 校验和之后的字节（实机常见 6 字节设备地址）静默忽略。
    /// </summary>
    public static VendorParseResult<VendorWeightReading> ParseV11Broadcast(ReadOnlySpan<byte> data)
    {
        if (data.Length < V11FrameSize)
            return Fail(VendorParseErrorCode.TooShort, $"Chipsea v1.1 frame needs {V11FrameSize} bytes, got {data.Length}.");

        if (data[0] != Magic || data[1] != Version11 || data[2] != DomainLength11)
            return Fail(VendorParseErrorCode.BadHeader, "Chipsea v1.1 header (0xCA, version 0x11, length 0x0F) does not match.");

        // 厂商文档：XOR(idx1..17)；一份公开抓包：XOR(idx0..17)。未定论，两者都接受。
        var sumFromVersion = Xor(data, 1, V11FrameSize - 1);
        var sumFromMagic = Xor(data, 0, V11FrameSize - 1);
        if (data[18] != sumFromVersion && data[18] != sumFromMagic)
            return Fail(VendorParseErrorCode.BadChecksum, "Chipsea v1.1 checksum matches neither accepted XOR rule.");

        var raw = (data[5] << 8) | data[6];
        var props = data[11];
        var unitBits = (props >> 3) & 0x3;
        var decimalBits = (props >> 1) & 0x3;
        var unit = UnitFromBits(unitBits);

        decimal original;
        if (unit == VendorWeightUnit.StoneLb)
        {
            // 小数位对 st:lb 无意义。高字节 = 英石，低字节 = 磅×10。OriginalWeight 为总磅数。
            original = data[5] * PoundsPerStone + data[6] / 10m;
        }
        else
        {
            if (!TryDivisor(decimalBits, out var divisor))
                return Fail(VendorParseErrorCode.InvalidValue, "Chipsea v1.1 decimal bits 11 are not defined.");
            original = raw / divisor;
        }

        var kg = ToKilograms(original, unit);
        return VendorParseResult<VendorWeightReading>.Ok(new VendorWeightReading(
            VendorProtocol.ChipseaOkok11, raw, unit, original, kg,
            Timestamp: null, ImpedanceRaw: null,
            Stabilized: data[3] == 0x01,
            WeightRemoved: false));
    }

    /// <summary>
    /// 解析 v2.0 广播帧（版本 0x20，数据域长度 0x0B，15 字节）。
    /// 体重为 raw/10，单位不在帧内。<paramref name="assumedUnit"/> 为 null 时假定千克并告警；
    /// <see cref="VendorWeightUnit.StoneLb"/> 对该帧未定义，返回 <see cref="VendorParseErrorCode.InvalidValue"/>。
    /// 校验和之后的字节静默忽略。
    /// </summary>
    public static VendorParseResult<VendorWeightReading> ParseV20Broadcast(ReadOnlySpan<byte> data, VendorWeightUnit? assumedUnit = null)
    {
        if (data.Length < V20FrameSize)
            return Fail(VendorParseErrorCode.TooShort, $"Chipsea v2.0 frame needs {V20FrameSize} bytes, got {data.Length}.");

        if (data[0] != Magic || data[1] != Version20 || data[2] != DomainLength20)
            return Fail(VendorParseErrorCode.BadHeader, "Chipsea v2.0 header (0xCA, version 0x20, length 0x0B) does not match.");

        // 校验和 = XOR(idx1..13)，不含起始字节。
        if (data[14] != Xor(data, 1, V20FrameSize - 1))
            return Fail(VendorParseErrorCode.BadChecksum, "Chipsea v2.0 checksum does not match XOR of bytes 1..13.");

        if (assumedUnit == VendorWeightUnit.StoneLb)
            return Fail(VendorParseErrorCode.InvalidValue, "Stone:pound is not defined for the Chipsea v2.0 broadcast frame.");

        var unit = assumedUnit ?? VendorWeightUnit.Kg;
        var raw = (data[10] << 8) | data[11];
        var original = raw / 10m;
        var kg = ToKilograms(original, unit);
        var impedance = (data[12] << 8) | data[13];
        IReadOnlyList<VendorParseWarningCode>? warnings = assumedUnit is null
            ? new[] { VendorParseWarningCode.UnitAssumed }
            : null;

        return VendorParseResult<VendorWeightReading>.Ok(new VendorWeightReading(
            VendorProtocol.ChipseaOkok20, raw, unit, original, kg,
            Timestamp: null, ImpedanceRaw: impedance,
            Stabilized: data[8] == 0x01,
            WeightRemoved: false), warnings);
    }

    /// <summary>
    /// 按版本字节分发：长度不足 2 为 <see cref="VendorParseErrorCode.TooShort"/>；
    /// 非 0xCA 或未知版本为 <see cref="VendorParseErrorCode.BadHeader"/>；
    /// 0x11 → v1.1，0x20 → v2.0（<paramref name="assumedUnit"/> 只作用于 v2.0）。
    /// </summary>
    public static VendorParseResult<VendorWeightReading> ParseManufacturerData(ReadOnlySpan<byte> data, VendorWeightUnit? assumedUnit = null)
    {
        if (data.Length < 2)
            return Fail(VendorParseErrorCode.TooShort, $"Chipsea manufacturer data needs at least 2 bytes, got {data.Length}.");
        if (data[0] != Magic)
            return Fail(VendorParseErrorCode.BadHeader, "Chipsea manufacturer data does not start with 0xCA.");

        return data[1] switch
        {
            Version11 => ParseV11Broadcast(data),
            Version20 => ParseV20Broadcast(data, assumedUnit),
            _ => Fail(VendorParseErrorCode.BadHeader, $"Chipsea version 0x{data[1]:X2} is not a supported broadcast frame."),
        };
    }

    private static VendorWeightUnit UnitFromBits(int unitBits) => unitBits switch
    {
        1 => VendorWeightUnit.Jin,
        2 => VendorWeightUnit.Lb,
        3 => VendorWeightUnit.StoneLb,
        _ => VendorWeightUnit.Kg,
    };

    /// <summary>00 = 1 位小数，01 = 0 位，10 = 2 位，11 未定义。</summary>
    private static bool TryDivisor(int decimalBits, out decimal divisor)
    {
        switch (decimalBits)
        {
            case 0:
                divisor = 10m;
                return true;
            case 1:
                divisor = 1m;
                return true;
            case 2:
                divisor = 100m;
                return true;
            default:
                divisor = 0m;
                return false;
        }
    }

    private static decimal ToKilograms(decimal original, VendorWeightUnit unit) => unit switch
    {
        VendorWeightUnit.Lb or VendorWeightUnit.StoneLb => UnitConversion.PoundsToKilograms(original),
        VendorWeightUnit.Jin => original * KilogramsPerJin,
        _ => original,
    };

    private static byte Xor(ReadOnlySpan<byte> data, int start, int endExclusive)
    {
        byte sum = 0;
        for (var i = start; i < endExclusive; i++)
            sum ^= data[i];
        return sum;
    }

    private static VendorParseResult<VendorWeightReading> Fail(VendorParseErrorCode code, string message) =>
        VendorParseResult<VendorWeightReading>.Fail(code, message);
}
