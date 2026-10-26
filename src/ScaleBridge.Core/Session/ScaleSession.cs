using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Session;

public enum SessionIssueKind
{
    /// <summary>收到的包无法解析（过短/截断/多包暂不支持）。会话继续接收后续数据。</summary>
    ParseError,
    /// <summary>设备报告「测量不成功」(0xFFFF)，不保存记录。</summary>
    MeasurementUnsuccessful,
}

public sealed record SessionIssue(SessionIssueKind Kind, Guid Characteristic, ParseErrorCode ErrorCode, string Message, string Hex, DateTimeOffset At);

/// <summary>
/// 一次与一台设备的会话：连接 → 发现服务 → 订阅 0x2A9D / 0x2A9C 的 indicate → 解析 → 产出 <see cref="Measurement"/>。
/// 与 UI/线程无关：事件在后台线程触发，调用方自行切回 UI 线程。
/// <see cref="RunAsync"/> 在「用户取消」或「连接结束（失败/掉线）」时返回，只有未预期的程序错误才会抛出。
/// 注意：WSS(0x181D) 与 BCS(0x181B) 的测量分别产出独立记录，本阶段不做合并（见 NOTES）。
/// </summary>
public sealed class ScaleSession
{
    private readonly IBleAdapter _adapter;
    private readonly string _deviceId;
    private readonly string? _deviceName;
    private readonly TimeProvider _clock;
    private readonly TimeSpan? _connectTimeout;
    private readonly object _gate = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    public ScaleSession(IBleAdapter adapter, string deviceId, string? deviceName, TimeProvider? clock = null, TimeSpan? connectTimeout = null)
    {
        _adapter = adapter;
        _deviceId = deviceId;
        _deviceName = deviceName;
        _clock = clock ?? TimeProvider.System;
        _connectTimeout = connectTimeout;
    }

    public event EventHandler<ConnectionStateChange>? StateChanged;
    public event EventHandler<Measurement>? MeasurementReceived;
    public event EventHandler<SessionIssue>? IssueRaised;

    public ConnectionState State { get { lock (_gate) return _state; } }

    /// <summary>读到的 Weight Scale Feature（若设备提供且可解析）。</summary>
    public WeightScaleFeature? WeightFeature { get; private set; }

    /// <summary>读到的 Body Composition Feature。</summary>
    public BodyCompositionFeature? BodyCompositionFeature { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        IBleConnection? conn = null;
        try
        {
            Raise(new ConnectionStateChange(ConnectionState.Connecting));
            conn = await _adapter.ConnectAsync(_deviceId, _connectTimeout, ct).ConfigureAwait(false);
            var services = await conn.DiscoverServicesAsync(ct).ConfigureAwait(false);

            var subs = new List<(Guid Service, Guid Char)>();
            if (CanSubscribe(services, GattUuids.WeightScaleService, GattUuids.WeightMeasurement))
                subs.Add((GattUuids.WeightScaleService, GattUuids.WeightMeasurement));
            if (CanSubscribe(services, GattUuids.BodyCompositionService, GattUuids.BodyCompositionMeasurement))
                subs.Add((GattUuids.BodyCompositionService, GattUuids.BodyCompositionMeasurement));
            if (subs.Count == 0)
                throw new BleException(BleErrorKind.NoSupportedService,
                    "The device exposes neither Weight Scale (0x181D) nor Body Composition (0x181B) measurement characteristics.");

            await ReadFeaturesAsync(conn, services, ct).ConfigureAwait(false);

            Raise(new ConnectionStateChange(ConnectionState.Connected));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loops = subs.Select(s => Task.Run(() => PumpAsync(conn, s.Service, s.Char, linked.Token), CancellationToken.None)).ToList();
            var first = await Task.WhenAny(loops).ConfigureAwait(false);
            // 一路订阅结束（对端断开）时，另一路特征上已经收到、还在缓冲里的通知要先处理完，不能随手取消把它们丢掉。
            // 用户主动取消（ct）时没有这个问题：linked 随 ct 一起取消，立即退出。
            if (!ct.IsCancellationRequested) await DrainSiblingsAsync(loops, ct).ConfigureAwait(false);
            linked.Cancel();
            try { await first.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            foreach (var l in loops)
            {
                try { await l.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (BleException) { }
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            // 订阅流在未被取消的情况下正常结束 = 对端关闭了连接
            throw new DeviceDisconnectedException("The notification stream ended.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Raise(new ConnectionStateChange(ConnectionState.Disconnected));
        }
        catch (BleException ex)
        {
            Raise(new ConnectionStateChange(ConnectionState.Dropped, ex.Message, ex.Kind));
        }
        finally
        {
            if (conn is not null)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* 释放失败不影响结果 */ }
            }
        }
    }

    /// <summary>另一路订阅流不会自己结束（连接实际还活着）时，最多再等这么久，之后照常判定为断开。</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(1500);

    private async Task DrainSiblingsAsync(IReadOnlyList<Task> loops, CancellationToken ct)
    {
        var pending = loops.Where(l => !l.IsCompleted).ToList();
        if (pending.Count == 0) return;
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(DrainGrace, _clock, grace.Token);
        var all = Task.WhenAll(pending.Select(AwaitQuietlyAsync));
        try { await Task.WhenAny(all, timer).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        grace.Cancel();
        try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task AwaitQuietlyAsync(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (BleException) { }
    }

    private static bool CanSubscribe(IReadOnlyList<GattServiceInfo> services, Guid service, Guid characteristic)
    {
        var c = services.FirstOrDefault(x => x.Uuid == service)?.Characteristics.FirstOrDefault(x => x.Uuid == characteristic);
        return c is not null && (c.Properties & (GattCharacteristicProperties.Indicate | GattCharacteristicProperties.Notify)) != 0;
    }

    private async Task ReadFeaturesAsync(IBleConnection conn, IReadOnlyList<GattServiceInfo> services, CancellationToken ct)
    {
        bool Readable(Guid svc, Guid ch) =>
            services.Any(s => s.Uuid == svc && s.Characteristics.Any(c => c.Uuid == ch && (c.Properties & GattCharacteristicProperties.Read) != 0));
        try
        {
            if (Readable(GattUuids.WeightScaleService, GattUuids.WeightScaleFeature))
            {
                var r = WeightScaleParser.ParseFeature(await conn.ReadAsync(GattUuids.WeightScaleService, GattUuids.WeightScaleFeature, ct).ConfigureAwait(false));
                if (r.IsSuccess) WeightFeature = r.Value;
            }
            if (Readable(GattUuids.BodyCompositionService, GattUuids.BodyCompositionFeature))
            {
                var r = BodyCompositionParser.ParseFeature(await conn.ReadAsync(GattUuids.BodyCompositionService, GattUuids.BodyCompositionFeature, ct).ConfigureAwait(false));
                if (r.IsSuccess) BodyCompositionFeature = r.Value;
            }
        }
        catch (BleException ex) when (ex.Kind is not (BleErrorKind.DeviceDisconnected or BleErrorKind.Timeout))
        {
            // Feature 是可选的辅助信息：读取失败（如权限/不支持）不影响接收测量。
        }
    }

    private async Task PumpAsync(IBleConnection conn, Guid service, Guid characteristic, CancellationToken ct)
    {
        await foreach (var n in conn.SubscribeAsync(service, characteristic, ct).WithCancellation(ct).ConfigureAwait(false))
            Handle(characteristic, n);
    }

    private void Handle(Guid characteristic, GattNotification n)
    {
        var device = new DeviceContext(_deviceId, _deviceName, _adapter.IsDemo);
        var now = _clock.GetLocalNow();
        var hex = Convert.ToHexString(n.Value);
        Measurement? m;
        if (characteristic == GattUuids.WeightMeasurement)
        {
            var r = WeightScaleParser.ParseMeasurement(n.Value);
            if (!r.IsSuccess) { Issue(SessionIssueKind.ParseError, characteristic, r.ErrorCode, r.ErrorMessage ?? "", hex, now); return; }
            m = MeasurementFactory.FromWeightScale(r.Value!, device, now);
        }
        else
        {
            var r = BodyCompositionParser.ParseMeasurement(n.Value);
            if (!r.IsSuccess) { Issue(SessionIssueKind.ParseError, characteristic, r.ErrorCode, r.ErrorMessage ?? "", hex, now); return; }
            m = MeasurementFactory.FromBodyComposition(r.Value!, device, now);
        }
        if (m is null)
        {
            Issue(SessionIssueKind.MeasurementUnsuccessful, characteristic, ParseErrorCode.None, "Measurement unsuccessful (0xFFFF).", hex, now);
            return;
        }
        MeasurementReceived?.Invoke(this, m);
    }

    private void Issue(SessionIssueKind kind, Guid ch, ParseErrorCode code, string msg, string hex, DateTimeOffset at) =>
        IssueRaised?.Invoke(this, new SessionIssue(kind, ch, code, msg, hex, at));

    private void Raise(ConnectionStateChange change)
    {
        lock (_gate) _state = change.State;
        StateChanged?.Invoke(this, change);
    }
}
