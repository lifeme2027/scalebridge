using ScaleBridge.Core.Ble;

namespace ScaleBridge.Windows;

/// <summary>
/// WinRT 的异步操作可能永远不返回（对端不响应、驱动卡住），所以每个 GATT 调用都要套看门狗。
/// 超时后：取消传给操作的令牌（尽力），抛 <see cref="BleTimeoutException"/>，并保证操作之后才完成的结果被清理
/// （例如迟到的 BluetoothLEDevice 要 Dispose，否则会一直占着连接）。
/// </summary>
internal static class WinWatchdog
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        TimeProvider time,
        string what,
        CancellationToken ct,
        Action<T>? onLateResult = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "超时必须为正。");
        ct.ThrowIfCancellationRequested();

        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<T> task;
        try
        {
            task = operation(opCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WinErrorMapper.FromException(ex, what);
        }

        try
        {
            return await task.WaitAsync(timeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            // 先取消，再挂清理：迟到的成功结果交给 onLateResult，迟到的异常只观察不抛。
            try { opCts.Cancel(); } catch (ObjectDisposedException) { }
            _ = task.ContinueWith(static (t, state) =>
            {
                if (t.IsFaulted) _ = t.Exception;
                else if (t.IsCompletedSuccessfully && state is Action<T> cleanup)
                {
                    try { cleanup(t.Result); } catch { /* 清理失败不可再抛 */ }
                }
            }, onLateResult, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new BleTimeoutException($"{what}: no response within {timeout.TotalSeconds:0.#} s.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WinErrorMapper.FromException(ex, what);
        }
    }

    public static Task RunVoidAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        TimeProvider time,
        string what,
        CancellationToken ct) =>
        RunAsync<bool>(async t => { await operation(t).ConfigureAwait(false); return true; }, timeout, time, what, ct);
}
