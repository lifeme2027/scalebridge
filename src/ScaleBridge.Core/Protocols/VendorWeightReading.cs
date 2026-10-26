namespace ScaleBridge.Core.Protocols;

/// <summary>厂商体重帧的协议种类。</summary>
public enum VendorProtocol
{
    /// <summary>小米体脂秤一代 / Mi Scale，10 字节帧（服务 0x181D）。</summary>
    XiaomiMiScale,
    /// <summary>小米体脂秤 2 / Mi Body Composition Scale 2，13 字节帧（服务 0x181B）。</summary>
    XiaomiBodyCompositionScale2,
}

/// <summary>帧内原始体重的单位。</summary>
public enum VendorWeightUnit
{
    /// <summary>千克。Mi Scale：raw/200；体脂秤 2 相同。</summary>
    Kg,
    /// <summary>磅。raw/100。</summary>
    Lb,
    /// <summary>斤（500 g）。raw/100。</summary>
    Jin,
}

/// <summary>
/// 一帧厂商体重读数。
/// <see cref="IsExperimental"/>：实验性（仅按规范/公开资料实现，未真机验证）。
/// </summary>
public sealed record VendorWeightReading(
    VendorProtocol Protocol,
    int RawWeight,
    VendorWeightUnit RawUnit,
    decimal OriginalWeight,
    decimal WeightKg,
    DateTime? Timestamp,
    int? ImpedanceRaw,
    bool Stabilized,
    bool WeightRemoved)
{
    /// <summary>实验性（仅按规范/公开资料实现，未真机验证）。</summary>
    public const bool IsExperimental = true;

    /// <summary>稳定且未离秤，适合作为一条最终测量。</summary>
    public bool IsFinal => Stabilized && !WeightRemoved;
}
