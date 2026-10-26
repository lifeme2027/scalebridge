namespace ScaleBridge.Core.Protocol;

/// <summary>
/// Feature 特征里「分辨率」位段的码表（Weight Measurement / Mass Measurement Resolution 4 位，Height Measurement Resolution 3 位）。
/// 注意（WSS/BCS §3.2.1.1）：这些位只用于让客户端知道设备的大致精度，<b>不改变</b>测量字段里 1 个 LSB 的含义。
/// </summary>
public static class FeatureResolutions
{
    // 码 → (SI kg, Imperial lb)
    private static readonly (decimal Kg, decimal Lb)[] Mass =
    {
        (0m, 0m),            // 0: Not Specified（占位）
        (0.5m, 1m), (0.2m, 0.5m), (0.1m, 0.2m), (0.05m, 0.1m), (0.02m, 0.05m), (0.01m, 0.02m), (0.005m, 0.01m),
    };

    // 码 → (SI m, Imperial in)
    private static readonly (decimal M, decimal In)[] Height =
    {
        (0m, 0m),
        (0.01m, 1m), (0.005m, 0.5m), (0.001m, 0.1m),
    };

    /// <summary>码 1–7 返回分辨率；0（未指定）与保留值（8–15）返回 null。</summary>
    public static decimal? MassKg(int code) => code is >= 1 and <= 7 ? Mass[code].Kg : null;
    public static decimal? MassLb(int code) => code is >= 1 and <= 7 ? Mass[code].Lb : null;
    public static decimal? HeightMeters(int code) => code is >= 1 and <= 3 ? Height[code].M : null;
    public static decimal? HeightInches(int code) => code is >= 1 and <= 3 ? Height[code].In : null;

    public static bool IsReservedMass(int code) => code >= 8;
    public static bool IsReservedHeight(int code) => code >= 4;
}
