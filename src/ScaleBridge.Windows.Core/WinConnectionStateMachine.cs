using ScaleBridge.Core.Ble;

namespace ScaleBridge.Windows;

/// <summary>
/// 连接状态机：Connecting → Connected → (Dropped | Disconnected)。Dropped 与 Disconnected 是终态，
/// 每次转换只返回一次 <see cref="ConnectionStateChange"/>（重复的掉线事件、释放后的迟到事件都返回 null），
/// 这样 StateChanged 事件不会重复发布终态。线程安全。
/// </summary>
internal sealed class WinConnectionStateMachine
{
    private readonly object _gate = new();
    private ConnectionState _state;

    public WinConnectionStateMachine(ConnectionState initial = ConnectionState.Connected) => _state = initial;

    public ConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsTerminal
    {
        get { lock (_gate) return _state is ConnectionState.Dropped or ConnectionState.Disconnected; }
    }

    /// <summary>Connecting → Connected。</summary>
    public ConnectionStateChange? TryMarkConnected()
    {
        lock (_gate)
        {
            if (_state != ConnectionState.Connecting) return null;
            _state = ConnectionState.Connected;
            return new ConnectionStateChange(ConnectionState.Connected);
        }
    }

    /// <summary>Connecting/Connected → Dropped（非主动断开）。已是终态则返回 null。</summary>
    public ConnectionStateChange? TryMarkDropped(string reason, BleErrorKind kind = BleErrorKind.DeviceDisconnected)
    {
        lock (_gate)
        {
            if (_state is ConnectionState.Dropped or ConnectionState.Disconnected) return null;
            _state = ConnectionState.Dropped;
            return new ConnectionStateChange(ConnectionState.Dropped, reason, kind);
        }
    }

    /// <summary>Connecting/Connected → Disconnected（主动断开/释放）。已是终态则返回 null。</summary>
    public ConnectionStateChange? TryMarkDisconnected(string? reason = null)
    {
        lock (_gate)
        {
            if (_state is ConnectionState.Dropped or ConnectionState.Disconnected) return null;
            _state = ConnectionState.Disconnected;
            return new ConnectionStateChange(ConnectionState.Disconnected, reason);
        }
    }
}
