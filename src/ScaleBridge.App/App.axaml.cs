using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScaleBridge.App.Localization;
using ScaleBridge.App.Services;
using ScaleBridge.App.ViewModels;
using ScaleBridge.App.Views;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

namespace ScaleBridge.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var opts = Program.Options;
            var db = new SqliteDatabase(AppPaths.DatabasePath);
            var settingsStore = new SqliteSettingsStore(db);
            var settings = settingsStore.Load();
            if (opts.Scenario is not null) settings = settings with { AdapterId = AdapterFactory.FakeId, ScenarioId = opts.Scenario };
            if (opts.Language is not null) settings = settings with { Language = opts.Language == "zh" ? LanguageSetting.Zh : LanguageSetting.En };
            if (opts.Smoke) settings = settings with { AdapterId = AdapterFactory.FakeId };

            var window = new MainWindow();
            var vm = new MainViewModel(
                new SqliteMeasurementRepository(db), settingsStore, new AvaloniaDialogService(() => window), new AvaloniaDispatcher(),
                new AdapterFactory(opts.Fast || opts.Smoke || opts.Autoplay ? 0.1 : 1.0), Loc.Instance, AppPaths.DataDirectory, settings);
            if (opts.Tab is { } tab) vm.SelectedTab = tab;
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => vm.Dispose();

            if (opts.Autoplay)
                window.Opened += (_, _) => DispatcherTimer.RunOnce(async () => await SmokeAsync(vm, keepRunning: true), TimeSpan.FromSeconds(1));
            if (opts.Smoke)
                window.Opened += (_, _) => DispatcherTimer.RunOnce(async () => desktop.Shutdown(await SmokeAsync(vm)), TimeSpan.FromSeconds(1));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 无人值守端到端自检（CI 里在 xvfb 下运行）：Fake 适配器扫描 → 连接 → 收到数据 → 写入 SQLite 历史。
    /// 打印 SCALEBRIDGE_SMOKE_OK 表示整条链路通；这只证明演示数据链路，不代表任何真机验证。
    /// </summary>
    private static async Task<int> SmokeAsync(MainViewModel vm, bool keepRunning = false)
    {
        async Task<bool> WaitAsync(Func<bool> cond, int ms)
        {
            for (var t = 0; t < ms && !cond(); t += 50) await Task.Delay(50);
            return cond();
        }
        await vm.StartScanAsync();
        if (!await WaitAsync(() => vm.Devices.Count > 0, 10_000)) { Console.WriteLine("SCALEBRIDGE_SMOKE_FAIL no device found"); return 1; }
        vm.SelectedDevice = vm.Devices[0];
        vm.Connect();
        if (!await WaitAsync(() => vm.LastMeasurement is not null && vm.History.Count > 0, 15_000))
        {
            Console.WriteLine($"SCALEBRIDGE_SMOKE_FAIL no measurement (connection: {vm.ConnectionText})");
            return 1;
        }
        var m = vm.LastMeasurement!;
        if (keepRunning) return 0;
        vm.StopScan();
        vm.Disconnect();
        Console.WriteLine($"SCALEBRIDGE_SMOKE_OK adapter={vm.SelectedAdapter.Value} scenario={vm.SelectedScenario.Value} devices={vm.Devices.Count} " +
                          $"history={vm.History.Count} weightKg={m.WeightKg} demo={m.IsDemo} experimental={m.IsExperimental} lang={vm.L.LanguageCode} data={vm.DataDirectory}");
        return 0;
    }
}
