using System.Diagnostics;
using ScaleBridge.App.Services;

namespace ScaleBridge.App.Tests;

public class TerminationSignalsTests
{
    [Fact]
    public async Task Sigterm_is_turned_into_an_exit_request_instead_of_killing_the_process()
    {
        if (!OperatingSystem.IsLinux()) return; // 用 kill(1) 给自己发信号；Windows 没有等价的、不会杀掉测试宿主的做法

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = TerminationSignals.Register(() => fired.TrySetResult());

        using var kill = Process.Start("kill", new[] { "-TERM", Environment.ProcessId.ToString() })!;
        await kill.WaitForExitAsync();

        var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(fired.Task, winner); // 能走到这里本身就说明进程没有被 SIGTERM 杀死
    }

    [Fact]
    public void Register_and_dispose_are_safe_on_every_platform()
    {
        TerminationSignals.Register(() => { }).Dispose();
    }
}
