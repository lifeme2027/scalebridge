namespace ScaleBridge.Core.Protocol;

/// <summary>
/// 本项目用到的标准 GATT UUID（16 位短 UUID 及其 128 位展开）。
/// 16 位短 UUID 展开规则：0000XXXX-0000-1000-8000-00805F9B34FB（Bluetooth 基准 UUID）。
/// </summary>
public static class GattUuids
{
    public const ushort WeightScaleServiceShort = 0x181D;
    public const ushort BodyCompositionServiceShort = 0x181B;
    public const ushort WeightScaleFeatureShort = 0x2A9E;
    public const ushort WeightMeasurementShort = 0x2A9D;
    public const ushort BodyCompositionFeatureShort = 0x2A9B;
    public const ushort BodyCompositionMeasurementShort = 0x2A9C;

    public static readonly Guid WeightScaleService = FromShort(WeightScaleServiceShort);
    public static readonly Guid BodyCompositionService = FromShort(BodyCompositionServiceShort);
    public static readonly Guid WeightScaleFeature = FromShort(WeightScaleFeatureShort);
    public static readonly Guid WeightMeasurement = FromShort(WeightMeasurementShort);
    public static readonly Guid BodyCompositionFeature = FromShort(BodyCompositionFeatureShort);
    public static readonly Guid BodyCompositionMeasurement = FromShort(BodyCompositionMeasurementShort);

    /// <summary>16 位短 UUID → 128 位 UUID。</summary>
    public static Guid FromShort(ushort shortUuid) =>
        new($"0000{shortUuid:x4}-0000-1000-8000-00805f9b34fb");

    /// <summary>若是 Bluetooth 基准 UUID 展开出来的值，返回其 16 位短 UUID；否则 null。</summary>
    public static ushort? TryGetShort(Guid uuid)
    {
        var s = uuid.ToString("D");
        if (!s.StartsWith("0000", StringComparison.Ordinal) ||
            !s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal))
            return null;
        return ushort.TryParse(s.AsSpan(4, 4), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>解析 "181D"、"0x181D"、完整 128 位 UUID 字符串。</summary>
    public static bool TryParse(string? text, out Guid uuid)
    {
        uuid = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (text.Length == 4 && ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var sh))
        {
            uuid = FromShort(sh);
            return true;
        }
        return Guid.TryParse(text, out uuid);
    }
}
