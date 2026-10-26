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
    private int _sessionMeasurements;
    private int _scanSeen;
    private (ConnectionState State, string? Reason, BleErrorKind? Kind) _conn = (ConnectionState.Disconnected, null, null);
    // 历史：数据库保留全部记录；界面只持有「最新 N 条 + 用户点“加载更多”追加的更旧记录」（N = AppSettings.HistoryWindow）。
    // 收到新记录时按显示时间增量插入，不重读数据库。_allHistory 始终按显示时间倒序（与仓库一致）。
    private List<HistoryItem> _allHistory = new();
    private int _totalHistory;

    public MainViewModel(IMeasurementRepository repo, ISettingsStore settingsStore, IDialogService dialogs, IUiDispatcher ui,
        IAdapterFactory adapters, Loc loc, string dataDirectory, AppSettings? initialSettings = null, TimeProvider? clock = null,
        TimeSpan? scanTimeout = null)
    {
        ScanTimeout = scanTimeout ?? DefaultScanTimeout;
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
        LoadHistory();
        _loading = false;
    }

    /// <summary>扫描默认最长持续时间；到点自动停止并给出结果提示（找到 N 台 / 未发现设备）。</summary>
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(30);

    /// <summary>本次运行的扫描超时；<see cref="TimeSpan.Zero"/>（或更小）表示不限时，一直扫到用户停止。</summary>
    public TimeSpan ScanTimeout { get; }

    public Loc L { get; }
    public string DataDirectory { get; }
    /// <summary>「关于」一行：版本与构建提交（没有注入提交时是 unknown，与 --version 一致）。</summary>
    public string AboutText => L.Format("Set.About", AppInfo.Version, AppInfo.Commit);
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
            // 断开原因只用词典里的文字（Err.<Kind>），整句同一种语言；设备/系统给出的原始英文原因放在 ConnectionDetail（悬停提示），不拼进这句话。
            var kind = L["Err." + (_conn.Kind ?? BleErrorKind.Other)];
            return $"{name}{(L.IsChinese ? "：" : ": ")}{kind}";
        }
    }

    /// <summary>断开的原始技术原因（来自设备或系统，语言不受控），仅作悬停提示，null 表示没有。</summary>
    public string? ConnectionDetail =>
        _conn.State == ConnectionState.Dropped && !string.IsNullOrWhiteSpace(_conn.Reason) ? L.Format("Conn.Detail", _conn.Reason) : null;

    /// <summary>
    /// 状态栏文字。优先显示一次性消息（导出结果、错误等，连接状态一变化就清掉）；没有消息时完全由当前连接/扫描状态推出，
    /// 所以不可能出现「已连接」而实际已断开的情况。
    /// </summary>
    public string StatusText
    {
        get
        {
            if (_status is { } st) return L.Format(st.Key, st.Args.Select(a => a is BleErrorKind k ? L["Err." + k] : a).ToArray());
            if (IsConnecting) return L["Conn.Connecting"];
            if (IsScanning) return L["Status.Scanning"];
            return _conn.State switch
            {
                ConnectionState.Connected => _sessionMeasurements == 0 ? L["Status.Connected"] : L.Format("Status.Receiving", _sessionMeasurements),
                ConnectionState.Dropped => L["Status.Dropped"],
                _ => "",
            };
        }
    }

    /// <summary>扫描失败时的原始技术原因（语言不受控），仅作悬停提示。</summary>
    public string? StatusDetail => _statusDetail is { Length: > 0 } d ? L.Format("Conn.Detail", d) : null;
    private string? _statusDetail;

    /// <summary>上一次扫描正常结束且一台设备都没发现：设备页显示「未发现设备」提示和「重新扫描」。</summary>
    public bool ScanNoDevice { get; private set; }
    public string LiveNotice => _notice is { } n ? L.Format(n.Key, n.Args) : "";
    public bool HasLiveNotice => _notice is not null;

    public string LiveWeightText => DisplayFormat.Weight(LastMeasurement?.WeightKg, Unit);
    public string LiveUnitText => DisplayFormat.UnitName(Unit);
    public bool LiveIsDemo => LastMeasurement?.IsDemo == true;
    public bool HasLive => LastMeasurement is not null;

    // 有读数时，缺失的字段一律显示「—」，而不是整项消失；还没有读数时显示等待提示。
    public string LiveBmiText => LastMeasurement is null ? "" : $"BMI {DisplayFormat.One(LastMeasurement.Bmi)}";
    public string LiveBodyFatText => LastMeasurement is null ? "" : $"{L["Live.BodyFat"]} {DisplayFormat.One(LastMeasurement.BodyFatPercent, " %")}";
    public bool ShowLiveBmi => ShowBmi && HasLive;
    public bool ShowLiveBodyFat => ShowBodyFat && HasLive;

    public string LiveDetailText
    {
        get
        {
            var m = LastMeasurement;
            if (m is null) return L["Live.Waiting"];
            const string Dash = "—";
            var height = m.HeightMeters is { } h
                ? decimal.Round(h, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture) + " m"
                : Dash;
            return string.Join("  ·  ", new[]
            {
                DisplayFormat.Time(m.DisplayTime),
                string.IsNullOrWhiteSpace(m.DeviceName) ? m.DeviceId : m.DeviceName!,
                $"{L["Live.User"]} {(m.UserId is { } u ? u.ToString(CultureInfo.InvariantCulture) : Dash)}",
                $"{L["Live.Height"]} {height}",
                $"{L["Live.Muscle"]} {DisplayFormat.One(m.MusclePercent, " %")}",
                $"{L["Live.Water"]} {DisplayFormat.One(m.BodyWaterMassKg, " kg")}",
                $"{L["Live.FatFree"]} {DisplayFormat.One(m.FatFreeMassKg, " kg")}",
                $"{L["Live.Bmr"]} {(m.BasalMetabolismKj is { } bmr ? bmr.ToString(CultureInfo.InvariantCulture) + " kJ" : Dash)}",
                $"{L["Live.Impedance"]} {DisplayFormat.One(m.ImpedanceOhm, " Ω")}",
            });
        }
    }

    public string HistorySummary => HasMoreHistory
        ? L.Format("History.SummaryWindow", History.Count, _totalHistory, _allHistory.Count)
        : L.Format("History.Summary", History.Count, _allHistory.Count);
    public bool HasHistory => _totalHistory > 0;
    /// <summary>数据库里还有更旧的、尚未加载到界面的记录。</summary>
    public bool HasMoreHistory => _allHistory.Count < _totalHistory;

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
    partial void OnIsScanningChanged(bool value) { OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(StatusText)); }
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
            nameof(ConnectionText), nameof(ConnectionDetail), nameof(StatusText), nameof(StatusDetail), nameof(AboutText), nameof(LiveNotice), nameof(LiveWeightText), nameof(LiveUnitText),
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
        SetScanNoDevice(false);
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
        catch (BleException ex) { SetStatusWithDetail("Status.ScanFailed", ex.Message, ex.Kind); return; }
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
        if (ScanTimeout > TimeSpan.Zero) cts.CancelAfter(ScanTimeout);
        _scanCts = cts;
        _scanSeen = 0;
        SetScanNoDevice(false);
        SetStatus(null);
        IsScanning = true;
        _scanTask = Task.Run(() => ScanLoopAsync(adapter, cts));
    }

    private Task _scanTask = Task.CompletedTask;
    /// <summary>仅供测试：等待后台扫描循环结束。</summary>
    internal Task ScanTask => _scanTask;

    private async Task ScanLoopAsync(IBleAdapter adapter, CancellationTokenSource cts)
    {
        var failed = false;
        try
        {
            var opts = new ScanOptions { ServiceFilter = DeviceClassifier.StandardServiceFilter };
            await foreach (var ad in adapter.ScanAsync(opts, cts.Token).ConfigureAwait(false))
                _ui.Post(() => { if (!cts.IsCancellationRequested) OnAdvertised(ad); });
        }
        catch (OperationCanceledException) { }
        catch (BleException ex)
        {
            failed = true;
            _ui.Post(() => SetStatusWithDetail(ex.Kind switch
            {
                BleErrorKind.PermissionDenied => "Status.BlePermission",
                BleErrorKind.BluetoothUnavailable => "Status.BleUnavailable",
                _ => "Status.ScanFailed",
            }, ex.Message, ex.Kind));
        }
        finally
        {
            _ui.Post(() =>
            {
                // _scanCts 仍是这一次：说明不是用户点了停止，而是到时间了（或适配器自己扫完了）→ 给出结果提示。
                if (ReferenceEquals(_scanCts, cts))
                {
                    _scanCts = null;
                    IsScanning = false;
                    if (!failed) OnScanFinished();
                }
                cts.Dispose();
            });
        }
    }

    private void OnScanFinished()
    {
        if (_scanSeen == 0)
        {
            SetScanNoDevice(true);
            SetStatus("Status.NoDeviceFound");
        }
        else SetStatus("Status.ScanFinished", _scanSeen);
    }

    private void SetScanNoDevice(bool value)
    {
        if (ScanNoDevice == value) return;
        ScanNoDevice = value;
        OnPropertyChanged(nameof(ScanNoDevice));
    }

    private void OnAdvertised(AdvertisedDevice ad)
    {
        _scanSeen++;
        SetScanNoDevice(false);
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
        SetStatus("Status.ScanStopped");
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
        _sessionMeasurements = 0;
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
        if (c.State is ConnectionState.Dropped or ConnectionState.Disconnected) _sessionCts = null;
    }

    private void SetConnection((ConnectionState State, string? Reason, BleErrorKind? Kind) c)
    {
        _conn = c;
        // 连接状态一变，之前的一次性状态消息（含上一次连接遗留的）就过期了；状态栏改由连接状态本身推出。
        _status = null;
        _statusDetail = null;
        foreach (var n in new[]
        {
            nameof(ConnectionState), nameof(IsConnected), nameof(IsConnecting), nameof(IsSessionActive), nameof(CanConnect),
            nameof(ConnectionText), nameof(ConnectionDetail), nameof(StatusText), nameof(StatusDetail),
        }) OnPropertyChanged(n);
    }

    private void OnMeasurement(Measurement m)
    {
        var r = _ingestor.Ingest(m);
        _sessionMeasurements++;
        OnPropertyChanged(nameof(StatusText));
        LastMeasurement = r.Measurement;
        switch (r.Outcome)
        {
            case IngestOutcome.Saved:
                InsertHistory(r.Measurement);
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

    private void SetStatus(string? key, params object?[] args) => SetStatusWithDetail(key, null, args);

    /// <summary>设一条状态消息，并附带原始技术原因（只用于悬停提示，不进正文）。</summary>
    private void SetStatusWithDetail(string? key, string? detail, params object?[] args)
    {
        _status = key is null ? null : (key, args);
        _statusDetail = key is null ? null : detail;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
    }

    [RelayCommand]
    private void ToggleUnit() => SelectedUnit = _unitChoices.First(c => c.Value != Unit);

    // ---- 历史 ----
    private int HistoryWindow => AppSettings.NormalizeHistoryWindow(_settings.HistoryWindow);

    private HistoryItem NewItem(Measurement m, bool isChecked = false) => new(m, Unit, L) { IsChecked = isChecked };

    private void LoadHistory()
    {
        _totalHistory = _repo.Count();
        _allHistory = _repo.ListPage(HistoryWindow).Select(m => NewItem(m)).ToList();
        ApplyFilter();
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasMoreHistory));
        OnPropertyChanged(nameof(HistorySummary));
    }

    /// <summary>a 在倒序列表里应排在 b 之前（显示时间更晚；相同则主键更大）。</summary>
    private static bool PlacedBefore(Measurement a, Measurement b) =>
        a.DisplayTime > b.DisplayTime || (a.DisplayTime == b.DisplayTime && a.Id > b.Id);

    /// <summary>倒序列表里 m 应插入的位置（二分，O(log n)）。</summary>
    private static int InsertIndex(IList<HistoryItem> list, Measurement m)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (PlacedBefore(list[mid].Measurement, m)) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private bool MatchesFilter(HistoryItem h)
    {
        var q = FilterText.Trim();
        return q.Length == 0 || h.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>新记录增量插入。比已加载窗口里最旧一条还旧、且库里有未加载记录时，它属于「更旧的未加载部分」，不显示（点加载更多会出现）。</summary>
    private void InsertHistory(Measurement m)
    {
        var hadUnloaded = HasMoreHistory;
        _totalHistory++;
        var idx = InsertIndex(_allHistory, m);
        if (idx == _allHistory.Count && hadUnloaded) { NotifyHistoryChanged(); return; }
        var item = NewItem(m);
        _allHistory.Insert(idx, item);
        if (MatchesFilter(item)) History.Insert(InsertIndex(History, m), item);
        NotifyHistoryChanged();
    }

    [RelayCommand]
    private void LoadMoreHistory()
    {
        var more = _repo.ListPage(HistoryWindow, _allHistory.LastOrDefault()?.Measurement);
        AppendHistory(more);
    }

    private void AppendHistory(IReadOnlyList<Measurement> more)
    {
        foreach (var m in more)
        {
            var item = NewItem(m);
            _allHistory.Add(item);
            if (MatchesFilter(item)) History.Add(item);
        }
        NotifyHistoryChanged();
    }

    /// <summary>删除后：从界面移除这些行，并用更旧的记录把窗口补满（不重读已加载的部分）。</summary>
    private void RemoveHistory(ISet<long> ids)
    {
        _allHistory.RemoveAll(h => ids.Contains(h.Id));
        for (var i = History.Count - 1; i >= 0; i--)
            if (ids.Contains(History[i].Id)) History.RemoveAt(i);
        _totalHistory = _repo.Count();
        var missing = HistoryWindow - _allHistory.Count;
        if (missing > 0 && HasMoreHistory) AppendHistory(_repo.ListPage(missing, _allHistory.LastOrDefault()?.Measurement));
        NotifyHistoryChanged();
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
        RemoveHistory(ids.ToHashSet());
        SetStatus("Status.Deleted", n);
    }

    [RelayCommand]
    public async Task ClearHistoryAsync()
    {
        if (_totalHistory == 0) { SetStatus("Status.HistoryEmpty"); return; }
        if (!await _dialogs.ConfirmAsync(L["Dlg.ClearTitle"], L.Format("Dlg.ClearBody", _totalHistory), L["Dlg.Clear"], L["Dlg.Cancel"]).ConfigureAwait(true)) return;
        var n = _repo.Clear();
        _allHistory = new();
        History.Clear();
        _totalHistory = 0;
        NotifyHistoryChanged();
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
