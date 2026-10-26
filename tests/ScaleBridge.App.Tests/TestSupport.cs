using ScaleBridge.App;
using ScaleBridge.App.Localization;
using ScaleBridge.App.Services;
using ScaleBridge.App.ViewModels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Data;

namespace ScaleBridge.App.Tests;

/// <summary>同步执行、带锁：后台线程 Post 的 UI 工作与测试线程读取互斥，测试不需要显示器。</summary>
public sealed class TestDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    public void Post(Action action) { lock (_gate) action(); }
    public T Read<T>(Func<T> read) { lock (_gate) return read(); }
}

public sealed class FakeDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public string? SavePath { get; set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
    {
        ConfirmCalls++;
        LastConfirmMessage = message;
        return Task.FromResult(Confirm);
    }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult(SavePath);
}

public sealed class TestApp : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "scalebridge-apptest-" + Guid.NewGuid().ToString("N"));
    public TestDispatcher Ui { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public Loc Loc { get; } = new();
    public SqliteDatabase Db { get; }
    public SqliteMeasurementRepository Repo { get; }
    public SqliteSettingsStore Settings { get; }
    public MainViewModel Vm { get; }

    public TestApp(string scenario = "normal-si", LanguageSetting lang = LanguageSetting.En, string adapter = AdapterFactory.FakeId,
        Func<IBleAdapter>? windowsFactory = null)
    {
        Directory.CreateDirectory(Dir);
        Db = new SqliteDatabase(Path.Combine(Dir, "t.db"));
        Repo = new SqliteMeasurementRepository(Db);
        Settings = new SqliteSettingsStore(Db);
        var s = new AppSettings { Language = lang, ScenarioId = scenario, AdapterId = adapter };
        Vm = new MainViewModel(Repo, Settings, Dialogs, Ui, new AdapterFactory(0, windowsFactory), Loc, Dir, s);
    }

    public T Read<T>(Func<T> f) => Ui.Read(f);

    public async Task UntilAsync(Func<bool> cond, string what, int timeoutMs = 10_000)
    {
        for (var t = 0; t < timeoutMs; t += 20)
        {
            if (Read(cond)) return;
            await Task.Delay(20);
        }
        Assert.Fail("timed out waiting for: " + what);
    }

    /// <summary>扫描到第一个设备 → 选中 → 连接。</summary>
    public async Task ScanAndConnectAsync(int deviceIndex = 0)
    {
        await Vm.StartScanAsync();
        await UntilAsync(() => Vm.Devices.Count > deviceIndex, "device found");
        Read(() => { Vm.SelectedDevice = Vm.Devices[deviceIndex]; return 0; });
        Vm.Connect();
    }

    public void Dispose()
    {
        Vm.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScaleBridge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ScaleBridge.sln not found above " + AppContext.BaseDirectory);
    }
}
