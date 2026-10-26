using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ScaleBridge.App.Localization;
using ScaleBridge.App.Services;
using ScaleBridge.App.ViewModels;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

// 历史记录规模基准（SB1-M01）：
//   seed <dir> <N>            用「当前代码」的仓库在 <dir>/bench.db 写入 N 条互异测量（用于造 v1 库）
//   run  <dir> <N> [packets]  对 <dir>/bench.db 做：打开(含迁移) → 构造 MainViewModel(启动加载历史) → 逐包走 UI 线程的接收路径
// 逐包路径通过反射调用 MainViewModel 的私有 OnMeasurement，所以同一份源码可以在修复前后的提交上各编译一次、对照数字。
// 这是性能测量，不是正确性测试；数字与机器有关，只在同一台机器上比较前后。
return Bench.Run(args);

internal static class Bench
{
    private sealed class SyncUi : IUiDispatcher { public void Post(Action a) => a(); }
    private sealed class NoDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string t, string m, string ok, string c) => Task.FromResult(true);
        public Task<string?> PickSaveFileAsync(string t, string s, string e) => Task.FromResult<string?>(null);
    }
    private sealed class MemSettings : ISettingsStore
    {
        private AppSettings _s = new() { Language = LanguageSetting.En };
        public AppSettings Load() => _s;
        public void Save(AppSettings s) => _s = s;
    }

    private static readonly DateTime Base = new(2020, 1, 1, 0, 0, 0);

    private static Measurement Make(int i, DateTime ts) => new()
    {
        DeviceTimestamp = ts,
        ReceivedAt = new DateTimeOffset(ts, TimeSpan.FromHours(8)),
        WeightKg = 50m + (i % 5000) / 100m,
        OriginalWeight = 50m + (i % 5000) / 100m,
        OriginalUnit = UnitSystem.Si,
        RawWeight = 10000 + (i % 5000) * 2,
        DeviceId = "AA:BB:CC:DD:EE:FF",
        DeviceName = "bench scale",
        Source = MeasurementSource.WeightScale,
        IsDemo = true,
    };

    public static int Run(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: seed <dir> <N> | run <dir> <N> [packets]"); return 2; }
        var dir = args[1];
        var n = int.Parse(args[2], CultureInfo.InvariantCulture);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bench.db");
        if (args[0] == "seed")
        {
            var sw = Stopwatch.StartNew();
            var repo = new SqliteMeasurementRepository(new SqliteDatabase(path));
            for (var i = 0; i < n; i++) repo.Add(Make(i, Base.AddSeconds(i * 60L)));
            Console.WriteLine($"seed N={n}: {sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / n:F3} ms/row, one Add per row)");
            return 0;
        }

        var packets = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 200;
        var t0 = Stopwatch.StartNew();
        var db = new SqliteDatabase(path);
        var repo2 = new SqliteMeasurementRepository(db);
        var openMs = t0.Elapsed.TotalMilliseconds;

        t0.Restart();
        var vm = new MainViewModel(repo2, new MemSettings(), new NoDialogs(), new SyncUi(), new AdapterFactory(0), new Loc(), dir,
            new AppSettings { Language = LanguageSetting.En });
        var startupMs = t0.Elapsed.TotalMilliseconds;

        var onMeasurement = typeof(MainViewModel).GetMethod("OnMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel.OnMeasurement not found");
        var times = new double[packets];
        var live = Base.AddSeconds(n * 60L + 3600);
        for (var i = 0; i < packets; i++)
        {
            var m = Make(n + i, live.AddSeconds(i * 60L));
            var s = Stopwatch.StartNew();
            onMeasurement.Invoke(vm, new object[] { m });
            times[i] = s.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        var total = times.Sum();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"run N={n} packets={packets}: open/migrate {openMs:F0} ms | startup(VM+history load) {startupMs:F0} ms | per-packet UI-thread mean {total / packets:F2} ms, p95 {times[(int)(packets * 0.95)]:F2} ms, max {times[^1]:F2} ms, total {total:F0} ms | rows shown {vm.History.Count}"));
        vm.Dispose();
        return 0;
    }
}
