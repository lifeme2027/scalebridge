using ScaleBridge.Core.Export;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests;

/// <summary>把 NOTES.md「解析策略与已知分歧」里的数据管线策略钉成测试（Q1/Q13/Q14/Q16/Q20）。</summary>
public class MeasurementPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(8));

    private static Measurement M(
        decimal? kg = 70m, int? raw = 14000, DateTime? ts = null, int? user = null,
        string device = "dev-1", MeasurementSource src = MeasurementSource.WeightScale,
        decimal? fat = null, DateTimeOffset? received = null) => new()
    {
        WeightKg = kg, RawWeight = raw, DeviceTimestamp = ts, UserId = user, DeviceId = device,
        Source = src, BodyFatPercent = fat, ReceivedAt = received ?? Now,
    };

    // ---- Q16 体重阈值 ----
    [Theory]
    [InlineData("0", WeightPlausibility.Zero)]
    [InlineData("0.005", WeightPlausibility.TooLight)]
    [InlineData("1.995", WeightPlausibility.TooLight)]
    [InlineData("2", WeightPlausibility.Normal)]      // 恰好 2.000 正常
    [InlineData("2.000", WeightPlausibility.Normal)]
    [InlineData("70", WeightPlausibility.Normal)]
    [InlineData("200", WeightPlausibility.Normal)]    // 恰好 200.000 正常
    [InlineData("200.000", WeightPlausibility.Normal)]
    [InlineData("200.005", WeightPlausibility.TooHeavy)]
    [InlineData("327.675", WeightPlausibility.TooHeavy)]
    public void Weight_plausibility_boundaries(string kg, WeightPlausibility expected)
        => Assert.Equal(expected, MeasurementPolicy.ClassifyWeight(decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Missing_weight_is_not_applicable_and_still_saved()
    {
        Assert.Equal(WeightPlausibility.NotApplicable, MeasurementPolicy.ClassifyWeight(null));
        Assert.True(MeasurementPolicy.ShouldAutoSave(M(kg: null, raw: null, fat: 21.5m)));
    }

    [Theory]
    [InlineData(WeightPlausibility.TooLight, true)]
    [InlineData(WeightPlausibility.TooHeavy, true)]
    [InlineData(WeightPlausibility.Normal, false)]
    [InlineData(WeightPlausibility.Zero, false)]
    [InlineData(WeightPlausibility.NotApplicable, false)]
    public void Only_too_light_and_too_heavy_are_abnormal(WeightPlausibility p, bool abnormal)
        => Assert.Equal(abnormal, MeasurementPolicy.IsAbnormal(p));

    [Fact]
    public void Zero_is_not_auto_saved_but_light_and_heavy_are()
    {
        Assert.False(MeasurementPolicy.ShouldAutoSave(M(kg: 0m, raw: 0)));
        Assert.True(MeasurementPolicy.ShouldAutoSave(M(kg: 1.5m)));
        Assert.True(MeasurementPolicy.ShouldAutoSave(M(kg: 250m)));
    }

    // ---- Q13 去重键 ----
    [Fact]
    public void Dedup_key_is_null_without_device_timestamp()
        => Assert.Null(MeasurementPolicy.DedupKey(M(ts: null)));

    [Fact]
    public void Dedup_key_ignores_received_time_but_not_payload_user_device_or_source()
    {
        var ts = new DateTime(2026, 10, 26, 7, 59, 30);
        var a = M(ts: ts, user: 1);
        Assert.Equal(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { ReceivedAt = Now.AddMinutes(5), Id = 9 }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { RawWeight = 14001, WeightKg = 70.005m }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { UserId = 2 }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { UserId = null }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { DeviceId = "dev-2" }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { Source = MeasurementSource.BodyComposition }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { DeviceTimestamp = ts.AddSeconds(1) }));
        Assert.NotEqual(MeasurementPolicy.DedupKey(a), MeasurementPolicy.DedupKey(a with { BodyFatPercent = 20m }));
    }

    [Fact]
    public void Dedup_key_ignores_sub_second_part_of_timestamp()
    {
        var ts = new DateTime(2026, 10, 26, 7, 59, 30);
        Assert.Equal(MeasurementPolicy.DedupKey(M(ts: ts)), MeasurementPolicy.DedupKey(M(ts: ts.AddMilliseconds(400))));
    }

    // ---- Q14 设备时钟 ----
    [Fact]
    public void Device_clock_suspect_boundaries()
    {
        Assert.False(MeasurementPolicy.IsDeviceClockSuspect(M(ts: null)));
        var now = Now.DateTime;
        Assert.False(MeasurementPolicy.IsDeviceClockSuspect(M(ts: now)));
        Assert.False(MeasurementPolicy.IsDeviceClockSuspect(M(ts: now.AddHours(24))));              // 恰好 24h：不算
        Assert.True(MeasurementPolicy.IsDeviceClockSuspect(M(ts: now.AddHours(24).AddSeconds(1))));
        Assert.False(MeasurementPolicy.IsDeviceClockSuspect(M(ts: now.AddDays(-366))));             // 恰好 366 天：不算
        Assert.True(MeasurementPolicy.IsDeviceClockSuspect(M(ts: now.AddDays(-366).AddSeconds(-1))));
        Assert.True(MeasurementPolicy.IsDeviceClockSuspect(M(ts: new DateTime(2000, 1, 1))));       // 复位到 2000 年
    }

    [Fact]
    public void Device_clock_is_compared_with_receive_wall_clock_not_converted_by_time_zone()
    {
        // 接收时刻 UTC+8 的墙钟是 08:00；设备时间 07:59（设备本地）→ 正常；不会因为机器时区不同而变化
        var m = M(ts: new DateTime(2026, 10, 26, 7, 59, 0), received: new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).AddHours(8));
        Assert.False(MeasurementPolicy.IsDeviceClockSuspect(m));
        // 设备时间原样保存，不做任何转换
        Assert.Equal(new DateTime(2026, 10, 26, 7, 59, 0), m.DeviceTimestamp);
    }

    // ---- Ingestor ----
    private sealed class MemoryRepo : IMeasurementRepository
    {
        private readonly List<Measurement> _items = new();
        private long _id;
        public Measurement Add(Measurement m) { var s = m with { Id = ++_id }; _items.Add(s); return s; }
        public IReadOnlyList<Measurement> List() => _items.OrderByDescending(x => x.DisplayTime).ToList();
        public int Delete(IEnumerable<long> ids) { var set = ids.ToHashSet(); return _items.RemoveAll(x => set.Contains(x.Id)); }
        public int Clear() { var n = _items.Count; _items.Clear(); return n; }
    }

    [Fact]
    public void Ingestor_saves_without_timestamp_every_time()
    {
        var repo = new MemoryRepo();
        var ing = new MeasurementIngestor(repo);
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(M()).Outcome);
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(M()).Outcome); // 相同读数、没有时间戳：两次真实称重
        Assert.Equal(2, repo.List().Count);
    }

    [Fact]
    public void Ingestor_drops_retransmission_with_same_timestamp_and_keeps_different_values()
    {
        var repo = new MemoryRepo();
        var ing = new MeasurementIngestor(repo);
        var ts = new DateTime(2026, 10, 26, 7, 59, 30);
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(M(ts: ts)).Outcome);
        Assert.Equal(IngestOutcome.Duplicate, ing.Ingest(M(ts: ts, received: Now.AddSeconds(3))).Outcome);
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(M(ts: ts, raw: 14010, kg: 70.05m)).Outcome); // 同时间戳不同数值：保留
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(M(ts: ts, user: 2)).Outcome);                  // 不同用户：保留
        Assert.Equal(3, repo.List().Count);
    }

    [Fact]
    public void Ingestor_dedups_across_restart_and_forgets_after_clear()
    {
        var repo = new MemoryRepo();
        var ts = new DateTime(2026, 10, 26, 7, 59, 30);
        new MeasurementIngestor(repo).Ingest(M(ts: ts));

        var restarted = new MeasurementIngestor(repo);        // 重启：从仓库已有记录重建
        Assert.Equal(IngestOutcome.Duplicate, restarted.Ingest(M(ts: ts)).Outcome);

        repo.Clear();
        restarted.Reload();
        Assert.Equal(IngestOutcome.Saved, restarted.Ingest(M(ts: ts)).Outcome);
    }

    [Fact]
    public void Ingestor_skips_zero_weight_flags_abnormal_and_reports_clock_suspect()
    {
        var repo = new MemoryRepo();
        var ing = new MeasurementIngestor(repo);

        var zero = ing.Ingest(M(kg: 0m, raw: 0));
        Assert.Equal(IngestOutcome.SkippedZeroWeight, zero.Outcome);
        Assert.Empty(repo.List());

        var light = ing.Ingest(M(kg: 1.5m, raw: 300));
        Assert.Equal(IngestOutcome.Saved, light.Outcome);
        Assert.True(light.IsAbnormal);

        var old = ing.Ingest(M(ts: new DateTime(2000, 1, 1)));
        Assert.True(old.DeviceClockSuspect);
        Assert.False(old.IsAbnormal);
    }

    // ---- Q1：英制换算与存储精确，只有显示舍入 ----
    [Fact]
    public void Imperial_weight_is_stored_and_exported_exactly_and_only_display_is_rounded()
    {
        // 150.09 lb = raw 15009
        var d = WeightScaleParser.ParseMeasurement(new byte[] { 0x01, 0xA1, 0x3A }).Value!;
        var m = MeasurementFactory.FromWeightScale(d, new DeviceContext("d", null, false), Now)!;
        Assert.Equal(68.0796788133m, m.WeightKg);                  // 150.09 × 0.45359237，精确到 10 位小数，无舍入
        Assert.Equal(150.09m, m.OriginalWeight);

        Assert.Contains("68.0796788133", CsvExporter.ToCsv(new[] { m }));
        Assert.Matches("\"weightKg\":\\s*68\\.0796788133,", JsonExporter.ToJson(new[] { m }, Now));

        Assert.Equal(68.080m, UnitConversion.ForDisplay(m.WeightKg!.Value, WeightUnit.Kg));
        Assert.Equal(150.09m, UnitConversion.ForDisplay(m.WeightKg.Value, WeightUnit.Lb));
    }

    [Fact]
    public void Display_rounding_is_away_from_zero_not_bankers()
    {
        Assert.Equal(0.125m, UnitConversion.ForDisplay(0.1245m, WeightUnit.Kg));  // 0.1245 → 0.125
        Assert.Equal(0.13m, UnitConversion.ForDisplay(0.125m * UnitConversion.KilogramsPerPound, WeightUnit.Lb));
    }
}
