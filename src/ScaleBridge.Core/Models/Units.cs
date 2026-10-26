namespace ScaleBridge.Core.Models;

/// <summary>协议里 Flags 第 0 位：测量单位制。</summary>
public enum UnitSystem
{
    /// <summary>SI：体重/质量 kg，身高 m。</summary>
    Si = 0,
    /// <summary>Imperial：体重/质量 lb，身高 in。</summary>
    Imperial = 1,
}

/// <summary>界面显示用的体重单位。</summary>
public enum WeightUnit
{
    Kg = 0,
    Lb = 1,
}

/// <summary>
/// 质量/体重值：保留设备发来的原始整数、按设备单位换算后的值（kg 或 lb），以及统一换算成 kg 的精确 decimal。
/// 全程用 decimal，不用 double 累积。
/// </summary>
public sealed record MassValue(ushort Raw, decimal Original, decimal Kilograms);

/// <summary>身高值：原始整数、设备单位值（m 或 in）、统一换算成米的精确 decimal。</summary>
public sealed record LengthValue(ushort Raw, decimal Original, decimal Meters);
