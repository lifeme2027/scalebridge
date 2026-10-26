using System.Globalization;
using System.Text;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Export;

/// <summary>
/// CSV 导出。格式：UTF-8 带 BOM、CRLF 换行（Excel/WPS 可直接打开）；文本列做公式注入防护。
/// 数值一律用 InvariantCulture 的 decimal 字符串（点号小数、无千分位）。
/// </summary>
public static class CsvExporter
{
    public static readonly string[] Header =
    {
        "device_time", "received_time", "weight_kg", "weight_original", "original_unit", "raw_weight",
        "bmi", "height_m", "body_fat_percent", "muscle_percent", "muscle_mass_kg", "fat_free_mass_kg",
        "soft_lean_mass_kg", "body_water_mass_kg", "basal_metabolism_kj", "impedance_ohm", "user_id",
        "device_id", "device_name", "source", "experimental", "demo",
    };

    private static bool IsTrigger(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>
    /// 防公式注入（仅文本列）：若字符串以若干个 ' 之后紧跟 = + - @ 制表符 回车 开头，整体前加一个 '。
    /// 数值列绝不调用（负数不应被改写）。
    /// </summary>
    public static string GuardText(string value)
    {
        var i = 0;
        while (i < value.Length && value[i] == '\'') i++;
        return i < value.Length && IsTrigger(value[i]) ? "'" + value : value;
    }

    public static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>返回带 BOM 的完整 CSV 文本（首字符是 U+FEFF）。</summary>
    public static string ToCsv(IEnumerable<Measurement> measurements)
    {
        var sb = new StringBuilder("\uFEFF");
        sb.Append(string.Join(",", Header)).Append("\r\n");
        foreach (var m in measurements.OrderBy(x => x.DisplayTime).ThenBy(x => x.Id))
        {
            string[] cols =
            {
                m.DeviceTimestamp?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                m.ReceivedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                Num(m.WeightKg), Num(m.OriginalWeight),
                m.OriginalUnit switch { UnitSystem.Si => "kg", UnitSystem.Imperial => "lb", _ => "" },
                Num(m.RawWeight),
                Num(m.Bmi), Num(m.HeightMeters), Num(m.BodyFatPercent), Num(m.MusclePercent), Num(m.MuscleMassKg),
                Num(m.FatFreeMassKg), Num(m.SoftLeanMassKg), Num(m.BodyWaterMassKg), Num(m.BasalMetabolismKj),
                Num(m.ImpedanceOhm), Num(m.UserId),
                GuardText(m.DeviceId), GuardText(m.DeviceName ?? ""),
                SourceName(m.Source),
                m.IsExperimental ? "true" : "false",
                m.IsDemo ? "true" : "false",
            };
            sb.Append(string.Join(",", cols.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>写文件：UTF-8（ToCsv 已含 BOM，所以不再让编码器加第二个 BOM）。</summary>
    public static void WriteFile(string path, IEnumerable<Measurement> measurements) =>
        File.WriteAllText(path, ToCsv(measurements), new UTF8Encoding(false));

    internal static string SourceName(MeasurementSource s) => s switch
    {
        MeasurementSource.WeightScale => "weight-scale",
        MeasurementSource.BodyComposition => "body-composition",
        _ => "unknown",
    };

    private static string Num(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
}
