using Microsoft.Data.Sqlite;
using ScaleBridge.Core.Models;

namespace ScaleBridge.Data;

/// <summary>设置存在同一个 SQLite 文件的 key/value 表里。读到未知/损坏的值时回退到默认值，不抛异常。</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteDatabase _db;
    public SqliteSettingsStore(SqliteDatabase db) => _db = db;

    public AppSettings Load()
    {
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) kv[r.GetString(0)] = r.GetString(1);
        }

        var d = new AppSettings();
        return new AppSettings
        {
            Language = Enum(kv, "language", d.Language),
            Unit = Enum(kv, "unit", d.Unit),
            AdapterId = kv.TryGetValue("adapter", out var a) && !string.IsNullOrWhiteSpace(a) ? a : d.AdapterId,
            ScenarioId = kv.TryGetValue("scenario", out var s) && !string.IsNullOrWhiteSpace(s) ? s : d.ScenarioId,
            ShowBmi = Bool(kv, "show_bmi", d.ShowBmi),
            ShowBodyFat = Bool(kv, "show_body_fat", d.ShowBodyFat),
            HistoryWindow = kv.TryGetValue("history_window", out var hw) &&
                int.TryParse(hw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? AppSettings.NormalizeHistoryWindow(n) : d.HistoryWindow,
        };
    }

    public void Save(AppSettings s)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        void Put(string key, string value)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        Put("language", s.Language.ToString());
        Put("unit", s.Unit.ToString());
        Put("adapter", s.AdapterId);
        Put("scenario", s.ScenarioId);
        Put("show_bmi", s.ShowBmi ? "1" : "0");
        Put("show_body_fat", s.ShowBodyFat ? "1" : "0");
        Put("history_window", AppSettings.NormalizeHistoryWindow(s.HistoryWindow).ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit();
    }

    private static T Enum<T>(Dictionary<string, string> kv, string key, T fallback) where T : struct, Enum =>
        kv.TryGetValue(key, out var v) && System.Enum.TryParse<T>(v, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed : fallback;

    private static bool Bool(Dictionary<string, string> kv, string key, bool fallback) =>
        kv.TryGetValue(key, out var v) ? (v == "1" ? true : v == "0" ? false : fallback) : fallback;
}
