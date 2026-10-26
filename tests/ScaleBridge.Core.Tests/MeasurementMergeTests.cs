using ScaleBridge.Core.Export;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Tests;

/// <summary>SB1-M02 / Q17：同一次称重从 0x181D(WSS) 与 0x181B(BCS) 各到一包的保守合并（实验性规则）。拿不准一律保留两条并标注。</summary>
public class MeasurementMergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTime Stamp = new(2026, 10, 26, 8, 0, 0);

    private static Measurement Wss(decimal? kg = 70m, DateTime? ts = null, int? user = null, double recvSec = 0, string device = "dev-1", decimal? bmi = null) => new()
    {
        Source = MeasurementSource.WeightScale, DeviceId = device, DeviceTimestamp = ts, UserId = user,
        WeightKg = kg, OriginalWeight = kg, OriginalUnit = kg is null ? null : UnitSystem.Si, RawWeight = kg is null ? null : (int)(kg * 200),
        Bmi = bmi, ReceivedAt = T0.AddSeconds(recvSec),
    };

    private static Measurement Bcs(decimal? kg = 70m, DateTime? ts = null, int? user = null, double recvSec = 0, string device = "dev-1", decimal? bmi = null) => new()
    {
        Source = MeasurementSource.BodyComposition, DeviceId = device, DeviceTimestamp = ts, UserId = user,
        WeightKg = kg, OriginalWeight = kg, OriginalUnit = kg is null ? null : UnitSystem.Si, RawWeight = kg is null ? null : (int)(kg * 200),
        BodyFatPercent = 21.5m, MuscleMassKg = 30m, ImpedanceOhm = 500m, Bmi = bmi, ReceivedAt = T0.AddSeconds(recvSec),
    };

    private static (MeasurementIngestor Ing, MemoryRepo Repo) New() { var r = new MemoryRepo(); return (new MeasurementIngestor(r), r); }

    // ---- 合并 ----

    [Fact]
    public void Wss_then_Bcs_with_same_timestamp_is_one_row_with_both_sources_and_complemented_fields()
    {
        var (ing, repo) = New();
        var a = ing.Ingest(Wss(ts: Stamp));
        var b = ing.Ingest(Bcs(ts: Stamp, recvSec: 0.2));
        Assert.Equal(IngestOutcome.Saved, a.Outcome);
        Assert.Equal(IngestOutcome.Merged, b.Outcome);
        var row = Assert.Single(repo.List());
        Assert.Equal(a.Measurement.Id, row.Id);                 // 保留原主键
        Assert.Equal(MeasurementSource.Both, row.Source);
        Assert.Equal(70m, row.WeightKg);                          // 体重只算一次
        Assert.Equal(21.5m, row.BodyFatPercent);                  // 体成分字段补进来
        Assert.Equal(500m, row.ImpedanceOhm);
        Assert.Null(row.PossibleSameAsId);
        Assert.Equal(row, b.Measurement);
    }

    [Fact]
    public void Bcs_before_Wss_merges_the_same_way()
    {
        var (ing, repo) = New();
        ing.Ingest(Bcs(ts: Stamp));
        var b = ing.Ingest(Wss(ts: Stamp, recvSec: 0.2));
        Assert.Equal(IngestOutcome.Merged, b.Outcome);
        var row = Assert.Single(repo.List());
        Assert.Equal(MeasurementSource.Both, row.Source);
        Assert.Equal(70m, row.WeightKg);
        Assert.Equal(21.5m, row.BodyFatPercent);
        Assert.Equal(T0, row.ReceivedAt);                          // 取较早的接收时间
    }

    [Fact]
    public void Without_timestamp_equal_weight_within_window_merges_and_beyond_window_does_not()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss());
        Assert.Equal(IngestOutcome.Merged, ing.Ingest(Bcs(recvSec: 1.5)).Outcome);
        Assert.Single(repo.List());

        var (ing2, repo2) = New();
        ing2.Ingest(Wss());
        var late = ing2.Ingest(Bcs(recvSec: 5));
        Assert.Equal(IngestOutcome.Saved, late.Outcome);
        Assert.Equal(2, repo2.Count());
        Assert.Null(late.Measurement.PossibleSameAsId);            // 隔了 5 秒又没时间戳：看不出关系，不标注
    }

    [Fact]
    public void Missing_weight_on_one_side_merges_only_when_timestamps_prove_it()
    {
        // 有相同时间戳：BCS 没带体重 → 补上 WSS 的体重
        var (ing, repo) = New();
        ing.Ingest(Bcs(kg: null, ts: Stamp));
        Assert.Equal(IngestOutcome.Merged, ing.Ingest(Wss(ts: Stamp)).Outcome);
        Assert.Equal(70m, Assert.Single(repo.List()).WeightKg);

        // 没有时间戳：只靠接收时间不够 → 两条并标注
        var (ing2, repo2) = New();
        ing2.Ingest(Bcs(kg: null));
        var b = ing2.Ingest(Wss(recvSec: 0.1));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo2.Count());
        Assert.NotNull(b.Measurement.PossibleSameAsId);
    }

    // ---- 保守：不合并 ----

    [Fact]
    public void Different_users_are_never_merged_nor_marked()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp, user: 1));
        var b = ing.Ingest(Bcs(ts: Stamp, user: 2));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Null(b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void One_side_without_user_is_kept_as_two_rows_and_marked()
    {
        var (ing, repo) = New();
        var a = ing.Ingest(Wss(ts: Stamp, user: 1));
        var b = ing.Ingest(Bcs(ts: Stamp, user: null));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Equal(a.Measurement.Id, b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void Same_timestamp_but_different_weights_is_kept_as_two_rows_and_marked()
    {
        var (ing, repo) = New();
        var a = ing.Ingest(Wss(kg: 70m, ts: Stamp));
        var b = ing.Ingest(Bcs(kg: 70.5m, ts: Stamp));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Equal(a.Measurement.Id, b.Measurement.PossibleSameAsId);
        Assert.All(repo.List(), r => Assert.NotEqual(MeasurementSource.Both, r.Source));   // 体重没有被改动或平均
    }

    [Fact]
    public void Without_timestamp_different_weights_are_two_consecutive_weigh_ins_and_stay_unmarked()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(kg: 70m));
        var b = ing.Ingest(Bcs(kg: 70.5m, recvSec: 0.3));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Null(b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void Different_devices_are_never_merged()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp, device: "dev-1"));
        var b = ing.Ingest(Bcs(ts: Stamp, device: "dev-2"));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Null(b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void Conflicting_bmi_blocks_the_merge()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp, bmi: 22.0m));
        var b = ing.Ingest(Bcs(ts: Stamp, bmi: 23.0m));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.NotNull(b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void Two_packets_from_the_same_service_are_two_weigh_ins_even_with_equal_values()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss());
        var b = ing.Ingest(Wss(recvSec: 1));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Null(b.Measurement.PossibleSameAsId);
    }

    [Fact]
    public void A_merged_row_is_not_merged_again()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp));
        ing.Ingest(Bcs(ts: Stamp));
        var c = ing.Ingest(Wss(kg: 70m, ts: Stamp.AddSeconds(1), recvSec: 0.5)); // 不同秒、不同去重键
        Assert.Equal(IngestOutcome.Saved, c.Outcome);
        Assert.Equal(2, repo.Count());
    }

    [Fact]
    public void Ambiguous_candidates_are_never_merged_and_the_new_row_is_marked()
    {
        var (ing, repo) = New();
        // 两条 WSS 同秒但 RawWeight 不同 → 两条独立记录（Q13 键不同）
        var w1 = ing.Ingest(Wss(kg: 70m, ts: Stamp));
        var w2 = ing.Ingest(Wss(kg: 70.5m, ts: Stamp, recvSec: 0.1));
        Assert.Equal(IngestOutcome.Saved, w2.Outcome);
        // BCS 同秒、体重与 w1 相同：对 w1 是合并候选，对 w2 是「像」→ 有歧义，不合并
        var b = ing.Ingest(Bcs(kg: 70m, ts: Stamp, recvSec: 0.2));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(3, repo.Count());
        Assert.NotNull(b.Measurement.PossibleSameAsId);
    }

    // ---- SB1 复测 N1：时间戳明确不同 → 不标；相同或缺失 → 才可能标 ----

    [Theory]
    [InlineData(600)]      // 相差 10 分钟
    [InlineData(86400)]    // 相差 1 天（如设备历史回放）
    [InlineData(1)]        // 仅差 1 秒也算明确不同
    public void Explicitly_different_timestamps_with_equal_weight_and_close_receive_times_are_neither_merged_nor_marked(int diffSeconds)
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp));
        var b = ing.Ingest(Bcs(ts: Stamp.AddSeconds(diffSeconds), recvSec: 0.2));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo.Count());
        Assert.Null(b.Measurement.PossibleSameAsId);
        Assert.All(repo.List(), r => Assert.NotEqual(MeasurementSource.Both, r.Source));
    }

    [Fact]
    public void Explicitly_different_timestamps_are_unrelated_in_both_arrival_orders_and_for_missing_or_different_weights()
    {
        var older = Stamp.AddDays(-1);
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Bcs(ts: Stamp), Wss(ts: older, recvSec: 0.1)));
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(ts: Stamp), Bcs(ts: older, recvSec: 0.1)));
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(ts: Stamp), Bcs(kg: null, ts: older, recvSec: 0.1)));
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(kg: 70m, ts: Stamp), Bcs(kg: 70.5m, ts: older, recvSec: 0.1)));
        // 一边缺用户也不改变：时间戳已证明是两次
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(ts: Stamp, user: 1), Bcs(ts: older, user: null, recvSec: 0.1)));
        // BMI 冲突 + 时间戳不同：仍不标
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(ts: Stamp, bmi: 22m), Bcs(ts: older, bmi: 23m, recvSec: 0.1)));
    }

    [Fact]
    public void Equal_or_missing_timestamps_are_still_marked_when_uncertain()
    {
        // 相同时间戳 + 体重不同 → 标
        Assert.Equal(MergeDecision.PossibleSame, MeasurementMerge.Evaluate(Wss(kg: 70m, ts: Stamp), Bcs(kg: 70.5m, ts: Stamp, recvSec: 5)));
        // 一边有时间戳、一边没有，体重相同、接收时间相近 → 标
        Assert.Equal(MergeDecision.PossibleSame, MeasurementMerge.Evaluate(Wss(ts: Stamp), Bcs(ts: null, recvSec: 0.3)));
        Assert.Equal(MergeDecision.PossibleSame, MeasurementMerge.Evaluate(Wss(ts: null), Bcs(ts: Stamp, recvSec: 0.3)));
        // 两边都没有时间戳、一边缺体重、接收时间相近 → 标
        Assert.Equal(MergeDecision.PossibleSame, MeasurementMerge.Evaluate(Wss(), Bcs(kg: null, recvSec: 0.3)));
        // 一边有时间戳一边没有，但接收时间相隔很久 → 不相关
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(ts: Stamp), Bcs(ts: null, recvSec: 60)));
    }

    [Fact]
    public void Merge_rules_are_unchanged_for_equal_timestamps_and_for_two_missing_timestamps()
    {
        Assert.Equal(MergeDecision.Merge, MeasurementMerge.Evaluate(Wss(ts: Stamp), Bcs(ts: Stamp, recvSec: 30)));   // 相同时间戳：与接收时间无关
        Assert.Equal(MergeDecision.Merge, MeasurementMerge.Evaluate(Wss(), Bcs(recvSec: 1.9)));
        Assert.Equal(MergeDecision.Unrelated, MeasurementMerge.Evaluate(Wss(), Bcs(recvSec: 2.5)));
    }

    // ---- 乱序 / 设备时钟 ----

    [Fact]
    public void Out_of_order_arrival_still_pairs_by_device_timestamp_not_by_last_row()
    {
        var (ing, repo) = New();
        var t1 = Stamp; var t2 = Stamp.AddMinutes(5);
        ing.Ingest(Wss(kg: 70m, ts: t1));
        ing.Ingest(Wss(kg: 71m, ts: t2, recvSec: 1));     // 在 t1 的 BCS 到来之前，又来了一次更晚的 WSS
        var b = ing.Ingest(Bcs(kg: 70m, ts: t1, recvSec: 2));   // t1 的 BCS 迟到、乱序
        Assert.Equal(IngestOutcome.Merged, b.Outcome);
        Assert.Equal(2, repo.Count());
        var merged = repo.List().Single(r => r.Source == MeasurementSource.Both);
        Assert.Equal(70m, merged.WeightKg);
        Assert.Equal(t1, merged.DeviceTimestamp);
    }

    [Fact]
    public void Out_of_order_without_timestamp_pairs_only_within_the_window()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(kg: 70m, recvSec: 0));
        ing.Ingest(Wss(kg: 71m, recvSec: 10));
        var b = ing.Ingest(Bcs(kg: 71m, recvSec: 10.5));
        Assert.Equal(IngestOutcome.Merged, b.Outcome);   // 与最近的 71 配对
        Assert.Equal(2, repo.Count());
        Assert.Equal(71m, repo.List().Single(r => r.Source == MeasurementSource.Both).WeightKg);
    }

    [Fact]
    public void Suspect_device_clock_needs_close_receive_times_as_well()
    {
        var stuck = new DateTime(2000, 1, 1, 0, 0, 0);   // 时钟卡在 2000 年（设备时钟可疑）
        // 接收时间相近 + 时间戳相同 + 体重相同 → 仍可合并
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: stuck));
        Assert.Equal(IngestOutcome.Merged, ing.Ingest(Bcs(ts: stuck, recvSec: 0.3)).Outcome);
        Assert.Single(repo.List());

        // 接收时间相隔很久：卡住的时钟上「时间戳相同」不能证明是同一次 → 保留两条并标注
        var (ing2, repo2) = New();
        ing2.Ingest(Wss(ts: stuck));
        var b = ing2.Ingest(Bcs(ts: stuck, recvSec: 3600));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(2, repo2.Count());
        Assert.NotNull(b.Measurement.PossibleSameAsId);

        // 时钟可疑 + 一边缺体重：不允许靠时间戳补体重
        var (ing3, repo3) = New();
        ing3.Ingest(Bcs(kg: null, ts: stuck));
        var c = ing3.Ingest(Wss(ts: stuck, recvSec: 0.1));
        Assert.Equal(IngestOutcome.Saved, c.Outcome);
        Assert.Equal(2, repo3.Count());
    }

    // ---- Q13 键不变 / 重发 ----

    [Fact]
    public void Redelivery_of_either_packet_after_a_merge_is_a_duplicate_and_the_row_is_unchanged()
    {
        var (ing, repo) = New();
        ing.Ingest(Wss(ts: Stamp));
        ing.Ingest(Bcs(ts: Stamp, recvSec: 0.2));
        Assert.Equal(IngestOutcome.Duplicate, ing.Ingest(Wss(ts: Stamp, recvSec: 30)).Outcome);
        Assert.Equal(IngestOutcome.Duplicate, ing.Ingest(Bcs(ts: Stamp, recvSec: 31)).Outcome);
        Assert.Single(repo.List());
    }

    [Fact]
    public void Q13_dedup_key_is_unchanged_by_the_merge_work()
    {
        var w = Wss(ts: Stamp); var b = Bcs(ts: Stamp);
        Assert.StartsWith("dev-1|1|", MeasurementPolicy.DedupKey(w));
        Assert.StartsWith("dev-1|2|", MeasurementPolicy.DedupKey(b));
        Assert.NotEqual(MeasurementPolicy.DedupKey(w), MeasurementPolicy.DedupKey(b));
        Assert.Null(MeasurementPolicy.DedupKey(Wss()));        // 没有时间戳仍然没有键
    }

    [Fact]
    public void Merge_is_skipped_when_the_stored_row_was_deleted_in_the_meantime()
    {
        var (ing, repo) = New();
        var a = ing.Ingest(Wss(ts: Stamp));
        repo.Delete(new[] { a.Measurement.Id });
        var b = ing.Ingest(Bcs(ts: Stamp, recvSec: 0.1));
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Single(repo.List());
        Assert.Null(b.Measurement.PossibleSameAsId);
    }

    // ---- 导出 ----

    [Fact]
    public void Export_names_the_merged_source_instead_of_unknown()
    {
        var merged = Wss(ts: Stamp) with { Source = MeasurementSource.Both };
        Assert.Contains("weight-scale+body-composition", CsvExporter.ToCsv(new[] { merged }));
        Assert.DoesNotContain("unknown", CsvExporter.ToCsv(new[] { merged }));
        Assert.Contains("weight-scale+body-composition", JsonExporter.ToJson(new[] { merged }, T0));
    }
}
