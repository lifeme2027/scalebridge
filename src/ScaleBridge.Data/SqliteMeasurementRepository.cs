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
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO measurements (device_time, received_at, weight_kg, original_weight, original_unit, raw_weight, bmi, height_m,
              body_fat_percent, muscle_percent, muscle_mass_kg, fat_free_mass_kg, soft_lean_mass_kg, body_water_mass_kg,
              basal_metabolism_kj, impedance_ohm, user_id, device_id, device_name, source, experimental, demo)
            VALUES ($device_time, $received_at, $weight_kg, $original_weight, $original_unit, $raw_weight, $bmi, $height_m,
              $body_fat, $muscle_pct, $muscle_mass, $fat_free, $soft_lean, $body_water,
              $basal, $impedance, $user_id, $device_id, $device_name, $source, $experimental, $demo);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, "$device_time", m.DeviceTimestamp?.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv));
        Bind(cmd, "$received_at", m.ReceivedAt.ToString("o", Inv));
        Bind(cmd, "$weight_kg", Dec(m.WeightKg));
        Bind(cmd, "$original_weight", Dec(m.OriginalWeight));
        Bind(cmd, "$original_unit", m.OriginalUnit is { } u ? (long)u : null);
        Bind(cmd, "$raw_weight", m.RawWeight is { } rw ? (long)rw : null);
        Bind(cmd, "$bmi", Dec(m.Bmi));
        Bind(cmd, "$height_m", Dec(m.HeightMeters));
        Bind(cmd, "$body_fat", Dec(m.BodyFatPercent));
        Bind(cmd, "$muscle_pct", Dec(m.MusclePercent));
        Bind(cmd, "$muscle_mass", Dec(m.MuscleMassKg));
        Bind(cmd, "$fat_free", Dec(m.FatFreeMassKg));
        Bind(cmd, "$soft_lean", Dec(m.SoftLeanMassKg));
        Bind(cmd, "$body_water", Dec(m.BodyWaterMassKg));
        Bind(cmd, "$basal", m.BasalMetabolismKj is { } b ? (long)b : null);
        Bind(cmd, "$impedance", Dec(m.ImpedanceOhm));
        Bind(cmd, "$user_id", m.UserId is { } uid ? (long)uid : null);
        Bind(cmd, "$device_id", m.DeviceId);
        Bind(cmd, "$device_name", m.DeviceName);
        Bind(cmd, "$source", (long)m.Source);
        Bind(cmd, "$experimental", m.IsExperimental ? 1L : 0L);
        Bind(cmd, "$demo", m.IsDemo ? 1L : 0L);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), Inv);
        return m with { Id = id };
    }

    public IReadOnlyList<Measurement> List()
    {
        var list = new List<Measurement>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM measurements;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        // 显示时间倒序；相同时按主键倒序（后收到的在前）
        return list.OrderByDescending(x => x.DisplayTime).ThenByDescending(x => x.Id).ToList();
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
            cmd.CommandText = "DELETE FROM measurements WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            total += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return total;
    }

    public int Clear()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM measurements;";
        return cmd.ExecuteNonQuery();
    }

    private static string? Dec(decimal? v) => v?.ToString(Inv);

    private static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static Measurement Read(SqliteDataReader r)
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
        };
    }
}
