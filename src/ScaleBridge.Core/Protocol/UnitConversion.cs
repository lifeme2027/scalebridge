using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Protocol;

/// <summary>
/// 单位换算（全部 decimal，精确）。
/// 国际 avoirdupois 磅 = 0.45359237 kg（定义值），英寸 = 0.0254 m（定义值）。
/// 两位小数的 lb × 八位小数的系数 = 十位小数，decimal 可精确表示，没有舍入误差。
/// </summary>
public static class UnitConversion
{
    public const decimal KilogramsPerPound = 0.45359237m;
    public const decimal MetersPerInch = 0.0254m;

    /// <summary>SI 质量分辨率 0.005 kg / Imperial 0.01 lb（Weight/Mass 字段的固定刻度，与 Feature 里的分辨率位无关）。</summary>
    public const decimal SiMassStep = 0.005m;
    public const decimal ImperialMassStep = 0.01m;
    /// <summary>身高：SI 0.001 m / Imperial 0.1 in。</summary>
    public const decimal SiHeightStep = 0.001m;
    public const decimal ImperialHeightStep = 0.1m;

    public static decimal PoundsToKilograms(decimal pounds) => pounds * KilogramsPerPound;
    public static decimal KilogramsToPounds(decimal kilograms) => kilograms / KilogramsPerPound;
    public static decimal InchesToMeters(decimal inches) => inches * MetersPerInch;
    public static decimal MetersToInches(decimal meters) => meters / MetersPerInch;

    public static MassValue DecodeMass(ushort raw, UnitSystem unit) => unit == UnitSystem.Si
        ? new MassValue(raw, raw * SiMassStep, raw * SiMassStep)
        : new MassValue(raw, raw * ImperialMassStep, PoundsToKilograms(raw * ImperialMassStep));

    public static LengthValue DecodeHeight(ushort raw, UnitSystem unit) => unit == UnitSystem.Si
        ? new LengthValue(raw, raw * SiHeightStep, raw * SiHeightStep)
        : new LengthValue(raw, raw * ImperialHeightStep, InchesToMeters(raw * ImperialHeightStep));

    /// <summary>界面显示：kg → 指定单位，四舍五入到合理小数位（kg 取 3 位、lb 取 2 位）。仅用于显示，不回写存储值。</summary>
    public static decimal ForDisplay(decimal kilograms, WeightUnit unit) => unit == WeightUnit.Kg
        ? Math.Round(kilograms, 3, MidpointRounding.AwayFromZero)
        : Math.Round(KilogramsToPounds(kilograms), 2, MidpointRounding.AwayFromZero);

    /// <summary>显示字符串（InvariantCulture），例如 "70.005" / "154.32"。</summary>
    public static string FormatWeight(decimal kilograms, WeightUnit unit)
    {
        var v = ForDisplay(kilograms, unit);
        var fmt = unit == WeightUnit.Kg ? "0.0##" : "0.00";
        return v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
    }
}
