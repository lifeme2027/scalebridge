using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Data;

/// <summary>SQLite 文件：建表与版本管理。每次操作用独立连接（Pooling 关闭，方便 Windows 上删除文件）。</summary>
public sealed class SqliteDatabase
{
    /// <summary>
    /// 2：measurements 增加 display_time（排序/分页用，带索引）；新增 dedup_keys（持久化的 Q13 去重键，查询不必读全表）；
    /// 增加 (device_id, device_time) 索引。1 → 2 的迁移见 <see cref="MigrateToV2"/>。
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>display_time 列的格式：定长 ISO，字符串顺序即时间顺序。</summary>
    internal const string DisplayTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    public string Path { get; }

    public SqliteDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Initialize();
    }

    public SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        int version;
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
                throw new InvalidOperationException($"Database schema version {version} is newer than this app supports ({SchemaVersion}).");
        }
        using (var cmd = c.CreateCommand())
        {
            // decimal 一律以不变区域性的 TEXT 保存，避免二进制浮点；时间用 ISO 8601。
            // 这里是 v1 的表结构；新库也先建 v1 再走同一条迁移，所以迁移路径每次都被测试覆盖。
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS measurements (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  device_time TEXT NULL,
                  received_at TEXT NOT NULL,
                  weight_kg TEXT NULL,
                  original_weight TEXT NULL,
                  original_unit INTEGER NULL,
                  raw_weight INTEGER NULL,
                  bmi TEXT NULL,
                  height_m TEXT NULL,
                  body_fat_percent TEXT NULL,
                  muscle_percent TEXT NULL,
                  muscle_mass_kg TEXT NULL,
                  fat_free_mass_kg TEXT NULL,
                  soft_lean_mass_kg TEXT NULL,
                  body_water_mass_kg TEXT NULL,
                  basal_metabolism_kj INTEGER NULL,
                  impedance_ohm TEXT NULL,
                  user_id INTEGER NULL,
                  device_id TEXT NOT NULL,
                  device_name TEXT NULL,
                  source INTEGER NOT NULL,
                  experimental INTEGER NOT NULL,
                  demo INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS settings (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        if (version < 2) MigrateToV2(c);
    }

    /// <summary>
    /// v1 → v2，单个事务，失败则整体回滚（库保持 v1，不丢数据）：
    /// ① display_time = 设备时间，没有设备时间的用接收时间的本地墙钟时间（与 <see cref="Measurement.DisplayTime"/> 一致；此后时区变化不再改变旧记录的排序）；
    /// ② 对已有记录重算去重键写入 dedup_keys（Q13 键规则不变）；③ 建索引。
    /// </summary>
    private static void MigrateToV2(SqliteConnection c)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        using var tx = c.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec("ALTER TABLE measurements ADD COLUMN display_time TEXT NULL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS dedup_keys (
              key TEXT PRIMARY KEY,
              measurement_id INTEGER NOT NULL
            ) WITHOUT ROWID;
            """);
        Exec("UPDATE measurements SET display_time = device_time || '.0000000' WHERE device_time IS NOT NULL;");

        // 没有设备时间的记录：接收时间的本地墙钟时间要在 C# 里算
        var noTime = new List<(long Id, string Display)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id, received_at FROM measurements WHERE device_time IS NULL;";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var ok = DateTimeOffset.TryParse(r.GetString(1), inv, System.Globalization.DateTimeStyles.RoundtripKind, out var ra);
                noTime.Add((r.GetInt64(0), (ok ? ra : DateTimeOffset.UnixEpoch).LocalDateTime.ToString(DisplayTimeFormat, inv)));
            }
        }
        using (var up = c.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE measurements SET display_time = $d WHERE id = $id;";
            var pd = up.Parameters.Add("$d", SqliteType.Text);
            var pi = up.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, display) in noTime) { pd.Value = display; pi.Value = id; up.ExecuteNonQuery(); }
        }

        // 去重键：只有带设备时间的记录才有键
        var keys = new List<(string Key, long Id)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT * FROM measurements WHERE device_time IS NOT NULL;";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var m = SqliteMeasurementRepository.ReadRow(r);
                if (MeasurementPolicy.DedupKey(m) is { } k) keys.Add((k, m.Id));
            }
        }
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO dedup_keys(key, measurement_id) VALUES ($k, $id);";
            var pk = ins.Parameters.Add("$k", SqliteType.Text);
            var pi = ins.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (k, id) in keys) { pk.Value = k; pi.Value = id; ins.ExecuteNonQuery(); }
        }

        Exec("CREATE INDEX IF NOT EXISTS ix_measurements_display ON measurements(display_time, id);");
        Exec("CREATE INDEX IF NOT EXISTS ix_measurements_device_time ON measurements(device_id, device_time);");
        Exec("CREATE INDEX IF NOT EXISTS ix_dedup_keys_measurement ON dedup_keys(measurement_id);");
        Exec($"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }
}
