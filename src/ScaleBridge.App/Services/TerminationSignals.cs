using System.Runtime.InteropServices;

namespace ScaleBridge.App.Services;

/// <summary>
/// 把 SIGTERM / SIGINT / SIGHUP（Windows 上是 Ctrl+C / Ctrl+Break / 关闭事件）转成一次「正常退出」请求：
/// 取消运行时的默认终止（否则进程以 143/130 之类被信号杀死、不经过 Exit 事件，连接和数据库都没有收尾），
/// 改为回调 <paramref name="requestExit"/>，由调用方走 Avalonia 的 Shutdown(0)，从而释放 ViewModel（断开设备、停止扫描）。
/// 回调可能在任意线程上执行，且每个信号都会触发；调用方负责幂等。
/// </summary>
public static class TerminationSignals
{
    public static IDisposable Register(Action requestExit)
    {
        var regs = new List<PosixSignalRegistration>();
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            try
            {
                regs.Add(PosixSignalRegistration.Create(sig, ctx =>
                {
                    ctx.Cancel = true;
                    requestExit();
                }));
            }
            catch (PlatformNotSupportedException) { /* 该平台没有这个信号：忽略 */ }
        }
        return new Registrations(regs);
    }

    private sealed class Registrations(List<PosixSignalRegistration> regs) : IDisposable
    {
        public void Dispose()
        {
            foreach (var r in regs) r.Dispose();
            regs.Clear();
        }
    }
}
