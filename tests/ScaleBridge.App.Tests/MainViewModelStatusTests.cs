using System.ComponentModel;
using System.Text.RegularExpressions;
using ScaleBridge.App.Services;
using ScaleBridge.App.ViewModels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Tests;

/// <summary>
/// L01 / L02 / L03 / L07：状态栏与连接状态严格同步；扫描超时与「未发现设备」；断开原因整句同一种语言；实时区缺失字段统一显示「—」。
/// </summary>
public class MainViewModelStatusTests
{
    private static readonly LanguageSetting[] Langs = { LanguageSetting.En, LanguageSetting.Zh };

    public static IEnumerable<object[]> BothLanguages() => Langs.Select(l => new object[] { l });

    // ---------- L02：状态栏 ↔ 连接状态 ----------

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task StatusBar_FollowsConnectionThroughConnectReceiveDropAndReconnect(LanguageSetting lang)
    {
        using var t = new TestApp("mid-connection-disconnect", lang);
        var connectedWaiting = t.Loc["Status.Connected"];
        var dropped = t.Loc["Status.Dropped"];

        Assert.Equal("", t.Read(() => t.Vm.StatusText)); // 启动：断开 → 状态栏空

        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        Assert.Equal(dropped, t.Read(() => t.Vm.StatusText));
        Assert.DoesNotContain(connectedWaiting, t.Read(() => t.Vm.StatusText));
        Assert.Equal(t.Loc["Conn.Dropped"] + (lang == LanguageSetting.Zh ? "：" : ": ") + t.Loc["Err.DeviceDisconnected"],
            t.Read(() => t.Vm.ConnectionText));

        Assert.Contains("powered off", t.Read(() => t.Vm.ConnectionDetail)!);
    }

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task StatusBar_ManualDisconnect_ClearsStatusAndConnectionText(LanguageSetting lang)
    {
        using var t = new TestApp("normal-si", lang);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.IsConnected, "connected");
        Assert.Equal(t.Loc["Conn.Connected"], t.Read(() => t.Vm.ConnectionText));
        t.Read(() => { t.Vm.Disconnect(); return 0; });
        Assert.Equal("", t.Read(() => t.Vm.StatusText));
        Assert.Equal(t.Loc["Conn.Disconnected"], t.Read(() => t.Vm.ConnectionText));
        Assert.Null(t.Read(() => t.Vm.ConnectionDetail));
    }

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task StatusBar_ShowsReceivingCount_ThenResetsOnReconnect(LanguageSetting lang)
    {
        using var t = new TestApp("reconnect", lang);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.History.Count >= 1, "first measurement");
        // 收到数据后：要么仍在连接并显示接收计数，要么已掉线并显示「连接已断开」，但绝不会在断开后还写「已连接」
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "scripted drop");
        Assert.Equal(t.Loc["Status.Dropped"], t.Read(() => t.Vm.StatusText));

        t.Read(() => { t.Vm.Connect(); return 0; });
        // 第二次连接收到 2 条：计数从 0 重新数（不是 3），且此时确实处于连接状态
        await t.UntilAsync(() => t.Vm.History.Count == 3, "both rows of the second connection");
        Assert.True(t.Read(() => t.Vm.IsConnected));
        Assert.Equal(t.Loc.Format("Status.Receiving", 2), t.Read(() => t.Vm.StatusText));
    }

    [Theory]
    [InlineData("reconnect", LanguageSetting.En)]
    [InlineData("reconnect", LanguageSetting.Zh)]
    [InlineData("mid-connection-disconnect", LanguageSetting.En)]
    [InlineData("mid-connection-disconnect", LanguageSetting.Zh)]
    public async Task Invariant_ConnectedStatusTextNeverShownUnlessConnected(string scenario, LanguageSetting lang)
    {
        using var t = new TestApp(scenario, lang);
        var violations = new List<string>();
        var snapshots = 0;
        var waiting = t.Loc["Status.Connected"];
        var receivingPrefix = Regex.Split(t.Loc["Status.Receiving"], @"\{0\}")[0];
        void Check(object? s, PropertyChangedEventArgs e)
        {
            // 在 UI 锁内执行：读到的是一致的快照
            var vm = t.Vm;
            snapshots++;
            var st = vm.StatusText;
            var claimsConnected = st == waiting || st.StartsWith(receivingPrefix, StringComparison.Ordinal);
            if (claimsConnected && !vm.IsConnected) violations.Add($"[{e.PropertyName}] status='{st}' but state={vm.ConnectionState}");
            if (vm.IsConnected && !claimsConnected && st.Length > 0 && !st.StartsWith(t.Loc["Status.Dropped"], StringComparison.Ordinal))
                violations.Add($"[{e.PropertyName}] connected but status='{st}'");
            if (vm.ConnectionState == ConnectionState.Dropped && vm.ConnectionText.Contains(t.Loc["Conn.Connected"] + "\u0000")) violations.Add("dropped shows connected");
        }
        t.Vm.PropertyChanged += Check;

        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "first drop");
        if (scenario == "reconnect")
        {
            t.Read(() => { t.Vm.Connect(); return 0; });
            await t.UntilAsync(() => t.Vm.History.Count == 3, "second connection data");
            t.Read(() => { t.Vm.Disconnect(); return 0; }); // 用户主动断开，同样要同步
        }
        await t.UntilAsync(() => !t.Vm.IsSessionActive, "session ended");
        t.Vm.PropertyChanged -= Check;

        Assert.True(snapshots > 5, "monitor should have observed several transitions");
        Assert.Empty(violations);
    }

    [Fact]
    public async Task StatusBar_ScanningIsDerived_AndStopShowsScanStopped()
    {
        using var t = new TestApp();
        await t.Vm.StartScanAsync();
        Assert.Equal(t.Loc["Status.Scanning"], t.Read(() => t.Vm.StatusText));
        t.Read(() => { t.Vm.StopScan(); return 0; });
        Assert.Equal(t.Loc["Status.ScanStopped"], t.Read(() => t.Vm.StatusText));
        Assert.False(t.Read(() => t.Vm.ScanNoDevice)); // 用户主动停止不算「未发现设备」
    }

    [Fact]
    public async Task StatusBar_ConnectClearsScanMessage_AndTransientMessagesDoNotOutliveConnectionChanges()
    {
        using var t = new TestApp("normal-si");
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count > 0, "device");
        t.Read(() => { t.Vm.StopScan(); return 0; });
        Assert.Equal(t.Loc["Status.ScanStopped"], t.Read(() => t.Vm.StatusText));
        t.Read(() => { t.Vm.SelectedDevice = t.Vm.Devices[0]; t.Vm.Connect(); return 0; });
        await t.UntilAsync(() => t.Vm.IsConnected, "connected");
        Assert.DoesNotContain(t.Loc["Status.ScanStopped"], t.Read(() => t.Vm.StatusText));
    }

    // ---------- L01：扫描超时 / 未发现设备 ----------

    private static TestApp EmptyScanApp(out StubEmptyAdapter stub, TimeSpan timeout, LanguageSetting lang = LanguageSetting.En)
    {
        stub = new StubEmptyAdapter();
        var s = stub;
        return new TestApp(adapter: AdapterFactory.WindowsId, lang: lang, windowsFactory: () => s, scanTimeout: timeout);
    }

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task Scan_WithNoDevice_TimesOut_ShowsHint_AndCanRescan(LanguageSetting lang)
    {
        using var t = EmptyScanApp(out var stub, TimeSpan.FromMilliseconds(500), lang);
        await t.Vm.StartScanAsync();
        Assert.True(t.Read(() => t.Vm.IsScanning));
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan timed out");
        Assert.True(t.Read(() => t.Vm.ScanNoDevice));
        Assert.Equal(t.Loc["Status.NoDeviceFound"], t.Read(() => t.Vm.StatusText));
        Assert.True(t.Read(() => t.Vm.CanScan), "the user can scan again");

        // 重新扫描：提示清除，重新进入扫描；再次超时后又给出提示
        await t.Vm.StartScanAsync();
        Assert.False(t.Read(() => t.Vm.ScanNoDevice));
        Assert.Equal(t.Loc["Status.Scanning"], t.Read(() => t.Vm.StatusText));
        await t.UntilAsync(() => !t.Vm.IsScanning, "second timeout");
        Assert.True(t.Read(() => t.Vm.ScanNoDevice));
        Assert.Equal(2, stub.ScanCalls);
    }

    [Fact]
    public async Task Scan_StoppedByUser_IsNotReportedAsNoDevice()
    {
        using var t = EmptyScanApp(out _, TimeSpan.FromSeconds(30));
        await t.Vm.StartScanAsync();
        t.Read(() => { t.Vm.StopScan(); return 0; });
        await Task.Delay(100);
        Assert.False(t.Read(() => t.Vm.ScanNoDevice));
        Assert.Equal(t.Loc["Status.ScanStopped"], t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Scan_ZeroTimeout_MeansNoTimeout()
    {
        using var t = EmptyScanApp(out _, TimeSpan.Zero);
        await t.Vm.StartScanAsync();
        await Task.Delay(300);
        Assert.True(t.Read(() => t.Vm.IsScanning));
        Assert.False(t.Read(() => t.Vm.ScanNoDevice));
    }

    [Fact]
    public async Task Scan_WithDevices_TimesOut_ReportsCount_WithoutNoDeviceHint()
    {
        using var t = new TestApp(adapter: AdapterFactory.WindowsId, windowsFactory: () => new StubRealAdapter(), scanTimeout: TimeSpan.FromMilliseconds(500));
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => !t.Vm.IsScanning, "timed out");
        Assert.False(t.Read(() => t.Vm.ScanNoDevice));
        Assert.Equal(t.Loc.Format("Status.ScanFinished", 1), t.Read(() => t.Vm.StatusText));
        Assert.Single(t.Vm.Devices);
    }

    [Fact]
    public async Task Scan_AdapterError_DoesNotShowNoDeviceHint()
    {
        var stub = new StubRealAdapter { ScanError = new BleException(BleErrorKind.PermissionDenied, "denied") };
        using var t = new TestApp(adapter: AdapterFactory.WindowsId, windowsFactory: () => stub, scanTimeout: TimeSpan.FromMilliseconds(100));
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan ended");
        Assert.False(t.Read(() => t.Vm.ScanNoDevice));
    }

    [Fact]
    public void DefaultScanTimeout_Is30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), MainViewModel.DefaultScanTimeout);
    }

    // ---------- L03：断开原因整句同一种语言 ----------

    [Fact]
    public async Task DropReason_InChinese_HasNoRawEnglish_AndKeepsDetailAsTooltip()
    {
        using var t = new TestApp("mid-connection-disconnect", LanguageSetting.Zh);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        var text = t.Read(() => t.Vm.ConnectionText);
        Assert.Equal("连接中断：设备已断开", text);
        Assert.DoesNotMatch("[A-Za-z]", text);
        Assert.DoesNotMatch("[A-Za-z]", t.Read(() => t.Vm.StatusText));
        Assert.Contains("powered off", t.Read(() => t.Vm.ConnectionDetail)!); // 原始原因只在悬停提示里
    }

    [Fact]
    public async Task DropReason_InEnglish_HasNoCjk()
    {
        using var t = new TestApp("mid-connection-disconnect", LanguageSetting.En);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        Assert.Equal("Connection dropped: Device disconnected", t.Read(() => t.Vm.ConnectionText));
        Assert.DoesNotMatch(@"[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]", t.Read(() => t.Vm.ConnectionText + t.Vm.StatusText));
    }

    [Fact]
    public async Task DropReason_SwitchingLanguage_RelocalizesWholeSentence()
    {
        using var t = new TestApp("mid-connection-disconnect", LanguageSetting.En);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.ConnectionState == ConnectionState.Dropped, "dropped");
        t.Read(() => { t.Vm.SelectedLanguage = t.Vm.LanguageChoices.First(c => c.Value == LanguageSetting.Zh); return 0; });
        Assert.Equal("连接中断：设备已断开", t.Read(() => t.Vm.ConnectionText));
        Assert.Equal("连接已断开。请选中设备后点「连接」重新连接。", t.Read(() => t.Vm.StatusText));
    }

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task ScanFailure_UsesDictionaryText_NotRawMessage(LanguageSetting lang)
    {
        var stub = new StubRealAdapter { ScanError = new BleException(BleErrorKind.Timeout, "WinRT 0x80070490 raw failure") };
        using var t = new TestApp(adapter: AdapterFactory.WindowsId, lang: lang, windowsFactory: () => stub);
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan ended");
        var text = t.Read(() => t.Vm.StatusText);
        Assert.Equal(t.Loc.Format("Status.ScanFailed", t.Loc["Err.Timeout"]), text);
        Assert.DoesNotContain("raw failure", text);
    }

    // ---------- L07：缺失字段统一显示「—」 ----------

    [Fact]
    public async Task Live_BeforeAnyMeasurement_ShowsWaiting_AndHidesCompositionLabels()
    {
        using var t = new TestApp();
        Assert.Equal(t.Loc["Live.Waiting"], t.Read(() => t.Vm.LiveDetailText));
        Assert.False(t.Read(() => t.Vm.ShowLiveBmi));
        Assert.False(t.Read(() => t.Vm.ShowLiveBodyFat));
        await Task.CompletedTask;
    }

    [Theory, MemberData(nameof(BothLanguages))]
    public async Task Live_WeightOnlyMeasurement_ShowsDashForEveryMissingField(LanguageSetting lang)
    {
        using var t = new TestApp("normal-si", lang);
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.LastMeasurement is not null, "measurement");
        var m = t.Read(() => t.Vm.LastMeasurement)!;
        // normal-si 是 Weight Scale 服务：没有体成分字段
        Assert.Null(m.BodyFatPercent);
        Assert.True(t.Read(() => t.Vm.ShowLiveBmi));
        Assert.True(t.Read(() => t.Vm.ShowLiveBodyFat));
        Assert.Equal(m.Bmi is null ? "BMI —" : "BMI " + DisplayFormat.One(m.Bmi), t.Read(() => t.Vm.LiveBmiText));
        Assert.Equal(t.Loc["Live.BodyFat"] + " —", t.Read(() => t.Vm.LiveBodyFatText));
        var detail = t.Read(() => t.Vm.LiveDetailText);
        foreach (var key in new[] { "Live.User", "Live.Height", "Live.Muscle", "Live.Water", "Live.FatFree", "Live.Bmr", "Live.Impedance" })
        {
            var label = t.Loc[key];
            Assert.True(Regex.IsMatch(detail, Regex.Escape(label) + @" (—|[\d.]+)"), $"'{label}' must be listed with a value or —; got: {detail}");
        }
        Assert.Contains(t.Loc["Live.Muscle"] + " —", detail);
        Assert.Contains(t.Loc["Live.Impedance"] + " —", detail);
    }

    [Fact]
    public async Task Live_FullBodyCompositionMeasurement_ShowsValuesNotDashes()
    {
        using var t = new TestApp("body-composition");
        await t.ScanAndConnectAsync();
        await t.UntilAsync(() => t.Vm.LastMeasurement?.BodyFatPercent is not null, "composition measurement");
        Assert.DoesNotContain(t.Loc["Live.BodyFat"] + " —", t.Read(() => t.Vm.LiveBodyFatText));
        Assert.Contains("%", t.Read(() => t.Vm.LiveBodyFatText));
    }
}

/// <summary>什么设备都扫不到的替身适配器（只用于测试扫描超时，不是任何真实蓝牙）。</summary>
public sealed class StubEmptyAdapter : IBleAdapter
{
    public int ScanCalls;
    public string Id => "windows";
    public string DisplayName => "stub-empty";
    public bool IsDemo => false;
    public Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default) => Task.FromResult(BleAvailability.Available);

    public async IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref ScanCalls);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        yield break;
    }

    public Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default) =>
        throw new BleException(BleErrorKind.Other, "stub does not connect");
}
