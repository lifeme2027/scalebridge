using System.Text;
using System.Text.Json;
using ScaleBridge.Core.Export;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Tests;

public class ExportTests
{
    private static Measurement Sample(string? name = "Demo Scale", decimal? weight = 70.005m) => new()
    {
        Id = 1,
        ReceivedAt = new DateTimeOffset(2026, 10, 26, 8, 30, 15, TimeSpan.FromHours(8)),
        DeviceTimestamp = new DateTime(2026, 10, 26, 8, 30, 10),
        WeightKg = weight, OriginalWeight = weight, OriginalUnit = UnitSystem.Si, RawWeight = 14001,
        Bmi = 22.5m, HeightMeters = 1.75m, UserId = 2,
        DeviceId = "FA:KE:00:00:00:01", DeviceName = name, Source = MeasurementSource.WeightScale, IsDemo = true,
    };

    [Fact]
    public void Csv_starts_with_single_bom_and_uses_crlf_only()
    {
        var csv = CsvExporter.ToCsv(new[] { Sample() });
        Assert.Equal('\uFEFF', csv[0]);
        Assert.NotEqual('\uFEFF', csv[1]);
        Assert.DoesNotContain("\n", csv.Replace("\r\n", ""));
        Assert.EndsWith("\r\n", csv);
        Assert.Equal(3, csv.Split("\r\n").Length); // header, row, trailing empty
    }

    [Fact]
    public void Csv_file_has_exactly_one_utf8_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-{Guid.NewGuid():N}.csv");
        try
        {
            CsvExporter.WriteFile(path, new[] { Sample("设备") });
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Skip(3).Take(3).ToArray());
            Assert.Contains("设备", Encoding.UTF8.GetString(bytes));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Csv_header_and_row_have_same_column_count()
    {
        var lines = CsvExporter.ToCsv(new[] { Sample() }).TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CsvExporter.Header.Length, lines[0].Split(',').Length);
        Assert.Equal(CsvExporter.Header.Length, lines[1].Split(',').Length);
    }

    [Fact]
    public void Csv_numbers_use_invariant_culture_and_exact_decimals()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var csv = CsvExporter.ToCsv(new[] { Sample() });
            Assert.Contains(",70.005,70.005,kg,14001,22.5,1.75,", csv);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("'=x", "''=x")]
    [InlineData("Scale", "Scale")]
    [InlineData("it's", "it's")]
    [InlineData("", "")]
    public void GuardText_prefixes_apostrophe_only_for_formula_triggers(string input, string expected) =>
        Assert.Equal(expected, CsvExporter.GuardText(input));

    [Fact]
    public void Csv_guards_text_columns_but_not_numeric_columns()
    {
        var m = Sample("=HYPERLINK(\"x\")") with { WeightKg = -1m, DeviceId = "-id" };
        var csv = CsvExporter.ToCsv(new[] { m });
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains("'-id", csv);
        Assert.Contains(",-1,", csv); // 数值列不加前缀
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_newlines()
    {
        var csv = CsvExporter.ToCsv(new[] { Sample("a,\"b\"\nc") });
        Assert.Contains("\"a,\"\"b\"\"\nc\"", csv);
    }

    [Fact]
    public void Csv_leaves_missing_values_empty_and_sorts_by_time()
    {
        var older = Sample() with { Id = 2, DeviceTimestamp = new DateTime(2026, 1, 1), WeightKg = null, OriginalWeight = null, OriginalUnit = null, RawWeight = null, Bmi = null, HeightMeters = null, UserId = null };
        var rows = CsvExporter.ToCsv(new[] { Sample(), older }).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("2026-01-01T00:00:00,", rows[1]);
        Assert.Contains(",,,,,,,,,,,,,,", rows[1].Replace("2026-10-26T08:30:15+08:00", "T"));
    }

    [Fact]
    public void Csv_only_header_when_empty()
    {
        var csv = CsvExporter.ToCsv(Array.Empty<Measurement>());
        Assert.Equal("\uFEFF" + string.Join(",", CsvExporter.Header) + "\r\n", csv);
    }

    [Fact]
    public void Json_has_version_and_format_fields()
    {
        var json = JsonExporter.ToJson(new[] { Sample() }, new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(8)));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("scalebridge-export", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal("2026-10-26T09:00:00+08:00", root.GetProperty("exportedAt").GetString());
    }

    [Fact]
    public void Json_measurement_fields_are_exact_and_flagged()
    {
        var json = JsonExporter.ToJson(new[] { Sample() }, DateTimeOffset.UnixEpoch);
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement.GetProperty("measurements")[0];
        Assert.Equal(70.005m, m.GetProperty("weightKg").GetDecimal());
        Assert.Equal("kg", m.GetProperty("originalUnit").GetString());
        Assert.Equal(14001, m.GetProperty("rawWeight").GetInt32());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("bodyFatPercent").ValueKind);
        Assert.True(m.GetProperty("experimental").GetBoolean());
        Assert.True(m.GetProperty("demo").GetBoolean());
        Assert.Equal("weight-scale", m.GetProperty("source").GetString());
        Assert.Contains("70.005", json); // 没有 70.00499999 之类的浮点痕迹
    }

    [Fact]
    public void Json_empty_list_is_valid()
    {
        using var doc = JsonDocument.Parse(JsonExporter.ToJson(Array.Empty<Measurement>(), DateTimeOffset.UnixEpoch));
        Assert.Equal(0, doc.RootElement.GetProperty("measurements").GetArrayLength());
    }

    // ---- 「可能重复」标记（SB1 复测 N2）----

    private static string[] CsvRow(string csv, int row) =>
        csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[row].Split(',');

    [Fact]
    public void Csv_possible_duplicate_columns_are_appended_at_the_end_and_old_columns_keep_their_order()
    {
        var h = CsvExporter.Header;
        Assert.Equal(new[] { "record_id", "possible_duplicate", "possible_same_as" }, h.Skip(h.Length - 3).ToArray());
        Assert.Equal(new[] { "device_time", "received_time", "weight_kg" }, h.Take(3).ToArray());
        Assert.Equal(new[] { "source", "experimental", "demo" }, h.Skip(h.Length - 6).Take(3).ToArray()); // 旧的最后三列位置不变（只是后面多了三列）
        Assert.Equal(22 + 3, h.Length);
    }

    [Fact]
    public void Csv_marks_possible_duplicates_and_points_to_the_earlier_record()
    {
        var first = Sample() with { Id = 7 };
        var second = Sample() with { Id = 9, ReceivedAt = first.ReceivedAt.AddSeconds(1), DeviceTimestamp = null, Source = MeasurementSource.BodyComposition, PossibleSameAsId = 7 };
        var csv = CsvExporter.ToCsv(new[] { second, first });
        var i = Array.IndexOf(CsvExporter.Header, "possible_duplicate");
        var rows = new[] { CsvRow(csv, 1), CsvRow(csv, 2) };
        var a = rows.Single(r => r[Array.IndexOf(CsvExporter.Header, "record_id")] == "7");
        var b = rows.Single(r => r[Array.IndexOf(CsvExporter.Header, "record_id")] == "9");
        Assert.Equal("false", a[i]); Assert.Equal("", a[i + 1]);
        Assert.Equal("true", b[i]);  Assert.Equal("7", b[i + 1]);
    }

    [Fact]
    public void Csv_possible_same_as_is_blank_when_the_target_is_not_in_the_export_but_the_flag_stays_true()
    {
        var orphan = Sample() with { Id = 9, PossibleSameAsId = 7 };   // 7 已被删除
        var row = CsvRow(CsvExporter.ToCsv(new[] { orphan }), 1);
        var i = Array.IndexOf(CsvExporter.Header, "possible_duplicate");
        Assert.Equal("true", row[i]);
        Assert.Equal("", row[i + 1]);
        Assert.Equal(CsvExporter.Header.Length, row.Length);
    }

    [Fact]
    public void Json_has_possible_duplicate_fields_and_keeps_version_1()
    {
        var first = Sample() with { Id = 7 };
        var second = Sample() with { Id = 9, ReceivedAt = first.ReceivedAt.AddSeconds(1), Source = MeasurementSource.BodyComposition, PossibleSameAsId = 7 };
        var orphan = Sample() with { Id = 11, ReceivedAt = first.ReceivedAt.AddSeconds(2), PossibleSameAsId = 3 };
        using var doc = JsonDocument.Parse(JsonExporter.ToJson(new[] { first, second, orphan }, DateTimeOffset.UnixEpoch));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());   // 只追加字段：版本不变（见 JsonExporter 注释与 NOTES）
        var ms = doc.RootElement.GetProperty("measurements").EnumerateArray().ToDictionary(e => e.GetProperty("recordId").GetInt32());
        Assert.False(ms[7].GetProperty("possibleDuplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ms[7].GetProperty("possibleSameAs").ValueKind);
        Assert.True(ms[9].GetProperty("possibleDuplicate").GetBoolean());
        Assert.Equal(7, ms[9].GetProperty("possibleSameAs").GetInt32());
        Assert.True(ms[11].GetProperty("possibleDuplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ms[11].GetProperty("possibleSameAs").ValueKind);
    }

    [Fact]
    public void Possible_duplicate_flag_does_not_change_any_measured_value_in_exports()
    {
        var plain = Sample() with { Id = 5 };
        var marked = plain with { PossibleSameAsId = 4 };
        var a = CsvRow(CsvExporter.ToCsv(new[] { plain }), 1).Take(22);
        var b = CsvRow(CsvExporter.ToCsv(new[] { marked }), 1).Take(22);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Json_file_is_utf8_without_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-{Guid.NewGuid():N}.json");
        try
        {
            JsonExporter.WriteFile(path, new[] { Sample("设备") }, DateTimeOffset.UnixEpoch);
            var bytes = File.ReadAllBytes(path);
            Assert.NotEqual((byte)0xEF, bytes[0]);
            Assert.Contains("设备", Encoding.UTF8.GetString(bytes));
        }
        finally { File.Delete(path); }
    }
}
