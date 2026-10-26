using System.Runtime.CompilerServices;
using ScaleBridge.App.Services;
using ScaleBridge.App.ViewModels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.App.Tests;

/// <summary>可控的「真实适配器」替身（只在测试里模拟 Windows 适配器的可用性与错误，不是任何真实蓝牙）。</summary>
public sealed class StubRealAdapter : IBleAdapter
{
    public BleAvailability Availability { get; set; } = BleAvailability.Available;
    public BleException? ScanError { get; set; }
    public int ScanCalls;

    public string Id => "windows";
    public string DisplayName => "stub";
    public bool IsDemo => false;

    public Task<BleAvailability> GetAvailabilityAsync(CancellationToken ct = default) => Task.FromResult(Availability);

    public async IAsyncEnumerable<AdvertisedDevice> ScanAsync(ScanOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref ScanCalls);
        if (ScanError is not null) throw ScanError;
        yield return new AdvertisedDevice("AA:BB:CC:DD:EE:FF", "Real Scale", -60,
            new[] { GattUuids.WeightScaleService }, new Dictionary<ushort, byte[]>(), DateTimeOffset.UtcNow, IsDemo: false);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    }

    public Task<IBleConnection> ConnectAsync(string deviceId, TimeSpan? timeout = null, CancellationToken ct = default) =>
        throw new BleException(BleErrorKind.Other, "stub does not connect");
}

public class PlatformAdapterTests
{
    private static TestApp RealApp(StubRealAdapter stub, string adapter = AdapterFactory.WindowsId, LanguageSetting lang = LanguageSetting.En)
        => new(adapter: adapter, lang: lang, windowsFactory: () => stub);

    // ---- AdapterFactory ----

    [Fact]
    public void Factory_WithoutWindowsFactory_OffersOnlyFake_AndNeverBuildsRealAdapters()
    {
        var f = new AdapterFactory(0);
        Assert.Equal(new[] { AdapterFactory.FakeId }, f.AvailableAdapterIds);
        Assert.NotNull(f.Create(new AppSettings { AdapterId = AdapterFactory.FakeId }));
        Assert.Null(f.Create(new AppSettings { AdapterId = AdapterFactory.WindowsId }));
        Assert.Null(f.Create(new AppSettings { AdapterId = AdapterFactory.LinuxId }));
        Assert.Null(f.Create(new AppSettings { AdapterId = "nonsense" }));
    }

    [Fact]
    public void Factory_WithWindowsFactory_OffersFakeFirst_ThenWindows()
    {
        var f = new AdapterFactory(0, () => new StubRealAdapter());
        Assert.Equal(new[] { AdapterFactory.FakeId, AdapterFactory.WindowsId }, f.AvailableAdapterIds);
    }

    [Fact]
    public void Factory_Windows_UsesInjectedAdapter_AndFakeIsStillDemo()
    {
        var stub = new StubRealAdapter();
        var f = new AdapterFactory(0, () => stub);
        Assert.Same(stub, f.Create(new AppSettings { AdapterId = AdapterFactory.WindowsId }));
        Assert.True(f.Create(new AppSettings { AdapterId = AdapterFactory.FakeId })!.IsDemo);
    }

    [Fact]
    public void Factory_LinuxIsNeverOffered_EvenWithWindowsFactory()
    {
        var f = new AdapterFactory(0, () => new StubRealAdapter());
        Assert.DoesNotContain(AdapterFactory.LinuxId, f.AvailableAdapterIds);
        Assert.Null(f.Create(new AppSettings { AdapterId = AdapterFactory.LinuxId }));
    }

    [Fact]
    public void GenericBuild_HasNoRealWindowsFactory() => Assert.Null(PlatformAdapters.WindowsFactory());

    // ---- 选择 / 默认值 / 持久化 ----

    [Fact]
    public void DefaultSettings_UseFake_EvenWhenRealBluetoothIsOffered()
    {
        using var t = new TestApp(windowsFactory: () => new StubRealAdapter());
        Assert.Equal(AdapterFactory.FakeId, t.Vm.SelectedAdapter.Value);
        Assert.True(t.Vm.IsDemoActive);
        Assert.False(t.Vm.IsRealAdapterSelected);
        Assert.Equal(new[] { AdapterFactory.FakeId, AdapterFactory.WindowsId }, t.Vm.AdapterChoices.Select(c => c.Value));
        Assert.Equal(AdapterFactory.FakeId, new AppSettings().AdapterId);
    }

    [Fact]
    public void WindowsOffered_RealChoiceLabelCarriesExperimentalLabel_InBothLanguages()
    {
        using var en = new TestApp(lang: LanguageSetting.En, windowsFactory: () => new StubRealAdapter());
        using var zh = new TestApp(lang: LanguageSetting.Zh, windowsFactory: () => new StubRealAdapter());
        Assert.Contains("not verified on real hardware", en.Vm.AdapterChoices.Single(c => c.Value == AdapterFactory.WindowsId).Text);
        Assert.Contains("实验性（仅按规范实现，未真机验证）", zh.Vm.AdapterChoices.Single(c => c.Value == AdapterFactory.WindowsId).Text);
    }

    [Fact]
    public void SelectingReal_PersistsAndClearsDemoFlag_AndShowsRealBanner()
    {
        using var t = RealApp(new StubRealAdapter(), AdapterFactory.FakeId);
        t.Vm.SelectedAdapter = t.Vm.AdapterChoices.First(c => c.Value == AdapterFactory.WindowsId);
        Assert.Equal(AdapterFactory.WindowsId, t.Settings.Load().AdapterId);
        Assert.True(t.Vm.IsRealAdapterSelected);
        Assert.False(t.Vm.IsDemoActive);
        Assert.False(t.Vm.IsFakeAdapterSelected);
    }

    [Fact]
    public void SavedRealChoice_IsRestoredOnNextStart()
    {
        using var t = RealApp(new StubRealAdapter(), AdapterFactory.FakeId);
        t.Vm.SelectedAdapter = t.Vm.AdapterChoices.First(c => c.Value == AdapterFactory.WindowsId);
        var restored = new MainViewModel(t.Repo, t.Settings, t.Dialogs, t.Ui, new AdapterFactory(0, () => new StubRealAdapter()),
            new ScaleBridge.App.Localization.Loc(), t.Dir);
        Assert.Equal(AdapterFactory.WindowsId, restored.SelectedAdapter.Value);
        Assert.True(restored.IsRealAdapterSelected);
        restored.Dispose();
    }

    [Fact]
    public void SavedRealChoice_OnMachineWithoutIt_FallsBackToFake_AndSettingsAgree()
    {
        using var t = new TestApp(adapter: AdapterFactory.WindowsId); // 没有 Windows 工厂
        Assert.Equal(AdapterFactory.FakeId, t.Vm.SelectedAdapter.Value);
        Assert.Equal(AdapterFactory.FakeId, t.Vm.CurrentSettings.AdapterId);
    }

    [Fact]
    public void SavedLinuxChoice_FallsBackToFake()
    {
        using var t = new TestApp(adapter: AdapterFactory.LinuxId, windowsFactory: () => new StubRealAdapter());
        Assert.Equal(AdapterFactory.FakeId, t.Vm.SelectedAdapter.Value);
    }

    [Fact]
    public void SwitchingBackToFake_RestoresDemoFlag()
    {
        using var t = RealApp(new StubRealAdapter());
        Assert.True(t.Vm.IsRealAdapterSelected);
        t.Vm.SelectedAdapter = t.Vm.AdapterChoices.First(c => c.Value == AdapterFactory.FakeId);
        Assert.True(t.Vm.IsDemoActive);
        Assert.False(t.Vm.IsRealAdapterSelected);
        Assert.Equal(AdapterFactory.FakeId, t.Settings.Load().AdapterId);
    }

    // ---- 真实适配器的扫描与友好提示 ----

    [Fact]
    public async Task Real_Unavailable_ShowsFriendlyMessage_AndDoesNotScan()
    {
        var stub = new StubRealAdapter { Availability = BleAvailability.Unavailable };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        Assert.False(t.Read(() => t.Vm.IsScanning));
        Assert.Equal(0, stub.ScanCalls);
        var text = t.Read(() => t.Vm.StatusText);
        Assert.Contains("Bluetooth is unavailable", text);
        Assert.Contains("turned off", text);
    }

    [Fact]
    public async Task Real_Unavailable_MessageIsFriendlyInChinese()
    {
        var stub = new StubRealAdapter { Availability = BleAvailability.Unavailable };
        using var t = RealApp(stub, lang: LanguageSetting.Zh);
        await t.Vm.StartScanAsync();
        var text = t.Read(() => t.Vm.StatusText);
        Assert.Contains("蓝牙不可用", text);
        Assert.Contains("打开蓝牙", text);
    }

    [Fact]
    public async Task Real_PermissionDenied_ShowsPermissionMessage()
    {
        var stub = new StubRealAdapter { Availability = BleAvailability.PermissionDenied };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        Assert.False(t.Read(() => t.Vm.IsScanning));
        Assert.Equal(0, stub.ScanCalls);
        Assert.Equal(t.Loc["Status.BlePermission"], t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Real_NotImplementedAvailability_ShowsUnsupportedMessage()
    {
        var stub = new StubRealAdapter { Availability = BleAvailability.NotImplemented };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        Assert.Equal(t.Loc["Status.AdapterUnsupported"], t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Real_BluetoothTurnedOffDuringScan_ShowsFriendlyMessage_AndStopsScanning()
    {
        var stub = new StubRealAdapter { ScanError = new BluetoothUnavailableException("radio off") };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        await t.Vm.ScanTask;
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan stopped");
        Assert.Equal(t.Loc["Status.BleUnavailable"], t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Real_ScanPermissionError_ShowsPermissionMessage()
    {
        var stub = new StubRealAdapter { ScanError = new BlePermissionException("denied") };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        await t.Vm.ScanTask;
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan stopped");
        Assert.Equal(t.Loc["Status.BlePermission"], t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Real_ScanOtherError_ShowsScanFailed()
    {
        var stub = new StubRealAdapter { ScanError = new BleException(BleErrorKind.Other, "boom") };
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        await t.Vm.ScanTask;
        await t.UntilAsync(() => !t.Vm.IsScanning, "scan stopped");
        Assert.Contains("boom", t.Read(() => t.Vm.StatusText));
    }

    [Fact]
    public async Task Real_Scan_ListsDevicesWithoutDemoTag()
    {
        var stub = new StubRealAdapter();
        using var t = RealApp(stub);
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "real device");
        var dev = t.Read(() => t.Vm.Devices[0]);
        Assert.Equal("AA:BB:CC:DD:EE:FF", dev.Id);
        Assert.DoesNotContain("Demo data", dev.TagText);
        Assert.Contains("Experimental", dev.TagText);
        t.Vm.StopScan();
    }

    [Fact]
    public async Task SwitchingAdapters_ResetsDeviceList_AndUsesTheNewAdapter()
    {
        var stub = new StubRealAdapter();
        using var t = RealApp(stub, AdapterFactory.FakeId);
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "demo device");
        Assert.Equal("DE:MO:00:00:00:01", t.Read(() => t.Vm.Devices[0].Id));
        t.Vm.StopScan();
        t.Read(() => { t.Vm.SelectedAdapter = t.Vm.AdapterChoices.First(c => c.Value == AdapterFactory.WindowsId); return 0; });
        Assert.Empty(t.Read(() => t.Vm.Devices.ToList()));
        await t.Vm.StartScanAsync();
        await t.UntilAsync(() => t.Vm.Devices.Count == 1, "real device");
        Assert.Equal("AA:BB:CC:DD:EE:FF", t.Read(() => t.Vm.Devices[0].Id));
        Assert.Equal(1, stub.ScanCalls);
        t.Vm.StopScan();
    }

    // ---- 文案 ----

    [Theory]
    [InlineData("Banner.RealBle")]
    [InlineData("Set.AdapterReal")]
    [InlineData("Adapter.Windows")]
    public void RealBluetoothTexts_CarryTheExperimentalLabel_InChinese(string key)
    {
        var loc = new ScaleBridge.App.Localization.Loc();
        loc.SetChinese(true);
        Assert.Contains("实验性（仅按规范实现，未真机验证）", loc[key]);
    }

    [Theory]
    [InlineData("Banner.RealBle")]
    [InlineData("Set.AdapterReal")]
    [InlineData("Adapter.Windows")]
    public void RealBluetoothTexts_CarryTheExperimentalLabel_InEnglish(string key)
    {
        var loc = new ScaleBridge.App.Localization.Loc();
        loc.SetChinese(false);
        Assert.Contains("not verified on real hardware", loc[key]);
    }

    [Fact]
    public void RealBluetoothBanner_NeverClaimsVerification()
    {
        var loc = new ScaleBridge.App.Localization.Loc();
        foreach (var zh in new[] { true, false })
        {
            loc.SetChinese(zh);
            var text = loc["Banner.RealBle"];
            Assert.DoesNotContain("已真机验证", text);
            Assert.DoesNotContain("已验证", text);
        }
    }
}
