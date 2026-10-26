using System.Globalization;
using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

namespace ScaleBridge.Data.Tests;

/// <summary>SB1-M01：分页、去重键持久化、v1→v2 迁移、索引。</summary>
public class PagingAndMigrationTests
{
    private static readonly DateTimeOffset Recv = new(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(8));

    private static Measurement M(int i, DateTime? ts, int raw = 14000) => new()
    {
        DeviceTimestamp = ts,
        ReceivedAt = Recv.AddSeconds(i),
        WeightKg = raw * 0.005m,
        OriginalWeight = raw * 0.005m,
        OriginalUnit = UnitSystem.Si,
        RawWeight = raw,
        DeviceId = "DE:MO:00:00:00:01",
        Source = MeasurementSource.WeightScale,
    };

    [Fact]
    public void ListPage_walks_the_whole_history_in_List_order_including_ties_and_rows_without_device_time()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0);
        for (var i = 0; i < 37; i++)
        {
            // 每 3 条共用一个时间戳（并列：靠主键倒序）；每 5 条没有设备时间（用接收时间的本地墙钟时间排序）
            DateTime? ts = i % 5 == 0 ? null : t0.AddMinutes(i / 3);
            repo.Add(M(i, ts, raw: 10000 + i));
        }

        Assert.Equal(37, repo.Count());
        var all = repo.List();
        var paged = new List<Measurement>();
        Measurement? cursor = null;
        while (true)
        {
            var page = repo.ListPage(10, cursor);
            if (page.Count == 0) break;
            paged.AddRange(page);
            cursor = page[^1];
        }
        Assert.Equal(all.Select(x => x.Id), paged.Select(x => x.Id));
        Assert.Equal(all, paged);
        Assert.Equal(all.Take(4).Select(x => x.Id), repo.ListPage(4).Select(x => x.Id));
        Assert.Empty(repo.ListPage(0));
    }

    [Fact]
    public void ListPage_cursor_is_stable_when_new_rows_arrive_between_pages()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0);
        for (var i = 0; i < 20; i++) repo.Add(M(i, t0.AddMinutes(i), raw: 10000 + i));

        var first = repo.ListPage(8);
        repo.Add(M(99, t0.AddMinutes(500), raw: 12345)); // 新记录排到最前面；偏移量分页在这里会重复一行
        var second = repo.ListPage(8, first[^1]);
        Assert.Empty(first.Select(x => x.Id).Intersect(second.Select(x => x.Id)));
        Assert.Equal(repo.List().Skip(1 + 8).Take(8).Select(x => x.Id), second.Select(x => x.Id));
    }

    [Fact]
    public void Paging_queries_use_the_display_time_index()
    {
        using var t = new TempDb();
        using var c = t.Db.Open();
        string Plan(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var r = cmd.ExecuteReader();
            var s = "";
            while (r.Read()) s += r.GetString(3) + ";";
            return s;
        }
        var first = Plan("SELECT * FROM measurements ORDER BY display_time DESC, id DESC LIMIT 10");
        Assert.Contains("ix_measurements_display", first);
        Assert.DoesNotContain("TEMP B-TREE", first);
        var next = Plan("SELECT * FROM measurements WHERE display_time < '2026' OR (display_time = '2026' AND id < 5) ORDER BY display_time DESC, id DESC LIMIT 10");
        Assert.DoesNotContain("TEMP B-TREE", next);
    }

    [Fact]
    public void Dedup_keys_persist_with_the_row_and_are_released_by_delete_and_clear()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var a = repo.Add(M(0, new DateTime(2026, 10, 1, 8, 0, 0)));
        var b = repo.Add(M(1, new DateTime(2026, 10, 1, 8, 5, 0)));
        var noTs = repo.Add(M(2, null));
        var ka = MeasurementPolicy.DedupKey(a)!;
        var kb = MeasurementPolicy.DedupKey(b)!;
        Assert.Null(MeasurementPolicy.DedupKey(noTs));

        var restarted = new SqliteMeasurementRepository(new SqliteDatabase(t.File)); // 模拟重启
        Assert.True(restarted.HasDedupKey(ka));
        Assert.True(restarted.HasDedupKey(kb));

        Assert.Equal(1, restarted.Delete(new[] { a.Id }));
        Assert.False(restarted.HasDedupKey(ka));
        Assert.True(restarted.HasDedupKey(kb));

        Assert.Equal(2, restarted.Clear());
        Assert.False(restarted.HasDedupKey(kb));
        Assert.Equal(0, restarted.Count());
    }

    private const string V1Schema = """
        CREATE TABLE measurements (
          id INTEGER PRIMARY KEY AUTOINCREMENT, device_time TEXT NULL, received_at TEXT NOT NULL, weight_kg TEXT NULL,
          original_weight TEXT NULL, original_unit INTEGER NULL, raw_weight INTEGER NULL, bmi TEXT NULL, height_m TEXT NULL,
          body_fat_percent TEXT NULL, muscle_percent TEXT NULL, muscle_mass_kg TEXT NULL, fat_free_mass_kg TEXT NULL,
          soft_lean_mass_kg TEXT NULL, body_water_mass_kg TEXT NULL, basal_metabolism_kj INTEGER NULL, impedance_ohm TEXT NULL,
          user_id INTEGER NULL, device_id TEXT NOT NULL, device_name TEXT NULL, source INTEGER NOT NULL,
          experimental INTEGER NOT NULL, demo INTEGER NOT NULL);
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        PRAGMA user_version = 1;
        """;

    private static void SeedV1(string file, params (string? DeviceTime, DateTimeOffset Received, int Raw)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = V1Schema; cmd.ExecuteNonQuery(); }
        foreach (var r in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO measurements(device_time, received_at, weight_kg, original_weight, original_unit, raw_weight, device_id, source, experimental, demo)
                VALUES ($dt, $ra, $w, $w, 0, $raw, 'AA:BB', 1, 1, 0);
                """;
            cmd.Parameters.AddWithValue("$dt", (object?)r.DeviceTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ra", r.Received.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$w", (r.Raw * 0.005m).ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$raw", r.Raw);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Opening_a_v1_database_migrates_it_keeping_every_row_order_and_dedup_keys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scalebridge-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "v1.db");
        try
        {
            SeedV1(file,
                ("2026-10-01T08:00:00", Recv, 14000),
                (null, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), 14002),   // 无设备时间：按接收时间的本地墙钟时间排
                ("2026-10-03T08:00:00", Recv.AddDays(2), 14004),
                ("2026-10-02T08:00:00", Recv.AddDays(1), 14006));

            var db = new SqliteDatabase(file);
            var repo = new SqliteMeasurementRepository(db);
            Assert.Equal(4, repo.Count());
            var list = repo.List();
            Assert.Equal(list.OrderByDescending(x => x.DisplayTime).ThenByDescending(x => x.Id).Select(x => x.Id), list.Select(x => x.Id));
            Assert.Equal(new long[] { 3, 4, 1 }, list.Where(x => x.DeviceTimestamp is not null).Select(x => x.Id));
            Assert.Equal(new[] { 14004, 14006, 14000 }, list.Where(x => x.DeviceTimestamp is not null).Select(x => x.RawWeight!.Value));
            foreach (var m in list.Where(x => x.DeviceTimestamp is not null))
                Assert.True(repo.HasDedupKey(MeasurementPolicy.DedupKey(m)!)); // 迁移前已入库的数据再次到来仍被识别为重复

            using var c = db.Open();
            using var v = c.CreateCommand();
            v.CommandText = "PRAGMA user_version;";
            Assert.Equal(SqliteDatabase.SchemaVersion, Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture));
            using var ix = c.CreateCommand();
            ix.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
            var names = new List<string>();
            using (var r = ix.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
            Assert.Contains("ix_measurements_display", names);
            Assert.Contains("ix_measurements_device_time", names);

            // 再打开一次：已是 v2，不再迁移，数据不变
            Assert.Equal(list, new SqliteMeasurementRepository(new SqliteDatabase(file)).List());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class HistoryWindowSettingTests
{
    [Fact]
    public void History_window_defaults_round_trips_and_is_clamped()
    {
        using var t = new TempDb();
        var store = new SqliteSettingsStore(t.Db);
        Assert.Equal(AppSettings.DefaultHistoryWindow, store.Load().HistoryWindow);
        Assert.Equal(5000, AppSettings.DefaultHistoryWindow);

        store.Save(store.Load() with { HistoryWindow = 20_000 });
        Assert.Equal(20_000, store.Load().HistoryWindow);
        store.Save(store.Load() with { HistoryWindow = 3 });
        Assert.Equal(AppSettings.MinHistoryWindow, store.Load().HistoryWindow);
        store.Save(store.Load() with { HistoryWindow = int.MaxValue });
        Assert.Equal(AppSettings.MaxHistoryWindow, store.Load().HistoryWindow);

        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE settings SET value = 'abc' WHERE key = 'history_window';";
        cmd.ExecuteNonQuery();
        Assert.Equal(AppSettings.DefaultHistoryWindow, store.Load().HistoryWindow); // 损坏的值回落到默认
    }
}
