using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleBridge.App.Localization;
using ScaleBridge.App.Services;
using ScaleBridge.Ble.Fake;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Export;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Session;

namespace ScaleBridge.App.ViewModels;

/// <summary>
/// 主界面状态：设备扫描 → 连接 → 实时读数 → 历史 → 导出 → 设置。与 Avalonia 无关（对话框与 UI 线程通过接口注入），
/// 所以整条流程可以在没有显示器、没有蓝牙的 CI 里用 Fake 适配器跑端到端测试。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMeasurementRepository _repo;
    private readonly ISettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IAdapterFactory _adapters;
    private readonly TimeProvider _clock;
    private readonly MeasurementIngestor _ingestor;
    private readonly List<Choice<LanguageSetting>> _languageChoices;
    private readonly List<Choice<WeightUnit>> _unitChoices;
    private readonly List<Choice<string>> _adapterChoices;
    private readonly List<Choice<string>> _scenarioChoices;
    private AppSettings _settings;
    private bool _loading = true;
    private bool _disposed;

    private IBleAdapter? _adapter;
    private string _adapterKey = "";
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _sessionCts;
    private int _sessionGeneration;
    private (string Key, object?[] Args)? _notice;
    private (string Key, object?[] Args)? _status;
    private (ConnectionState State, string? Reason, BleErrorKind? Kind) _conn = (ConnectionState.Disconnected, null, null);
    private List<HistoryItem> _allHistory = new();

    public MainViewModel(IMeasurementRepository repo, ISettingsStore settingsStore, IDialogService dialogs, IUiDispatcher ui,
        IAdapterFactory adapters, Loc loc, string dataDirectory, AppSettings? initialSettings = null, TimeProvider? clock = null)
    {
        _repo = repo;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _ui = ui;
        _adapters = adapters;
        L = loc;
        DataDirectory = dataDirectory;
        _clock = clock ?? TimeProvider.System;
        _settings = initialSettings ?? settingsStore.Load();
        _ingestor = new MeasurementIngestor(repo);

        _languageChoices = new()
        {
            new(LanguageSetting.System, L, l => l["Lang.System"]),
            new(LanguageSetting.Zh, L, _ => "中文"),
            new(LanguageSetting.En, L, _ => "English"),
        };
        _unitChoices = new() { new(WeightUnit.Kg, L, _ => "kg"), new(WeightUnit.Lb, L, _ => "lb") };
        // 只列出本机真的可用的适配器：Fake 永远第一（默认）；Windows 版额外有「真实蓝牙（实验性）」；Linux 只有 Fake。
        _adapterChoices = adapters.AvailableAdapterIds
            .Where(id => id is AdapterFactory.FakeId or AdapterFactory.WindowsId)
            .Select(id => id == AdapterFactory.WindowsId
                ? new Choice<string>(id, L, l => l["Adapter.Windows"])
                : new Choice<string>(id, L, l => l["Adapter.Fake"]))
            .ToList();
        if (_adapterChoices.Count == 0 || _adapterChoices[0].Value != AdapterFactory.FakeId)
            _adapterChoices.Insert(0, new(AdapterFactory.FakeId, L, l => l["Adapter.Fake"]));
        _scenarioChoices = FakeScenarioLibrary.All.Select(s => new Choice<string>(s.Id, L, l => s.Title.Get(l.IsChinese))).ToList();

        LanguageChoices = _languageChoices;
        UnitChoices = _unitChoices;
        AdapterChoices = _adapterChoices;
        ScenarioChoices = _scenarioChoices;

        _selectedLanguage = _languageChoices.First(c => c.Value == _settings.Language);
        _selectedUnit = _unitChoices.First(c => c.Value == _settings.Unit);
        // 设置里存的适配器本机用不了（例如把数据库从 Windows 拷到 Linux）时回落到 Fake，并让内部设置与界面一致。
        _selectedAdapter = _adapterChoices.FirstOrDefault(c => c.Value == _settings.AdapterId) ?? _adapterChoices[0];
        _settings = _settings with { AdapterId = _selectedAdapter.Value };
        _selectedScenario = _scenarioChoices.FirstOrDefault(c => c.Value == _settings.ScenarioId) ?? _scenarioChoices[0];
        _showBmi = _settings.ShowBmi;
        _showBodyFat = _settings.ShowBodyFat;

        ApplyLanguage();
        L.PropertyChanged += OnLocChanged;
        ReloadHistory();
        _loading = false;
    }

    public Loc L { get; }
    public string DataDirectory { get; }
    public IReadOnlyList<Choice<LanguageSetting>> LanguageChoices { get; }
    public IReadOnlyList<Choice<WeightUnit>> UnitChoices { get; }
    public IReadOnlyList<Choice<string>> AdapterChoices { get; }
    public IReadOnlyList<Choice<string>> ScenarioChoices { get; }
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<HistoryItem> History { get; } = new();

    [ObservableProperty] private Choice<LanguageSetting> _selectedLanguage;
    [ObservableProperty] private Choice<WeightUnit> _selectedUnit;
    [ObservableProperty] private Choice<string> _selectedAdapter;
    [ObservableProperty] private Choice<string> _selectedScenario;
    [ObservableProperty] private bool _showBmi;
    [ObservableProperty] private bool _showBodyFat;
    [ObservableProperty] private DeviceItem? _selectedDevice;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private Measurement? _lastMeasurement;
    [ObservableProperty] private int _selectedTab;

    public WeightUnit Unit => SelectedUnit.Value;
    public AppSettings CurrentSettings => _settings;

    // ---- 派生显示 ----
    public bool IsDemoActive => _adapterChoiceIsFake;
    private bool _adapterChoiceIsFake => SelectedAdapter.Value == AdapterFactory.FakeId;
    public bool IsFakeAdapterSelected => _adapterChoiceIsFake;
    /// <summary>选中的是真实蓝牙适配器（目前只有 Windows 的 WinRT 适配器）：界面要显示「实验性（仅按规范实现，未真机验证）」。</summary>
    public bool IsRealAdapterSelected => !_adapterChoiceIsFake;
    public string ScenarioDescription => FakeScenarioLibrary.Find(SelectedScenario.Value) is { } s ? s.Description.Get(L.IsChinese) : "";

    public ConnectionState ConnectionState => _conn.State;
    public bool IsConnected => _conn.State == ConnectionState.Connected;
    public bool IsConnecting => _conn.State == ConnectionState.Connecting;
    public bool IsSessionActive => _conn.State is ConnectionState.Connecting or ConnectionState.Connected;
    public bool CanScan => !IsScanning;
    public bool CanConnect => SelectedDevice is not null && !IsSessionActive;

    public string ConnectionText
    {
        get
        {
            var name = _conn.State switch
            {
                ConnectionState.Connecting => L["Conn.Connecting"],
                ConnectionState.Connected => L["Conn.Connected"],
                ConnectionState.Dropped => L["Conn.Dropped"],
                _ => L["Conn.Disconnected"],
            };
            if (_conn.State != ConnectionState.Dropped) return name;
            var kind = _conn.Kind is { } k ? L["Err." + k] : "";
            var reason = string.IsNullOrWhiteSpace(_conn.Reason) ? "" : _conn.Reason!;
            return $"{name}{(L.IsChinese ? "：" : ": ")}{kind}{(kind.Length > 0 && reason.Length > 0 ? " — " : "")}{reason}";
        }
    }

    public string StatusText => _status is { } s ? L.Format(s.Key, s.Args) : "";
    public string LiveNotice => _notice is { } n ? L.Format(n.Key, n.Args) : "";
    public bool HasLiveNotice => _notice is not null;

    public string LiveWeightText => DisplayFormat.Weight(LastMeasurement?.WeightKg, Unit);
    public string LiveUnitText => DisplayFormat.UnitName(Unit);
    public bool LiveIsDemo => LastMeasurement?.IsDemo == true;
    public bool HasLive => LastMeasurement is not null;

    public string LiveBmiText => LastMeasurement?.Bmi is null ? "" : $"BMI {DisplayFormat.One(LastMeasurement.Bmi)}";
    public string LiveBodyFatText => LastMeasurement?.BodyFatPercent is null ? "" : $"{L["Live.BodyFat"]} {DisplayFormat.One(LastMeasurement.BodyFatPercent, " %")}";
    public bool ShowLiveBmi => ShowBmi && LiveBmiText.Length > 0;
    public bool ShowLiveBodyFat => ShowBodyFat && LiveBodyFatText.Length > 0;

    public string LiveDetailText
    {
        get
        {
            var m = LastMeasurement;
            if (m is null) return L["Live.Waiting"];
            var parts = new List<string> { DisplayFormat.Time(m.DisplayTime) };
            parts.Add(string.IsNullOrWhiteSpace(m.DeviceName) ? m.DeviceId : m.DeviceName!);
            if (m.UserId is { } u) parts.Add($"{L["Live.User"]} {u}");
            if (m.HeightMeters is { } h) parts.Add($"{L["Live.Height"]} {decimal.Round(h, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture)} m");
            if (m.MusclePercent is { } mp) parts.Add($"{L["Live.Muscle"]} {DisplayFormat.One(mp, " %")}");
            if (m.BodyWaterMassKg is { } w) parts.Add($"{L["Live.Water"]} {DisplayFormat.One(w, " kg")}");
            if (m.FatFreeMassKg is { } ffm) parts.Add($"{L["Live.FatFree"]} {DisplayFormat.One(ffm, " kg")}");
            if (m.BasalMetabolismKj is { } bmr) parts.Add($"{L["Live.Bmr"]} {bmr} kJ");
            if (m.ImpedanceOhm is { } z) parts.Add($"{L["Live.Impedance"]} {DisplayFormat.One(z, " Ω")}");
            return string.Join("  ·  ", parts);
        }
    }

    public string HistorySummary => L.Format("History.Summary", History.Count, _allHistory.Count);
    public bool HasHistory => _allHistory.Count > 0;

    // ---- 设置变更 ----
    partial void OnSelectedLanguageChanged(Choice<LanguageSetting> value)
    {
        ApplyLanguage();
        PersistSettings();
    }

    partial void OnSelectedUnitChanged(Choice<WeightUnit> value)
    {
        RefreshAllTexts();
        PersistSettings();
    }

    partial void OnSelectedAdapterChanged(Choice<string> value)
    {
        if (_loading) return;
        ResetAdapterState();
        OnPropertyChanged(nameof(IsDemoActive));
        OnPropertyChanged(nameof(IsFakeAdapterSelected));
        OnPropertyChanged(nameof(IsRealAdapterSelected));
        PersistSettings();
    }

    partial void OnSelectedScenarioChanged(Choice<string> value)
    {
        if (_loading) return;
        ResetAdapterState();
        OnPropertyChanged(nameof(ScenarioDescription));
        PersistSettings();
    }

    partial void OnShowBmiChanged(bool value) { OnPropertyChanged(nameof(ShowLiveBmi)); PersistSettings(); }
    partial void OnShowBodyFatChanged(bool value) { OnPropertyChanged(nameof(ShowLiveBodyFat)); PersistSettings(); }
    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanScan));
    partial void OnSelectedDeviceChanged(DeviceItem? value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnLastMeasurementChanged(Measurement? value)
    {
        foreach (var n in new[]
        {
            nameof(LiveWeightText), nameof(LiveIsDemo), nameof(HasLive), nameof(LiveBmiText), nameof(LiveBodyFatText),
            nameof(ShowLiveBmi), nameof(ShowLiveBodyFat), nameof(LiveDetailText),
        }) OnPropertyChanged(n);
    }

    private void PersistSettings()
    {
        _settings = new AppSettings
        {
            Language = SelectedLanguage.Value,
            Unit = SelectedUnit.Value,
            AdapterId = SelectedAdapter.Value,
            ScenarioId = SelectedScenario.Value,
            ShowBmi = ShowBmi,
            ShowBodyFat = ShowBodyFat,
        };
        if (_loading) return;
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus("Status.SettingsSaveFailed", ex.Message);
        }
    }

    private void ApplyLanguage()
    {
        L.SetChinese(SelectedLanguage.Value switch
        {
            LanguageSetting.Zh => true,
            LanguageSetting.En => false,
            _ => Loc.IsSystemChinese(),
        });
    }

    private void OnLocChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RefreshAllTexts();
    }

    private void RefreshAllTexts()
    {
        foreach (var c in _languageChoices) c.Refresh();
        foreach (var c in _unitChoices) c.Refresh();
        foreach (var c in _adapterChoices) c.Refresh();
        foreach (var c in _scenarioChoices) c.Refresh();
        foreach (var d in Devices) d.Refresh();
        foreach (var h in _allHistory) h.Refresh(Unit);
        foreach (var n in new[]
        {
            nameof(ConnectionText), nameof(StatusText), nameof(LiveNotice), nameof(LiveWeightText), nameof(LiveUnitText),
            nameof(LiveBmiText), nameof(LiveBodyFatText), nameof(LiveDetailText), nameof(HistorySummary), nameof(ScenarioDescription),
            nameof(ShowLiveBmi), nameof(ShowLiveBodyFat),
        }) OnPropertyChanged(n);
        ApplyFilter();
    }

    // ---- 扫描 ----
    private IBleAdapter? GetAdapter()
    {
        var key = SelectedAdapter.Value + "/" + SelectedScenario.Value;
        if (_adapter is not null && _adapterKey == key) return _adapter;
        _adapter = _adapters.Create(_settings);
        _adapterKey = key;
        return _adapter;
    }

    private void ResetAdapterState()
    {
        StopScan();
        Disconnect();
        _adapter = null;
        _adapterKey = "";
        Devices.Clear();
        SelectedDevice = null;
        LastMeasurement = null;
        SetNotice(null);
        SetStatus(null);
    }

    /// <summary>开始扫描（立即返回，扫描在后台跑；用 <see cref="StopScan"/> 停止）。</summary>
    [RelayCommand]
    public async Task StartScanAsync()
    {
        if (IsScanning || _disposed) return;
        var adapter = GetAdapter();
        if (adapter is null) { SetStatus("Status.AdapterUnsupported"); return; }
        BleAvailability availability;
        try { availability = await adapter.GetAvailabilityAsync().ConfigureAwait(true); }
        catch (BleException ex) { SetStatus("Status.ScanFailed", ex.Message); return; }
        if (availability != BleAvailability.Available)
        {
            SetStatus(availability switch
            {
                BleAvailability.PermissionDenied => "Status.BlePermission",
                BleAvailability.NotImplemented => "Status.AdapterUnsupported",
                _ => "Status.BleUnavailable",
            });
            return;
        }
        if (IsScanning) return;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsScanning = true;
        SetStatus("Status.Scanning");
        _scanTask = Task.Run(() => ScanLoopAsync(adapter, cts));
    }

    private Task _scanTask = Task.CompletedTask;
    /// <summary>仅供测试：等待后台扫描循环结束。</summary>
    internal Task ScanTask => _scanTask;

    private async Task ScanLoopAsync(IBleAdapter adapter, CancellationTokenSource cts)
    {
        try
        {
            var opts = new ScanOptions { ServiceFilter = DeviceClassifier.StandardServiceFilter };
            await foreach (var ad in adapter.ScanAsync(opts, cts.Token).ConfigureAwait(false))
                _ui.Post(() => { if (!cts.IsCancellationRequested) OnAdvertised(ad); });
        }
        catch (OperationCanceledException) { }
        catch (BleException ex)
        {
            _ui.Post(() => SetStatus(ex.Kind switch
            {
                BleErrorKind.PermissionDenied => "Status.BlePermission",
                BleErrorKind.BluetoothUnavailable => "Status.BleUnavailable",
                _ => "Status.ScanFailed",
            }, ex.Message));
        }
        finally
        {
            _ui.Post(() => { if (ReferenceEquals(_scanCts, cts)) { _scanCts = null; IsScanning = false; } cts.Dispose(); });
        }
    }

    private void OnAdvertised(AdvertisedDevice ad)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == ad.Id);
        if (existing is null) Devices.Add(new DeviceItem(ad, L));
        else existing.Update(ad);
    }

    [RelayCommand]
    public void StopScan()
    {
        var cts = _scanCts;
        if (cts is null) return;
        _scanCts = null;
        IsScanning = false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_status?.Key == "Status.Scanning") SetStatus("Status.ScanStopped");
    }

    // ---- 连接 ----
    /// <summary>连接选中的设备。已有连接进行中（含连续快速点击）时忽略。</summary>
    [RelayCommand]
    public void Connect()
    {
        if (_disposed || IsSessionActive || SelectedDevice is not { } dev) return;
        var adapter = GetAdapter();
        if (adapter is null) { SetStatus("Status.AdapterUnsupported"); return; }
        StopScan();
        var session = new ScaleSession(adapter, dev.Id, dev.Name, _clock, TimeSpan.FromSeconds(15));
        var gen = ++_sessionGeneration;
        var cts = new CancellationTokenSource();
        _sessionCts = cts;
        SetNotice(null);
        session.StateChanged += (_, c) => _ui.Post(() => { if (gen == _sessionGeneration) OnConnectionChanged(c); });
        session.MeasurementReceived += (_, m) => _ui.Post(() => { if (gen == _sessionGeneration) OnMeasurement(m); });
        session.IssueRaised += (_, i) => _ui.Post(() => { if (gen == _sessionGeneration) OnIssue(i); });
        // 同步置为「连接中」，让快速连点立即被 IsSessionActive 拦住
        SetConnection((ConnectionState.Connecting, null, null));
        _sessionTask = Task.Run(async () =>
        {
            try { await session.RunAsync(cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ui.Post(() => { if (gen == _sessionGeneration) OnConnectionChanged(new ConnectionStateChange(ConnectionState.Dropped, ex.Message, BleErrorKind.Other)); });
            }
            finally { _ui.Post(cts.Dispose); }
        });
    }

    private Task _sessionTask = Task.CompletedTask;
    /// <summary>仅供测试：等待后台会话结束。</summary>
    internal Task SessionTask => _sessionTask;

    [RelayCommand]
    public void Disconnect()
    {
        var cts = _sessionCts;
        if (cts is null) return;
        _sessionCts = null;
        _sessionGeneration++; // 之后迟到的事件作废
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        SetConnection((ConnectionState.Disconnected, null, null));
    }

    private void OnConnectionChanged(ConnectionStateChange c)
    {
        SetConnection((c.State, c.Reason, c.ErrorKind));
        if (c.State == ConnectionState.Connected) SetStatus("Status.Connected");
        if (c.State is ConnectionState.Dropped or ConnectionState.Disconnected) _sessionCts = null;
    }

    private void SetConnection((ConnectionState State, string? Reason, BleErrorKind? Kind) c)
    {
        _conn = c;
        foreach (var n in new[]
        {
            nameof(ConnectionState), nameof(IsConnected), nameof(IsConnecting), nameof(IsSessionActive), nameof(CanConnect), nameof(ConnectionText),
        }) OnPropertyChanged(n);
    }

    private void OnMeasurement(Measurement m)
    {
        var r = _ingestor.Ingest(m);
        LastMeasurement = r.Measurement;
        switch (r.Outcome)
        {
            case IngestOutcome.Saved:
                ReloadHistory();
                if (r.IsAbnormal) SetNotice("Live.Abnormal");
                else if (r.DeviceClockSuspect) SetNotice("Live.ClockSuspect");
                else SetNotice(null);
                break;
            case IngestOutcome.Duplicate:
                SetNotice("Live.Duplicate");
                break;
            case IngestOutcome.SkippedZeroWeight:
                SetNotice("Live.ZeroNotSaved");
                break;
        }
    }

    private void OnIssue(SessionIssue i)
    {
        if (i.Kind == SessionIssueKind.MeasurementUnsuccessful) SetNotice("Live.Unsuccessful");
        else SetNotice("Live.ParseError", i.ErrorCode.ToString(), i.Hex);
    }

    private void SetNotice(string? key, params object?[] args)
    {
        _notice = key is null ? null : (key, args);
        OnPropertyChanged(nameof(LiveNotice));
        OnPropertyChanged(nameof(HasLiveNotice));
    }

    private void SetStatus(string? key, params object?[] args)
    {
        _status = key is null ? null : (key, args);
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private void ToggleUnit() => SelectedUnit = _unitChoices.First(c => c.Value != Unit);

    // ---- 历史 ----
    private void ReloadHistory()
    {
        var checkedIds = _allHistory.Where(h => h.IsChecked).Select(h => h.Id).ToHashSet();
        _allHistory = _repo.List()
            .OrderByDescending(m => m.DisplayTime).ThenByDescending(m => m.Id)
            .Select(m => new HistoryItem(m, Unit, L) { IsChecked = checkedIds.Contains(m.Id) })
            .ToList();
        ApplyFilter();
        OnPropertyChanged(nameof(HasHistory));
    }

    private void ApplyFilter()
    {
        var q = FilterText.Trim();
        var rows = q.Length == 0 ? _allHistory : _allHistory.Where(h => h.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        History.Clear();
        foreach (var r in rows) History.Add(r);
        OnPropertyChanged(nameof(HistorySummary));
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var any = History.Any(h => !h.IsChecked);
        foreach (var h in History) h.IsChecked = any;
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        var ids = _allHistory.Where(h => h.IsChecked).Select(h => h.Id).ToList();
        if (ids.Count == 0) { SetStatus("Status.NothingSelected"); return; }
        if (!await _dialogs.ConfirmAsync(L["Dlg.DeleteTitle"], L.Format("Dlg.DeleteBody", ids.Count), L["Dlg.Delete"], L["Dlg.Cancel"]).ConfigureAwait(true)) return;
        var n = _repo.Delete(ids);
        _ingestor.Reload();
        ReloadHistory();
        SetStatus("Status.Deleted", n);
    }

    [RelayCommand]
    public async Task ClearHistoryAsync()
    {
        if (_allHistory.Count == 0) { SetStatus("Status.HistoryEmpty"); return; }
        if (!await _dialogs.ConfirmAsync(L["Dlg.ClearTitle"], L.Format("Dlg.ClearBody", _allHistory.Count), L["Dlg.Clear"], L["Dlg.Cancel"]).ConfigureAwait(true)) return;
        var n = _repo.Clear();
        _ingestor.Reload();
        ReloadHistory();
        SetStatus("Status.Cleared", n);
    }

    // ---- 导出（全部历史，不受过滤影响）----
    [RelayCommand]
    public Task ExportCsvAsync() => ExportAsync("csv", (path, items) => CsvExporter.WriteFile(path, items));

    [RelayCommand]
    public Task ExportJsonAsync() => ExportAsync("json", (path, items) => JsonExporter.WriteFile(path, items, _clock.GetLocalNow()));

    private async Task ExportAsync(string ext, Action<string, IReadOnlyList<Measurement>> write)
    {
        var all = _repo.List();
        if (all.Count == 0) { SetStatus("Status.ExportEmpty"); return; }
        var suggested = $"scalebridge-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.{ext}";
        var path = await _dialogs.PickSaveFileAsync(L["Dlg.ExportTitle"], suggested, ext).ConfigureAwait(true);
        if (path is null) return;
        try
        {
            write(path, all);
            SetStatus("Status.Exported", all.Count, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Status.ExportFailed", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        L.PropertyChanged -= OnLocChanged;
        StopScan();
        Disconnect();
    }
}
