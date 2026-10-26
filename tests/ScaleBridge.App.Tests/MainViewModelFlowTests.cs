using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ScaleBridge.App.Services;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Session;

namespace ScaleBridge.App.Tests;

/// <summary>全流程（扫描 → 连接 → 接收 → 历史 → 导出）都用 Fake 适配器驱动，不需要显示器或蓝牙。</summary>
public class MainViewModelFlowTests
{
    [Fact]
    public async Task NormalFlow_ScanConnectReceiveHistory()
    {
        using var t = new TestApp("normal-si");
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "one device");
        Assert.True(t.Read(() => t.Vm.IsScanning));
        var dev = t.Read(() => t.Vm.Devices[0]);
        Assert.Equal("DE:MO:00:00:00:01", dev.Id);
        Assert.Contains("Demo data", dev.TagText);
        Assert.Contains("0x181D", dev.ProtocolText);

        t.Read(() => { t.Vm.SelectedDevice = dev; return 0; });
        Assert.True(t.Read(() => t.Vm.CanConnect));
        t.Vm.Connect();
        Assert.True(t.Read(() => t.Vm.IsSessionActive));
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 history rows");
        Assert.False(t.Read(() => t.Vm.IsScanning), "connecting stops the scan");

        Assert.True(t.Read(() => t.Vm.IsConnected));
        Assert.Equal("69.900", t.Read(() => t.Vm.LiveWeightText));
        Assert.Equal("kg", t.Read(() => t.Vm.LiveUnitText));
        Assert.True(t.Read(() => t.Vm.LiveIsDemo));
        Assert.True(t.Read(() => t.Vm.IsDemoActive));
        var flags = t.Read(() => t.Vm.History.Select(h => h.FlagsText).ToList());
        Assert.All(flags, f => { Assert.Contains("Demo data", f); Assert.Contains("Experimental", f); });
        Assert.Equal(3, t.Repo.List().Count);
        Assert.All(t.Repo.List(), m => { Assert.True(m.IsDemo); Assert.True(m.IsExperimental); });
        Assert.Equal(new[] { 69.9m, 70.35m, 70.0m }.OrderBy(x => x), t.Repo.List().Select(m => m.WeightKg!.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task Imperial_ShowsLb_AndUnitToggleSwitchesDisplay()
    {
        using var t = new TestApp("imperial");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Vm.ToggleUnitCommand.Execute(null);
        Assert.Equal(WeightUnit.Lb, t.Read(() => t.Vm.Unit));
        Assert.Equal("153.80", t.Read(() => t.Vm.LiveWeightText));
        Assert.Equal("lb", t.Read(() => t.Vm.LiveUnitText));
        Assert.All(t.Read(() => t.Vm.History.Select(h => h.WeightText).ToList()), w => Assert.EndsWith(" lb", w));
        t.Vm.ToggleUnitCommand.Execute(null);
        Assert.All(t.Read(() => t.Vm.History.Select(h => h.WeightText).ToList()), w => Assert.EndsWith(" kg", w));
        // 设置已持久化
        Assert.Equal(WeightUnit.Kg, t.Settings.Load().Unit);
    }

    [Fact]
    public async Task BodyComposition_ShowsBmiAndBodyFat_WithOptionalToggles()
    {
        using var t = new TestApp("body-composition");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 2, "2 rows");
        Assert.Contains("0x181B", t.Read(() => t.Vm.Devices[0].ProtocolText));
        Assert.True(t.Read(() => t.Vm.ShowLiveBodyFat));
        Assert.StartsWith("Body fat", t.Read(() => t.Vm.LiveBodyFatText));
        Assert.All(t.Read(() => t.Vm.History.Select(h => h.SourceText).ToList()), s => Assert.Equal("0x2A9C", s));
        t.Read(() => { t.Vm.ShowBodyFat = false; return 0; });
        Assert.False(t.Read(() => t.Vm.ShowLiveBodyFat));
        Assert.False(t.Settings.Load().ShowBodyFat);
    }

    [Fact]
    public async Task TimestampUserHeight_ShowsUserAndHeightInDetail()
    {
        using var t = new TestApp("timestamp-user-bmi-height");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 2, "2 rows");
        var detail = t.Read(() => t.Vm.LiveDetailText);
        Assert.Contains("User 2", detail);
        Assert.Contains("1.75 m", detail);
        Assert.True(t.Read(() => t.Vm.ShowLiveBmi));
    }

    [Fact]
    public async Task InvalidReading_IsNotSaved_AndShowsNotice()
    {
        using var t = new TestApp("invalid-0xffff");
        var notices = new List<string>();
        t.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(t.Vm.LiveNotice)) notices.Add(t.Vm.LiveNotice); };
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 1, "1 saved row");
        Assert.Equal(71.05m, t.Repo.List().Single().WeightKg);
        Assert.Contains(t.Read(() => notices.ToList()), n => n.Contains("0xFFFF"));
    }

    [Fact]
    public async Task TruncatedPackets_AreReportedAndSkipped_AndListeningContinues()
    {
        using var t = new TestApp("truncated-packet");
        var notices = new List<string>();
        t.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(t.Vm.LiveNotice)) notices.Add(t.Vm.LiveNotice); };
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 1, "valid packet after the broken ones");
        var seen = t.Read(() => notices.ToList());
        Assert.Contains(seen, n => n.Contains("TooShort"));
        Assert.Contains(seen, n => n.Contains("Truncated"));
        Assert.Equal(71.75m, t.Repo.List().Single().WeightKg);
    }

    [Fact]
    public async Task MidConnectionDisconnect_ShowsDroppedWithReason()
    {
        using var t = new TestApp("mid-connection-disconnect");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        var text = t.Read(() => t.Vm.ConnectionText);
        Assert.Equal(t.Loc["Conn.Dropped"] + ": " + t.Loc["Err.DeviceDisconnected"], text);
        Assert.DoesNotContain("powered off", text); // 设备给出的原始英文原因不进正文（见 MainViewModelStatusTests）
        Assert.Single(t.Repo.List());
        Assert.False(t.Read(() => t.Vm.IsSessionActive));
        Assert.True(t.Read(() => t.Vm.CanConnect), "a dropped connection can be retried");
    }

    [Fact]
    public async Task Reconnect_AfterDrop_ReceivesTheSecondConnectionsData()
    {
        using var t = new TestApp("reconnect");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        t.Vm.Connect();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "2 more rows after reconnect");
        Assert.True(t.Read(() => t.Vm.IsConnected));
    }

    [Fact]
    public async Task MultiDevice_ListsAllThree_WithProtocolTypes()
    {
        using var t = new TestApp("multi-device");
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 3, "three devices");
        var protos = t.Read(() => t.Vm.Devices.Select(d => d.ProtocolText).ToList());
        Assert.Equal(2, protos.Count(p => p.Contains("0x181D")));
        Assert.Single(protos, p => p.Contains("0x181B"));
        Assert.All(t.Read(() => t.Vm.Devices.Select(d => d.TagText).ToList()), tag => Assert.Contains("Experimental", tag));
        t.Vm.StopScan();
        Assert.False(t.Read(() => t.Vm.IsScanning));
        Assert.Equal("Scan stopped", t.Read(() => t.Vm.StatusText));
        await t.Vm.ScanTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RapidDoubleConnect_StartsOnlyOneSession()
    {
        using var t = new TestApp("normal-si");
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "device");
        t.Read(() => { t.Vm.SelectedDevice = t.Vm.Devices[0]; return 0; });
        t.Vm.Connect();
        t.Vm.Connect();
        t.Vm.Connect();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        await Task.Delay(200);
        Assert.Equal(3, t.Repo.List().Count);
    }

    [Fact]
    public async Task UnavailableRealAdapter_FallsBackToFake_AndStillScansDemoData()
    {
        // 没有注入 Windows 适配器（Linux/通用构建）时，设置里残留的 "windows" 不能让界面卡在一个什么都不做的选项上。
        using var t = new TestApp(adapter: AdapterFactory.WindowsId);
        Assert.False(t.Vm.IsRealAdapterSelected);
        Assert.True(t.Vm.IsDemoActive);
        Assert.Equal(AdapterFactory.FakeId, t.Vm.SelectedAdapter.Value);
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "demo device");
    }

    [Fact]
    public async Task ZeroWeight_IsNotSaved()
    {
        using var t = new TestApp();
        var ingest = new MeasurementIngestor(t.Repo);
        var m = new Measurement { DeviceId = "x", ReceivedAt = DateTimeOffset.Now, WeightKg = 0m, Source = MeasurementSource.WeightScale };
        var r = ingest.Ingest(m);
        Assert.Equal(IngestOutcome.SkippedZeroWeight, r.Outcome);
        Assert.Empty(t.Repo.List());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Filter_NarrowsRows_ButNotExport()
    {
        using var t = new TestApp("normal-si");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Read(() => { t.Vm.FilterText = "70.350"; return 0; });
        Assert.Single(t.Read(() => t.Vm.History.ToList()));
        Assert.Contains("Showing 1 of 3", t.Read(() => t.Vm.HistorySummary));
        t.Dialogs.SavePath = Path.Combine(t.Dir, "all.json");
        await t.Vm.ExportJsonAsync();
        using var doc = JsonDocument.Parse(File.ReadAllText(t.Dialogs.SavePath));
        Assert.Equal(3, doc.RootElement.GetProperty("measurements").GetArrayLength());
        t.Read(() => { t.Vm.FilterText = "no-such-text"; return 0; });
        Assert.Empty(t.Read(() => t.Vm.History.ToList()));
    }

    [Fact]
    public async Task Export_Csv_HasBomCrlfAndAllRows()
    {
        using var t = new TestApp("normal-si");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Dialogs.SavePath = Path.Combine(t.Dir, "out.csv");
        await t.Vm.ExportCsvAsync();
        var bytes = File.ReadAllBytes(t.Dialogs.SavePath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains("demo", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exported 3 records", t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Export_Json_HasVersionField()
    {
        using var t = new TestApp("imperial");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Dialogs.SavePath = Path.Combine(t.Dir, "out.json");
        await t.Vm.ExportJsonAsync();
        using var doc = JsonDocument.Parse(File.ReadAllText(t.Dialogs.SavePath));
        Assert.Equal("scalebridge-export", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("measurements")[0].GetProperty("demo").GetBoolean());
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing_AndEmptyHistoryIsReported()
    {
        using var t = new TestApp();
        await t.Vm.ExportCsvAsync();
        Assert.Equal("Nothing to export.", t.Vm.StatusText);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Dialogs.SavePath = null; // 用户取消
        await t.Vm.ExportCsvAsync();
        Assert.Empty(Directory.GetFiles(t.Dir, "*.csv"));
    }

    [Fact]
    public async Task Export_ToUnwritablePath_ReportsFailure()
    {
        using var t = new TestApp();
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Dialogs.SavePath = Path.Combine(t.Dir, "no-such-dir", "x.csv");
        await t.Vm.ExportCsvAsync();
        Assert.StartsWith("Export failed", t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task DeleteSelected_AsksConfirmation_AndRespectsCancel()
    {
        using var t = new TestApp("normal-si");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");

        await t.Vm.DeleteSelectedAsync();
        Assert.Equal(0, t.Dialogs.ConfirmCalls);
        Assert.Equal("No records are checked.", t.Vm.StatusText);

        t.Read(() => { t.Vm.History[0].IsChecked = true; t.Vm.History[1].IsChecked = true; return 0; });
        t.Dialogs.Confirm = false;
        await t.Vm.DeleteSelectedAsync();
        Assert.Equal(1, t.Dialogs.ConfirmCalls);
        Assert.Contains("2", t.Dialogs.LastConfirmMessage);
        Assert.Equal(3, t.Repo.List().Count);

        t.Dialogs.Confirm = true;
        await t.Vm.DeleteSelectedAsync();
        Assert.Equal(1, t.Read(() => t.Vm.History.Count));
        Assert.Single(t.Repo.List());
        Assert.Equal("Deleted 2 records.", t.Vm.StatusText);
    }

    [Fact]
    public async Task Clear_AsksConfirmation_ThenEmpties()
    {
        using var t = new TestApp("normal-si");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count == 3, "3 rows");
        t.Dialogs.Confirm = false;
        await t.Vm.ClearHistoryAsync();
        Assert.Equal(3, t.Repo.List().Count);
        t.Dialogs.Confirm = true;
        await t.Vm.ClearHistoryAsync();
        Assert.Empty(t.Repo.List());
        Assert.False(t.Vm.HasHistory);
        await t.Vm.ClearHistoryAsync();
        Assert.Equal("History is empty.", t.Vm.StatusText);
    }

    [Fact]
    public void SelectAll_TogglesVisibleRows()
    {
        using var t = new TestApp();
        for (var i = 0; i < 3; i++)
            t.Repo.Add(new Measurement { DeviceId = "d", ReceivedAt = DateTimeOffset.Now.AddMinutes(-i), WeightKg = 60 + i, Source = MeasurementSource.WeightScale });
        // 在同一个库上重新打开一个 VM，验证启动时会载入历史
        var vm = new ScaleBridge.App.ViewModels.MainViewModel(t.Repo, t.Settings, t.Dialogs, t.Ui, new AdapterFactory(0), new ScaleBridge.App.Localization.Loc(), t.Dir, new AppSettings { Language = LanguageSetting.En });
        Assert.Equal(3, vm.History.Count);
        vm.SelectAllVisibleCommand.Execute(null);
        Assert.All(vm.History, h => Assert.True(h.IsChecked));
        vm.SelectAllVisibleCommand.Execute(null);
        Assert.All(vm.History, h => Assert.False(h.IsChecked));
        vm.Dispose();
    }

    [Fact]
    public async Task LanguageSwitch_ChangesTextsAtRuntime_AndPersists()
    {
        using var t = new TestApp("normal-si", LanguageSetting.En);
        Assert.Equal("Devices", t.Loc["Tab.Devices"]);
        Assert.Equal("Disconnected", t.Vm.ConnectionText);
        var changed = new List<string?>();
        t.Loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        t.Vm.SelectedLanguage = t.Vm.LanguageChoices.First(c => c.Value == LanguageSetting.Zh);
        Assert.Equal("设备", t.Loc["Tab.Devices"]);
        Assert.Equal("未连接", t.Vm.ConnectionText);
        Assert.Contains("Item", changed);
        Assert.Equal(LanguageSetting.Zh, t.Settings.Load().Language);
        Assert.Contains(t.Vm.ScenarioChoices, c => c.Text.Contains("标准公制体重"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SettingsPersist_AdapterAndScenario()
    {
        using var t = new TestApp("normal-si");
        t.Vm.SelectedScenario = t.Vm.ScenarioChoices.First(c => c.Value == "imperial");
        var saved = t.Settings.Load();
        Assert.Equal("imperial", saved.ScenarioId);
        Assert.Equal(AdapterFactory.FakeId, saved.AdapterId);
        Assert.Equal(new[] { AdapterFactory.FakeId }, t.Vm.AdapterChoices.Select(c => c.Value));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DemoBanner_FlagFollowsAdapterChoice()
    {
        using var t = new TestApp();
        Assert.True(t.Vm.IsDemoActive);
        using var w = new TestApp(windowsFactory: () => new StubRealAdapter());
        Assert.True(w.Vm.IsDemoActive);
        w.Vm.SelectedAdapter = w.Vm.AdapterChoices.First(c => c.Value == AdapterFactory.WindowsId);
        Assert.False(w.Vm.IsDemoActive);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Disconnect_StopsSession_AndLateEventsAreIgnored()
    {
        using var t = new TestApp("normal-si");
        await t.ScanAndConnectAsync();
        t.Vm.Disconnect();
        Assert.Equal(ConnectionState.Disconnected, t.Vm.ConnectionState);
        await Task.Delay(300);
        Assert.Equal(ConnectionState.Disconnected, t.Read(() => t.Vm.ConnectionState));
    }
}
