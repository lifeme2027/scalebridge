using System.Globalization;
using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Data;

public sealed class SqliteMeasurementRepository : IMeasurementRepository
{
    private readonly SqliteDatabase _db;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SqliteMeasurementRepository(SqliteDatabase db) => _db = db;

    public Measurement Add(Measurement m)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO measurements ({Columns})
            VALUES ({Params});
            SELECT last_insert_rowid();
            """;
        BindAll(cmd, m);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), Inv);
        if (MeasurementPolicy.DedupKey(m) is { } key) PutKey(c, tx, key, id);
        tx.Commit();
        return m with { Id = id };
    }

    public Measurement? Replace(Measurement merged, string? additionalDedupKey)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE measurements SET {SetList} WHERE id = $id;";
        BindAll(cmd, merged);
        cmd.Parameters.AddWithValue("$id", merged.Id);
        if (cmd.ExecuteNonQuery() == 0) return null;
        if (additionalDedupKey is not null) PutKey(c, tx, additionalDedupKey, merged.Id);
        tx.Commit();
        return merged;
    }

    public Measurement? Get(long id) =>
        Query("SELECT * FROM measurements WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    public IReadOnlyList<Measurement> FindByDeviceTime(string deviceId, DateTime deviceTime) =>
        Query("SELECT * FROM measurements WHERE device_id = $d AND device_time = $t ORDER BY id;", cmd =>
        {
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$t", deviceTime.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv));
        });

    private static readonly string[] ColumnNames =
    {
        "display_time", "device_time", "received_at", "weight_kg", "original_weight", "original_unit", "raw_weight", "bmi", "height_m",
        "body_fat_percent", "muscle_percent", "muscle_mass_kg", "fat_free_mass_kg", "soft_lean_mass_kg", "body_water_mass_kg",
        "basal_metabolism_kj", "impedance_ohm", "user_id", "device_id", "device_name", "source", "experimental", "demo", "possible_same_as_id",
    };
    private static readonly string Columns = string.Join(", ", ColumnNames);
    private static readonly string Params = string.Join(", ", ColumnNames.Select(n => "$" + n));
    private static readonly string SetList = string.Join(", ", ColumnNames.Select(n => $"{n} = ${n}"));

    private static void BindAll(SqliteCommand cmd, Measurement m)
    {
        Bind(cmd, "$display_time", m.DisplayTime.ToString(SqliteDatabase.DisplayTimeFormat, Inv));
        Bind(cmd, "$device_time", m.DeviceTimestamp?.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv));
        Bind(cmd, "$received_at", m.ReceivedAt.ToString("o", Inv));
        Bind(cmd, "$weight_kg", Dec(m.WeightKg));
        Bind(cmd, "$original_weight", Dec(m.OriginalWeight));
        Bind(cmd, "$original_unit", m.OriginalUnit is { } u ? (long)u : null);
        Bind(cmd, "$raw_weight", m.RawWeight is { } rw ? (long)rw : null);
        Bind(cmd, "$bmi", Dec(m.Bmi));
        Bind(cmd, "$height_m", Dec(m.HeightMeters));
        Bind(cmd, "$body_fat_percent", Dec(m.BodyFatPercent));
        Bind(cmd, "$muscle_percent", Dec(m.MusclePercent));
        Bind(cmd, "$muscle_mass_kg", Dec(m.MuscleMassKg));
        Bind(cmd, "$fat_free_mass_kg", Dec(m.FatFreeMassKg));
        Bind(cmd, "$soft_lean_mass_kg", Dec(m.SoftLeanMassKg));
        Bind(cmd, "$body_water_mass_kg", Dec(m.BodyWaterMassKg));
        Bind(cmd, "$basal_metabolism_kj", m.BasalMetabolismKj is { } b ? (long)b : null);
        Bind(cmd, "$impedance_ohm", Dec(m.ImpedanceOhm));
        Bind(cmd, "$user_id", m.UserId is { } uid ? (long)uid : null);
        Bind(cmd, "$device_id", m.DeviceId);
        Bind(cmd, "$device_name", m.DeviceName);
        Bind(cmd, "$source", (long)m.Source);
        Bind(cmd, "$experimental", m.IsExperimental ? 1L : 0L);
        Bind(cmd, "$demo", m.IsDemo ? 1L : 0L);
        Bind(cmd, "$possible_same_as_id", m.PossibleSameAsId);
    }

    public IReadOnlyList<Measurement> List() => Query("SELECT * FROM measurements ORDER BY display_time DESC, id DESC;", null);

    public int Count()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM measurements;";
        return Convert.ToInt32(cmd.ExecuteScalar(), Inv);
    }

    public IReadOnlyList<Measurement> ListPage(int limit, Measurement? after = null)
    {
        if (limit <= 0) return Array.Empty<Measurement>();
        if (after is null)
            return Query("SELECT * FROM measurements ORDER BY display_time DESC, id DESC LIMIT $limit;", cmd => cmd.Parameters.AddWithValue("$limit", limit));
        return Query("""
            SELECT * FROM measurements
            WHERE display_time < $t OR (display_time = $t AND id < $id)
            ORDER BY display_time DESC, id DESC LIMIT $limit;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$t", after.DisplayTime.ToString(SqliteDatabase.DisplayTimeFormat, Inv));
            cmd.Parameters.AddWithValue("$id", after.Id);
            cmd.Parameters.AddWithValue("$limit", limit);
        });
    }

    public bool HasDedupKey(string key)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dedup_keys WHERE key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is not null;
    }

    private List<Measurement> Query(string sql, Action<SqliteCommand>? bind)
    {
        var list = new List<Measurement>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadRow(r));
        return list;
    }

    private static void PutKey(SqliteConnection c, SqliteTransaction tx, string key, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO dedup_keys(key, measurement_id) VALUES ($k, $id);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int Delete(IEnumerable<long> ids)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) return 0;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var total = 0;
        foreach (var id in distinct)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM dedup_keys WHERE measurement_id = $id; DELETE FROM measurements WHERE id = $id; SELECT changes();";
            cmd.Parameters.AddWithValue("$id", id);
            total += Convert.ToInt32(cmd.ExecuteScalar(), Inv);
        }
        tx.Commit();
        return total;
    }

    public int Clear()
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM dedup_keys; DELETE FROM measurements;";
        cmd.ExecuteNonQuery();
        using var n = c.CreateCommand();
        n.Transaction = tx;
        n.CommandText = "SELECT changes();";
        var total = Convert.ToInt32(n.ExecuteScalar(), Inv);
        tx.Commit();
        return total;
    }

    private static string? Dec(decimal? v) => v?.ToString(Inv);

    private static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static bool HasColumn(SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static Measurement ReadRow(SqliteDataReader r)
    {
        string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        decimal? D(string col) => S(col) is { } s && decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, Inv, out var d) ? d : null;
        long? L(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));

        return new Measurement
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            DeviceTimestamp = S("device_time") is { } dt &&
                DateTime.TryParseExact(dt, "yyyy-MM-dd'T'HH:mm:ss", Inv, DateTimeStyles.None, out var t) ? t : null,
            ReceivedAt = DateTimeOffset.TryParse(S("received_at"), Inv, DateTimeStyles.RoundtripKind, out var ra) ? ra : DateTimeOffset.UnixEpoch,
            WeightKg = D("weight_kg"),
            OriginalWeight = D("original_weight"),
            OriginalUnit = L("original_unit") is { } ou ? (UnitSystem)ou : null,
            RawWeight = L("raw_weight") is { } raw ? (int)raw : null,
            Bmi = D("bmi"),
            HeightMeters = D("height_m"),
            BodyFatPercent = D("body_fat_percent"),
            MusclePercent = D("muscle_percent"),
            MuscleMassKg = D("muscle_mass_kg"),
            FatFreeMassKg = D("fat_free_mass_kg"),
            SoftLeanMassKg = D("soft_lean_mass_kg"),
            BodyWaterMassKg = D("body_water_mass_kg"),
            BasalMetabolismKj = L("basal_metabolism_kj") is { } bk ? (int)bk : null,
            ImpedanceOhm = D("impedance_ohm"),
            UserId = L("user_id") is { } uid ? (int)uid : null,
            DeviceId = S("device_id") ?? "",
            DeviceName = S("device_name"),
            Source = (MeasurementSource)(L("source") ?? 1),
            IsExperimental = (L("experimental") ?? 1) != 0,
            IsDemo = (L("demo") ?? 0) != 0,
            // v1→v2 迁移读行时这一列还不存在
            PossibleSameAsId = HasColumn(r, "possible_same_as_id") ? L("possible_same_as_id") : null,
        };
    }
}
