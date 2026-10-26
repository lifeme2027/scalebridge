namespace ScaleBridge.Windows.Core.Tests.Support;

/// <summary>手动推进的时间源：测试超时/看门狗时不真的等待，也不会因 CI 机器慢而抖动。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Armed); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        lock (_gate) _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Armed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) { _now = target; return; }
                if (next.Due > _now) _now = next.Due;
            }
            next.Fire();
        }
    }

    /// <summary>等到有定时器被挂上（被测代码已进入等待），再推进时间。</summary>
    public async Task AdvanceWhenArmedAsync(TimeSpan by, int minTimers = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (PendingTimers < minTimers)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("被测代码没有挂上定时器。");
            await Task.Delay(1);
        }
        Advance(by);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Armed { get; private set; }
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan) { Armed = false; return true; }
                Due = _owner._now + dueTime;
                Armed = true;
                return true;
            }
        }

        public void Fire()
        {
            lock (_owner._gate)
            {
                if (!Armed) return;
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero) Armed = false;
                else Due += _period;
            }
            _callback(_state);
        }

        public void Dispose()
        {
            lock (_owner._gate) { Armed = false; _owner._timers.Remove(this); }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
