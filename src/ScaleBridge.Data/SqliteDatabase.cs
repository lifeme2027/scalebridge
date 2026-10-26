using Microsoft.Data.Sqlite;

namespace ScaleBridge.Data;

/// <summary>SQLite 文件：建表与版本管理。每次操作用独立连接（Pooling 关闭，方便 Windows 上删除文件）。</summary>
public sealed class SqliteDatabase
{
    public const int SchemaVersion = 1;

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
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
                throw new InvalidOperationException($"Database schema version {version} is newer than this app supports ({SchemaVersion}).");
        }
        using var cmd = c.CreateCommand();
        // decimal 一律以不变区域性的 TEXT 保存，避免二进制浮点；时间用 ISO 8601。
        cmd.CommandText = $"""
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
            PRAGMA user_version = {SchemaVersion};
            """;
        cmd.ExecuteNonQuery();
    }
}
