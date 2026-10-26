using System.Globalization;
using System.Text;
using System.Text.Json;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Export;

/// <summary>
/// JSON 导出，带版本字段：<c>{"format":"scalebridge-export","version":1,...}</c>。
/// 版本约定：<c>version</c> 只在「破坏性变更」（删除/改名/改变既有字段含义）时递增；只追加字段不递增（读取方应忽略未知字段，
/// 桌面与 web 的导入都是这样做的；web 导入会拒绝比自己更新的 version）。<c>recordId</c> / <c>possibleDuplicate</c> / <c>possibleSameAs</c>
/// 是追加字段，所以 version 仍为 1。小数用 JSON number 精确写出（decimal，不经过 double）；没有值的字段写 null。
/// </summary>
public static class JsonExporter
{
    public const string FormatName = "scalebridge-export";
    public const int Version = 1;

    public static string ToJson(IEnumerable<Measurement> measurements, DateTimeOffset exportedAt)
    {
        var list = measurements.OrderBy(x => x.DisplayTime).ThenBy(x => x.Id).ToList();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatName);
            w.WriteNumber("version", Version);
            w.WriteString("exportedAt", exportedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            w.WriteString("note", "Experimental: parsed per Bluetooth SIG spec only, not verified on real hardware. Records with demo=true are demo data, not real weights.");
            w.WriteNumber("count", list.Count);
            w.WriteStartArray("measurements");
            var ids = PossibleSameAs.IdSet(list);
            foreach (var m in list) Write(w, m, ids);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()) + "\n";
    }

    public static void WriteFile(string path, IEnumerable<Measurement> measurements, DateTimeOffset exportedAt) =>
        File.WriteAllText(path, ToJson(measurements, exportedAt), new UTF8Encoding(false));

    private static void Write(Utf8JsonWriter w, Measurement m, HashSet<long> ids)
    {
        w.WriteStartObject();
        Str(w, "deviceTime", m.DeviceTimestamp?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        w.WriteString("receivedTime", m.ReceivedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        Dec(w, "weightKg", m.WeightKg);
        Dec(w, "weightOriginal", m.OriginalWeight);
        Str(w, "originalUnit", m.OriginalUnit switch { UnitSystem.Si => "kg", UnitSystem.Imperial => "lb", _ => null });
        Int(w, "rawWeight", m.RawWeight);
        Dec(w, "bmi", m.Bmi);
        Dec(w, "heightM", m.HeightMeters);
        Dec(w, "bodyFatPercent", m.BodyFatPercent);
        Dec(w, "musclePercent", m.MusclePercent);
        Dec(w, "muscleMassKg", m.MuscleMassKg);
        Dec(w, "fatFreeMassKg", m.FatFreeMassKg);
        Dec(w, "softLeanMassKg", m.SoftLeanMassKg);
        Dec(w, "bodyWaterMassKg", m.BodyWaterMassKg);
        Int(w, "basalMetabolismKj", m.BasalMetabolismKj);
        Dec(w, "impedanceOhm", m.ImpedanceOhm);
        Int(w, "userId", m.UserId);
        w.WriteString("deviceId", m.DeviceId);
        Str(w, "deviceName", m.DeviceName);
        w.WriteString("source", CsvExporter.SourceName(m.Source));
        w.WriteBoolean("experimental", m.IsExperimental);
        w.WriteBoolean("demo", m.IsDemo);
        w.WriteNumber("recordId", m.Id);
        w.WriteBoolean("possibleDuplicate", m.PossibleSameAsId is not null);
        var target = PossibleSameAs.Target(m, ids);
        if (target is null) w.WriteNull("possibleSameAs"); else w.WriteNumber("possibleSameAs", target.Value);
        w.WriteEndObject();
    }

    private static void Str(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }

    private static void Dec(Utf8JsonWriter w, string name, decimal? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteNumber(name, v.Value);
    }

    private static void Int(Utf8JsonWriter w, string name, int? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteNumber(name, v.Value);
    }
}
