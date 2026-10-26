using System.Globalization;
using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

namespace ScaleBridge.Data.Tests;

/// <summary>SB1-M02：合并结果与「可能重复」标注在 SQLite 里的持久化（含重启后重发去重、v2→v3 迁移）。</summary>
public class MergePersistenceTests
{
    private static readonly DateTimeOffset Recv = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTime Stamp = new(2026, 10, 26, 8, 0, 0);

    private static Measurement Wss(double sec = 0) => new()
    {
        Source = MeasurementSource.WeightScale, DeviceId = "AA", DeviceTimestamp = Stamp, ReceivedAt = Recv.AddSeconds(sec),
        WeightKg = 70m, OriginalWeight = 70m, OriginalUnit = UnitSystem.Si, RawWeight = 14000,
    };

    private static Measurement Bcs(double sec = 0.2) => new()
    {
        Source = MeasurementSource.BodyComposition, DeviceId = "AA", DeviceTimestamp = Stamp, ReceivedAt = Recv.AddSeconds(sec),
        WeightKg = 70m, OriginalWeight = 70m, OriginalUnit = UnitSystem.Si, RawWeight = 14000, BodyFatPercent = 21.5m, ImpedanceOhm = 500m,
    };

    [Fact]
    public void Merge_is_persisted_and_redelivery_of_either_packet_is_a_duplicate_even_after_restart()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var ing = new MeasurementIngestor(repo);
        Assert.Equal(IngestOutcome.Saved, ing.Ingest(Wss()).Outcome);
        Assert.Equal(IngestOutcome.Merged, ing.Ingest(Bcs()).Outcome);

        var row = Assert.Single(repo.List());
        Assert.Equal(MeasurementSource.Both, row.Source);
        Assert.Equal(21.5m, row.BodyFatPercent);
        Assert.Equal(500m, row.ImpedanceOhm);
        Assert.Equal(1, repo.Count());

        // 重启：新的仓库 + 新的 ingestor（没有内存状态）
        var ing2 = new MeasurementIngestor(new SqliteMeasurementRepository(t.Db));
        Assert.Equal(IngestOutcome.Duplicate, ing2.Ingest(Wss(30)).Outcome);
        Assert.Equal(IngestOutcome.Duplicate, ing2.Ingest(Bcs(31)).Outcome);
        Assert.Equal(1, repo.Count());

        // 删除合并记录后两个键一起释放
        repo.Delete(new[] { row.Id });
        Assert.Equal(IngestOutcome.Saved, ing2.Ingest(Wss(60)).Outcome);
    }

    [Fact]
    public void Possible_duplicate_mark_round_trips_and_get_and_find_work_through_the_index()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var ing = new MeasurementIngestor(repo);
        var a = ing.Ingest(Wss());
        var b = ing.Ingest(Bcs() with { WeightKg = 70.5m, OriginalWeight = 70.5m, RawWeight = 14100 });   // 同一时间戳、体重不同 → 两条并标注
        Assert.Equal(IngestOutcome.Saved, b.Outcome);
        Assert.Equal(a.Measurement.Id, repo.Get(b.Measurement.Id)!.PossibleSameAsId);
        Assert.Null(repo.Get(a.Measurement.Id)!.PossibleSameAsId);
        Assert.Null(repo.Get(999));
        Assert.Equal(2, repo.FindByDeviceTime("AA", Stamp).Count);
        Assert.Empty(repo.FindByDeviceTime("AA", Stamp.AddSeconds(1)));
        Assert.Empty(repo.FindByDeviceTime("BB", Stamp));

        using var c = t.Db.Open();
        using var plan = c.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM measurements WHERE device_id = 'AA' AND device_time = '2026-10-26T08:00:00';";
        var text = new List<string>();
        using (var r = plan.ExecuteReader()) while (r.Read()) text.Add(r.GetString(3));
        Assert.Contains(text, x => x.Contains("ix_measurements_device_time", StringComparison.Ordinal));
    }

    [Fact]
    public void Replace_of_a_missing_row_changes_nothing_and_a_merge_can_move_the_display_time()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        Assert.Null(repo.Replace(Wss() with { Id = 42 }, "k"));
        Assert.False(repo.HasDedupKey("k"));

        // 先到的没有设备时间戳（按接收时间排），合并后得到设备时间戳 → 显示时间随之更新
        var first = repo.Add(Wss() with { DeviceTimestamp = null });
        var other = repo.Add(Wss(5) with { DeviceTimestamp = Stamp.AddHours(5), RawWeight = 14002, WeightKg = 70.01m });
        var merged = repo.Replace(first with { DeviceTimestamp = Stamp.AddHours(10), Source = MeasurementSource.Both }, "k2");
        Assert.NotNull(merged);
        Assert.Equal(new[] { first.Id, other.Id }, repo.List().Select(x => x.Id));
        Assert.True(repo.HasDedupKey("k2"));
    }

    [Fact]
    public void Opening_a_v2_database_adds_the_possible_same_as_column_and_keeps_rows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scalebridge-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "v2.db");
        try
        {
            Directory.CreateDirectory(dir);
            var repo = new SqliteMeasurementRepository(new SqliteDatabase(file));
            repo.Add(Wss());
            // 把它还原成 v2 形态
            using (var c = new SqliteDatabase(file).Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE measurements DROP COLUMN possible_same_as_id; PRAGMA user_version = 2;";
                cmd.ExecuteNonQuery();
            }

            var db = new SqliteDatabase(file);   // 打开时迁移 v2 → v3
            var migrated = new SqliteMeasurementRepository(db);
            var row = Assert.Single(migrated.List());
            Assert.Null(row.PossibleSameAsId);
            Assert.Equal(70m, row.WeightKg);
            migrated.Add(Bcs() with { PossibleSameAsId = row.Id, DeviceTimestamp = null });
            Assert.Equal(row.Id, migrated.List().Single(x => x.Source == MeasurementSource.BodyComposition).PossibleSameAsId);
            using var c2 = db.Open();
            using var v = c2.CreateCommand();
            v.CommandText = "PRAGMA user_version;";
            Assert.Equal(3, Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
