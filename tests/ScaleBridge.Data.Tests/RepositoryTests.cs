using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

namespace ScaleBridge.Data.Tests;

public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "scalebridge-tests-" + Guid.NewGuid().ToString("N"));
    public string File => Path.Combine(Dir, "test.db");
    public SqliteDatabase Db { get; }
    public TempDb() { Db = new SqliteDatabase(File); }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { /* best effort */ }
    }
}

public class RepositoryTests
{
    private static Measurement Sample(int n = 0) => new()
    {
        DeviceTimestamp = new DateTime(2026, 10, 26, 8, 30, 59).AddMinutes(n),
        ReceivedAt = new DateTimeOffset(2026, 10, 26, 8, 31, 0, TimeSpan.FromHours(8)).AddMinutes(n),
        WeightKg = 70.005m + n,
        OriginalWeight = 154.32m,
        OriginalUnit = UnitSystem.Imperial,
        RawWeight = 15432,
        Bmi = 24.5m,
        HeightMeters = 1.7526m,
        BodyFatPercent = 21.5m,
        MusclePercent = 42.1m,
        MuscleMassKg = 30.005m,
        FatFreeMassKg = 55.5m,
        SoftLeanMassKg = 50m,
        BodyWaterMassKg = 40.25m,
        BasalMetabolismKj = 6500,
        ImpedanceOhm = 512.3m,
        UserId = 2,
        DeviceId = "DE:MO:00:00:00:01",
        DeviceName = "演示体重秤 A",
        Source = MeasurementSource.BodyComposition,
        IsExperimental = true,
        IsDemo = true,
    };

    [Fact]
    public void Add_assigns_an_id_and_round_trips_every_field_exactly()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var added = repo.Add(Sample());
        Assert.True(added.Id > 0);
        var back = Assert.Single(repo.List());
        Assert.Equal(added, back);
        Assert.Equal(70.005m, back.WeightKg);
        Assert.Equal("演示体重秤 A", back.DeviceName);
    }

    [Fact]
    public void Nulls_round_trip_as_nulls()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var m = new Measurement { ReceivedAt = DateTimeOffset.UtcNow, DeviceId = "x", Source = MeasurementSource.WeightScale };
        var back = Assert.Single(new[] { repo.Add(m) }.Select(a => repo.List().Single(x => x.Id == a.Id)));
        Assert.Null(back.WeightKg);
        Assert.Null(back.DeviceTimestamp);
        Assert.Null(back.OriginalUnit);
        Assert.Null(back.UserId);
        Assert.Null(back.DeviceName);
        Assert.False(back.IsDemo);
    }

    [Fact]
    public void Decimal_precision_survives_without_binary_float_noise()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var kg = 154.32m * 0.45359237m; // 69.9990... 十位小数
        repo.Add(new Measurement { ReceivedAt = DateTimeOffset.UtcNow, DeviceId = "x", WeightKg = kg });
        Assert.Equal(kg, repo.List().Single().WeightKg);
    }

    [Fact]
    public void List_is_newest_display_time_first_with_id_as_tiebreaker()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var a = repo.Add(Sample(0));
        var b = repo.Add(Sample(5));
        var c = repo.Add(Sample(5));
        Assert.Equal(new[] { c.Id, b.Id, a.Id }, repo.List().Select(x => x.Id));
    }

    [Fact]
    public void Delete_removes_only_requested_ids_and_reports_the_count()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var a = repo.Add(Sample(0));
        var b = repo.Add(Sample(1));
        var c = repo.Add(Sample(2));
        Assert.Equal(2, repo.Delete(new[] { a.Id, c.Id, c.Id, 9999 }));
        Assert.Equal(b.Id, Assert.Single(repo.List()).Id);
        Assert.Equal(0, repo.Delete(Array.Empty<long>()));
    }

    [Fact]
    public void Clear_empties_everything()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        for (int i = 0; i < 5; i++) repo.Add(Sample(i));
        Assert.Equal(5, repo.Clear());
        Assert.Empty(repo.List());
        Assert.Equal(0, repo.Clear());
    }

    [Fact]
    public void Data_persists_across_a_reopened_database()
    {
        using var t = new TempDb();
        new SqliteMeasurementRepository(t.Db).Add(Sample());
        var again = new SqliteMeasurementRepository(new SqliteDatabase(t.File));
        Assert.Single(again.List());
    }

    [Fact]
    public void Text_with_quotes_and_sql_metacharacters_is_stored_verbatim()
    {
        using var t = new TempDb();
        var repo = new SqliteMeasurementRepository(t.Db);
        var name = "x'); DROP TABLE measurements;--";
        repo.Add(Sample() with { DeviceName = name });
        Assert.Equal(name, repo.List().Single().DeviceName);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_instead_of_silently_corrupted()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version = 99;"; cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => new SqliteDatabase(t.File));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_when_nothing_was_saved()
    {
        using var t = new TempDb();
        Assert.Equal(new AppSettings(), new SqliteSettingsStore(t.Db).Load());
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        using var t = new TempDb();
        var store = new SqliteSettingsStore(t.Db);
        var s = new AppSettings { Language = LanguageSetting.En, Unit = WeightUnit.Lb, AdapterId = "linux", ScenarioId = "imperial", ShowBmi = false, ShowBodyFat = false };
        store.Save(s);
        Assert.Equal(s, new SqliteSettingsStore(new SqliteDatabase(t.File)).Load());
    }

    [Fact]
    public void Saving_twice_overwrites()
    {
        using var t = new TempDb();
        var store = new SqliteSettingsStore(t.Db);
        store.Save(new AppSettings { Unit = WeightUnit.Lb });
        store.Save(new AppSettings { Unit = WeightUnit.Kg });
        Assert.Equal(WeightUnit.Kg, store.Load().Unit);
    }

    [Fact]
    public void Corrupt_values_fall_back_to_defaults()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO settings VALUES ('language','Klingon'),('unit','99'),('adapter',''),('show_bmi','maybe');";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(new AppSettings(), new SqliteSettingsStore(t.Db).Load());
    }
}

public class AppPathsTests
{
    [Fact]
    public void Env_var_overrides_the_data_directory_and_creates_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-paths-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, dir);
            Assert.Equal(dir, AppPaths.DataDirectory);
            Assert.True(Directory.Exists(dir));
            Assert.Equal(Path.Combine(dir, "scalebridge.db"), AppPaths.DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, old);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
