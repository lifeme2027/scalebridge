using System.Threading.Channels;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Windows.Platform;
using CoreProps = ScaleBridge.Core.Ble.GattCharacteristicProperties;

namespace ScaleBridge.Windows;

/// <summary>
/// 基于 <see cref="IWinGattDevice"/> 的 GATT 连接。要点（均来自对 WinRT 行为的文档化陷阱，未真机验证）：
/// <list type="bullet">
/// <item>发现过的服务/特征对象必须一直持有引用，否则被 GC 后 ValueChanged 会悄悄停止。</item>
/// <item>特征值回调里的缓冲在回调返回后可能被复用，所以回调里立即拷贝；每个订阅者拿到独立的数组。</item>
/// <item>同一特征的多个订阅共用一次 CCCD 写入；最后一个订阅者退出时才写回 None。</item>
/// <item>每个 WinRT 调用都有看门狗；释放与进行中的操作互不死锁（最多等 <see cref="WindowsBleOptions.DisposeGrace"/>）。</item>
/// <item>掉线：状态只发布一次终态，所有订阅流以 <see cref="DeviceDisconnectedException"/> 结束。</item>
/// </list>
/// </summary>
internal sealed class WindowsBleConnection : IBleConnection
{
    /// <summary>本程序会去发现的服务（体重秤 0x181D、体成分 0x181B）。</summary>
    internal static readonly Guid[] DiscoverableServices =
    {
        GattUuids.WeightScaleService,
        GattUuids.BodyCompositionService,
    };

    private sealed class Subscriber
    {
        public required Channel<GattNotification> Channel { get; init; }
    }

    private sealed class CharSubscription
    {
        public required Guid Service { get; init; }
        public required Guid Characteristic { get; init; }
        public required IWinGattCharacteristic Native { get; init; }
        public required WinValueChangedHandler Handler { get; init; }
        public WinCccdValue Cccd { get; init; }
        public Subscriber[] Subscribers = Array.Empty<Subscriber>();
    }

    private readonly IWinGattDevice _device;
    private readonly WindowsBleOptions _options;
    private readonly TimeProvider _time;
    private readonly WinConnectionStateMachine _state = new(ConnectionState.Connected);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gattGate = new(1, 1);      // 串行化发现与 CCCD 读写（WinRT 同一设备上并发 GATT 操作不可靠）
    private readonly object _lock = new();
    private readonly Dictionary<(Guid, Guid), IWinGattCharacteristic> _characteristics = new();
    private readonly List<IWinGattService> _services = new();   // 只为持有引用（防 GC）并在释放时 Dispose
    private readonly Dictionary<(Guid, Guid), CharSubscription> _subscriptions = new();
    private IReadOnlyList<GattServiceInfo>? _discovered;
    private int _activeOps;
    private TaskCompletionSource? _idle;
    private bool _disposed;
    private TaskCompletionSource? _disposeDone;

    public WindowsBleConnection(string deviceId, IWinGattDevice device, WindowsBleOptions options, TimeProvider time)
    {
        DeviceId = deviceId;
        _device = device;
        _options = options;
        _time = time;
        _device.ConnectionChanged += OnDeviceConnectionChanged;
    }

    public string DeviceId { get; }

    public ConnectionState State => _state.State;

    public event EventHandler<ConnectionStateChange>? StateChanged;

    // ---------------------------------------------------------------- 发现

    public async Task<IReadOnlyList<GattServiceInfo>> DiscoverServicesAsync(CancellationToken ct = default)
    {
        using var op = BeginOp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            return await DiscoverCoreAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceDisconnectedException("The connection was closed while discovering services.");
        }
    }

    private async Task<IReadOnlyList<GattServiceInfo>> DiscoverCoreAsync(CancellationToken ct)
    {
        await _gattGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotUsable();
            if (_discovered is not null) return _discovered;

            var infos = new List<GattServiceInfo>();
            var newServices = new List<IWinGattService>();
            var newChars = new Dictionary<(Guid, Guid), IWinGattCharacteristic>();
            try
            {
                foreach (var serviceUuid in DiscoverableServices)
                {
                    var r = await WinWatchdog.RunAsync(
                        t => _device.GetServicesForUuidAsync(serviceUuid, t),
                        _options.StepTimeout, _time, "Discover services", ct,
                        onLateResult: LateServices).ConfigureAwait(false);
                    if (!r.IsSuccess) throw WinErrorMapper.FromGattStatus(r.Status, r.ProtocolError, "Discover services");
                    foreach (var service in r.Value ?? Array.Empty<IWinGattService>())
                    {
                        newServices.Add(service);
                        var cr = await WinWatchdog.RunAsync(
                            service.GetCharacteristicsAsync,
                            _options.StepTimeout, _time, "Discover characteristics", ct).ConfigureAwait(false);
                        if (!cr.IsSuccess) throw WinErrorMapper.FromGattStatus(cr.Status, cr.ProtocolError, "Discover characteristics");
                        var chars = new List<GattCharacteristicInfo>();
                        foreach (var c in cr.Value ?? Array.Empty<IWinGattCharacteristic>())
                        {
                            newChars[(service.Uuid, c.Uuid)] = c;
                            chars.Add(new GattCharacteristicInfo(c.Uuid, c.Properties));
                        }
                        infos.Add(new GattServiceInfo(service.Uuid, chars));
                    }
                }
            }
            catch
            {
                foreach (var s in newServices) SafeDispose(s);
                throw;
            }

            if (infos.Count == 0)
            {
                // 什么都没发现：不缓存，下次可重试（设备可能还没准备好）。
                return infos;
            }

            lock (_lock)
            {
                _services.AddRange(newServices);
                foreach (var kv in newChars) _characteristics[kv.Key] = kv.Value;
            }
            _discovered = infos;
            return infos;
        }
        finally
        {
            _gattGate.Release();
        }
    }

    private static void LateServices(WinGattResult<IReadOnlyList<IWinGattService>> late)
    {
        if (late.Value is null) return;
        foreach (var s in late.Value) SafeDispose(s);
    }

    private async Task<IWinGattCharacteristic> FindCharacteristicAsync(Guid service, Guid characteristic, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_characteristics.TryGetValue((service, characteristic), out var known)) return known;
        }
        await DiscoverCoreAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_characteristics.TryGetValue((service, characteristic), out var found)) return found;
        }
        throw new BleException(BleErrorKind.NoSupportedService,
            $"Characteristic {Describe(characteristic)} of service {Describe(service)} was not found on the device.");
    }

    private static string Describe(Guid uuid) =>
        GattUuids.TryGetShort(uuid) is { } s ? $"0x{s:X4}" : uuid.ToString("D");

    // ---------------------------------------------------------------- 读

    public async Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct = default)
    {
        using var op = BeginOp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var native = await FindCharacteristicAsync(service, characteristic, linked.Token).ConfigureAwait(false);
            if ((native.Properties & CoreProps.Read) == 0)
                throw new BleException(BleErrorKind.Other, $"Characteristic {Describe(characteristic)} does not support read.");
            ThrowIfNotUsable();
            var r = await WinWatchdog.RunAsync(native.ReadAsync, _options.StepTimeout, _time, "Read characteristic", linked.Token)
                .ConfigureAwait(false);
            if (!r.IsSuccess) throw WinErrorMapper.FromGattStatus(r.Status, r.ProtocolError, "Read characteristic");
            return (byte[])(r.Value ?? Array.Empty<byte>()).Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceDisconnectedException("The connection was closed while reading.");
        }
    }

    // ---------------------------------------------------------------- 订阅

    public async IAsyncEnumerable<GattNotification> SubscribeAsync(
        Guid service, Guid characteristic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var op = BeginOp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        Subscriber sub;
        CharSubscription cs;
        try
        {
            (cs, sub) = await AcquireAsync(service, characteristic, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceDisconnectedException("The connection was closed while subscribing.");
        }

        try
        {
            var reader = sub.Channel.Reader;
            while (true)
            {
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break; // 调用方取消：流正常结束
                }
                if (!more) yield break; // 主动断开/释放：流正常结束；掉线时 WaitToReadAsync 会抛 DeviceDisconnectedException
                while (reader.TryRead(out var n)) yield return n;
            }
        }
        finally
        {
            await ReleaseAsync(cs, sub).ConfigureAwait(false);
        }
    }

    private async Task<(CharSubscription, Subscriber)> AcquireAsync(Guid service, Guid characteristic, CancellationToken ct)
    {
        var native = await FindCharacteristicAsync(service, characteristic, ct).ConfigureAwait(false);
        var mode = ChooseCccd(native.Properties);

        await _gattGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotUsable();
            var sub = new Subscriber
            {
                Channel = Channel.CreateBounded<GattNotification>(new BoundedChannelOptions(Math.Max(1, _options.NotificationBuffer))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                }),
            };

            if (_subscriptions.TryGetValue((service, characteristic), out var existing))
            {
                // 重复订阅同一特征：不再写 CCCD，只多一个接收者。
                AddSubscriber(existing, sub);
                return (existing, sub);
            }

            CharSubscription? created = null;
            created = new CharSubscription
            {
                Service = service,
                Characteristic = characteristic,
                Native = native,
                Cccd = mode,
                Handler = value => Dispatch(created!, value),
            };
            created.Subscribers = new[] { sub }; // 先登记接收者再写 CCCD，避免丢掉紧跟 CCCD 写入的第一条 indication
            native.ValueChanged += created.Handler;
            try
            {
                var r = await WinWatchdog.RunAsync(t => native.WriteCccdAsync(mode, t),
                    _options.StepTimeout, _time, "Enable notifications", ct).ConfigureAwait(false);
                if (!r.IsSuccess) throw WinErrorMapper.FromGattStatus(r.Status, r.ProtocolError, "Enable notifications");
                ThrowIfNotUsable();
            }
            catch
            {
                native.ValueChanged -= created.Handler;
                sub.Channel.Writer.TryComplete();
                throw;
            }

            lock (_lock) _subscriptions[(service, characteristic)] = created;
            return (created, sub);
        }
        finally
        {
            _gattGate.Release();
        }
    }

    /// <summary>支持 Indicate 就用 Indicate（体重/体成分特征是 indicate-only），否则 Notify。</summary>
    internal static WinCccdValue ChooseCccd(CoreProps props)
    {
        if ((props & CoreProps.Indicate) != 0) return WinCccdValue.Indicate;
        if ((props & CoreProps.Notify) != 0) return WinCccdValue.Notify;
        throw new BleException(BleErrorKind.Other, "The characteristic supports neither notify nor indicate.");
    }

    private static void AddSubscriber(CharSubscription cs, Subscriber sub)
    {
        lock (cs)
            cs.Subscribers = cs.Subscribers.Append(sub).ToArray();
    }

    private void Dispatch(CharSubscription cs, ReadOnlySpan<byte> value)
    {
        // 在回调里同步拷贝：WinRT 的缓冲在回调之后可能被复用。每个订阅者一份独立数组。
        Subscriber[] subs;
        lock (cs) subs = cs.Subscribers;
        if (subs.Length == 0) return;
        var now = _time.GetLocalNow();
        foreach (var s in subs)
            s.Channel.Writer.TryWrite(new GattNotification(cs.Service, cs.Characteristic, value.ToArray(), now));
    }

    private async Task ReleaseAsync(CharSubscription cs, Subscriber sub)
    {
        sub.Channel.Writer.TryComplete();
        var last = false;
        lock (cs)
        {
            cs.Subscribers = cs.Subscribers.Where(s => s != sub).ToArray();
            last = cs.Subscribers.Length == 0;
        }
        if (!last) return;

        // 最后一个接收者：摘事件，尽力写回 CCCD = None（连接已不可用或正在释放则跳过）。
        var gotGate = false;
        try
        {
            using var graceCts = new CancellationTokenSource(_options.DisposeGrace, _time);
            await _gattGate.WaitAsync(graceCts.Token).ConfigureAwait(false);
            gotGate = true;
        }
        catch (OperationCanceledException) { /* 等不到闸门：强制继续收尾 */ }
        try
        {
            bool stillOurs;
            lock (_lock)
            {
                stillOurs = _subscriptions.TryGetValue((cs.Service, cs.Characteristic), out var cur) && ReferenceEquals(cur, cs);
                bool again;
                lock (cs) again = cs.Subscribers.Length > 0;
                if (again) return; // 等锁期间又有人订阅了
                if (stillOurs) _subscriptions.Remove((cs.Service, cs.Characteristic));
            }
            cs.Native.ValueChanged -= cs.Handler;
            if (stillOurs && !_state.IsTerminal && !Volatile.Read(ref _disposed))
            {
                try
                {
                    await WinWatchdog.RunAsync(t => cs.Native.WriteCccdAsync(WinCccdValue.None, t),
                        _options.DisposeGrace, _time, "Disable notifications", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* 尽力而为 */ }
            }
        }
        finally
        {
            if (gotGate) _gattGate.Release();
        }
    }

    // ---------------------------------------------------------------- 掉线 / 断开 / 释放

    private void OnDeviceConnectionChanged(bool connected)
    {
        if (connected) return; // 掉线后系统重新连上不会复活本对象：ScaleSession 重连时会新建连接
        var change = _state.TryMarkDropped("The device disconnected (connection status changed).");
        if (change is null) return;
        Publish(change);
        FailAllSubscribers(new DeviceDisconnectedException("The device disconnected."));
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void FailAllSubscribers(Exception error)
    {
        CharSubscription[] all;
        lock (_lock) all = _subscriptions.Values.ToArray();
        foreach (var cs in all)
        {
            Subscriber[] subs;
            lock (cs) subs = cs.Subscribers;
            foreach (var s in subs) s.Channel.Writer.TryComplete(error);
        }
    }

    private void CompleteAllSubscribers()
    {
        CharSubscription[] all;
        lock (_lock) all = _subscriptions.Values.ToArray();
        foreach (var cs in all)
        {
            Subscriber[] subs;
            lock (cs) subs = cs.Subscribers;
            foreach (var s in subs) s.Channel.Writer.TryComplete();
        }
    }

    private void Publish(ConnectionStateChange change)
    {
        var handler = StateChanged;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<ConnectionStateChange>)d)(this, change); }
            catch { /* 订阅者的异常不能影响连接 */ }
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        // 幂等：第一个调用者负责拆除，其余调用者（含并发）等待同一个结果。拆除过程不抛异常。
        TaskCompletionSource done;
        var first = false;
        lock (_lock)
        {
            _disposed = true;
            if (_disposeDone is null)
            {
                _disposeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first = true;
            }
            done = _disposeDone;
        }
        if (first)
        {
            try { await TeardownAsync().ConfigureAwait(false); }
            catch { /* 拆除尽力而为 */ }
            finally { done.TrySetResult(); }
        }
        await done.Task.ConfigureAwait(false);
    }

    private async Task TeardownAsync()
    {
        var change = _state.TryMarkDisconnected("Disconnected by the app.");
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        CompleteAllSubscribers();
        if (change is not null) Publish(change);

        // 给进行中的操作/订阅一点收尾时间，但不无限等（WinRT 调用可能卡住）。
        Task idle;
        lock (_lock)
        {
            if (_activeOps == 0) idle = Task.CompletedTask;
            else idle = (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        try { await idle.WaitAsync(_options.DisposeGrace, _time, CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }

        _device.ConnectionChanged -= OnDeviceConnectionChanged;
        CharSubscription[] subs;
        IWinGattService[] services;
        lock (_lock)
        {
            subs = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
            services = _services.ToArray();
            _services.Clear();
            _characteristics.Clear();
        }
        foreach (var cs in subs) cs.Native.ValueChanged -= cs.Handler;
        foreach (var s in services) SafeDispose(s);
        SafeDispose(_device); // 同时释放 GattSession 与 BluetoothLEDevice（外壳保证）
        _lifetime.Dispose();
    }

    // ---------------------------------------------------------------- 辅助

    private void ThrowIfNotUsable()
    {
        if (Volatile.Read(ref _disposed))
            throw new DeviceDisconnectedException("The connection was closed.");
        switch (_state.State)
        {
            case ConnectionState.Dropped:
                throw new DeviceDisconnectedException("The device disconnected.");
            case ConnectionState.Disconnected:
                throw new DeviceDisconnectedException("The connection was closed.");
        }
    }

    private IDisposable BeginOp()
    {
        lock (_lock)
        {
            if (_disposed) throw new DeviceDisconnectedException("The connection was closed.");
            _activeOps++;
        }
        return new OpScope(this);
    }

    private void EndOp()
    {
        TaskCompletionSource? idle = null;
        lock (_lock)
        {
            _activeOps--;
            if (_activeOps == 0) { idle = _idle; _idle = null; }
        }
        idle?.TrySetResult();
    }

    private sealed class OpScope : IDisposable
    {
        private WindowsBleConnection? _owner;
        public OpScope(WindowsBleConnection owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOp();
    }

    private static void SafeDispose(IDisposable d)
    {
        try { d.Dispose(); } catch { /* 释放失败无法补救 */ }
    }
}
