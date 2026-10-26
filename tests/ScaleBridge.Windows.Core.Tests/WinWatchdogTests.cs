using System.Runtime.InteropServices;
using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Core.Tests.Support;

namespace ScaleBridge.Windows.Core.Tests;

public class WinWatchdogTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(9);

    [Fact]
    public async Task CompletesNormally_BeforeTimeout()
    {
        var time = new ManualTimeProvider();
        var r = await WinWatchdog.RunAsync(_ => Task.FromResult(42), Limit, time, "op", default);
        Assert.Equal(42, r);
    }

    [Fact]
    public async Task HungOperation_ThrowsBleTimeout_AfterConfiguredTime()
    {
        var time = new ManualTimeProvider();
        var tcs = new TaskCompletionSource<int>();
        var run = WinWatchdog.RunAsync(_ => tcs.Task, Limit, time, "Connect", default);
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(8));
        Assert.False(run.IsCompleted, "8 秒时还不应超时");
        time.Advance(TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<BleTimeoutException>(() => TestData.Within(run));
        Assert.Contains("Connect", ex.Message);
    }

    [Fact]
    public async Task Timeout_CancelsTheTokenGivenToTheOperation()
    {
        var time = new ManualTimeProvider();
        CancellationToken seen = default;
        var tcs = new TaskCompletionSource<int>();
        var run = WinWatchdog.RunAsync(t => { seen = t; return tcs.Task; }, Limit, time, "op", default);
        await time.AdvanceWhenArmedAsync(Limit);
        await Assert.ThrowsAsync<BleTimeoutException>(() => TestData.Within(run));
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task LateSuccessfulResult_IsHandedToCleanup()
    {
        var time = new ManualTimeProvider();
        var tcs = new TaskCompletionSource<FakeDevice>();
        var cleaned = new TaskCompletionSource<FakeDevice>();
        var run = WinWatchdog.RunAsync(_ => tcs.Task, Limit, time, "Connect", default, d => cleaned.TrySetResult(d));
        await time.AdvanceWhenArmedAsync(Limit);
        await Assert.ThrowsAsync<BleTimeoutException>(() => TestData.Within(run));
        var device = new FakeDevice();
        tcs.SetResult(device);
        Assert.Same(device, await TestData.Within(cleaned.Task));
    }

    [Fact]
    public async Task LateFault_IsObserved_NotUnhandled()
    {
        var time = new ManualTimeProvider();
        var tcs = new TaskCompletionSource<int>();
        var run = WinWatchdog.RunAsync(_ => tcs.Task, Limit, time, "op", default);
        await time.AdvanceWhenArmedAsync(Limit);
        await Assert.ThrowsAsync<BleTimeoutException>(() => TestData.Within(run));
        tcs.SetException(new InvalidOperationException("late"));
        GC.Collect(); GC.WaitForPendingFinalizers(); // 若异常未被观察，这里会触发 UnobservedTaskException（本测试不应崩溃）
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource<int>();
        var run = WinWatchdog.RunAsync(_ => tcs.Task, Limit, time, "op", cts.Token);
        await time.AdvanceWhenArmedAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestData.Within(run));
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsWithoutStartingOperation()
    {
        var started = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WinWatchdog.RunAsync(_ => { started = true; return Task.FromResult(1); }, Limit, TimeProvider.System, "op", cts.Token));
        Assert.False(started);
    }

    [Fact]
    public async Task SynchronousThrow_IsMapped()
    {
        var ex = await Assert.ThrowsAsync<BlePermissionException>(() =>
            WinWatchdog.RunAsync<int>(_ => throw new UnauthorizedAccessException(), Limit, TimeProvider.System, "op", default));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task FaultedOperation_IsMapped_ByHResult()
    {
        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() =>
            WinWatchdog.RunAsync<int>(_ => Task.FromException<int>(new COMException("x", WinErrorMapper.ErrorDeviceNotConnected)),
                Limit, TimeProvider.System, "op", default));
        Assert.NotNull(ex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveTimeout_IsRejected(int seconds) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WinWatchdog.RunAsync(_ => Task.FromResult(1), TimeSpan.FromSeconds(seconds), TimeProvider.System, "op", default));
}
