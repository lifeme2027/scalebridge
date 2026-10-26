using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace ScaleBridge.Linux.Tests.Support;

/// <summary>起一个私有 dbus-daemon（--session 配置，临时套接字）并在上面挂 <see cref="FakeBluezService"/>。</summary>
internal sealed class BluezDbusHarness : IAsyncDisposable
{
    private readonly Process _daemon;
    private readonly DBusConnection _serviceConnection;

    private BluezDbusHarness(Process daemon, string address, DBusConnection serviceConnection, FakeBluezService service)
    {
        _daemon = daemon;
        Address = address;
        _serviceConnection = serviceConnection;
        Service = service;
    }

    public string Address { get; }

    public FakeBluezService Service { get; }

    public DBusConnection ServiceConnection => _serviceConnection;

    /// <summary>仅 Linux 且 PATH 里有 dbus-daemon 时可用（Windows/macOS CI 上相关测试会被跳过）。</summary>
    public static bool IsAvailable => FindDaemon() is not null;

    public static async Task<BluezDbusHarness> StartAsync()
    {
        var path = FindDaemon() ?? throw new InvalidOperationException("dbus-daemon not found");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("--session");
        info.ArgumentList.Add("--nofork");
        info.ArgumentList.Add("--print-address=1");
        info.ArgumentList.Add("--address=unix:tmpdir=" + Path.GetTempPath().TrimEnd('/'));

        var daemon = Process.Start(info) ?? throw new InvalidOperationException("cannot start dbus-daemon");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var address = (await daemon.StandardOutput.ReadLineAsync(cts.Token))?.Trim();
            if (string.IsNullOrEmpty(address))
                throw new InvalidOperationException("dbus-daemon printed no address");

            var service = new FakeBluezService();
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(service);
            await connection.RequestNameAsync("org.bluez", RequestNameOptions.Default);
            return new BluezDbusHarness(daemon, address, connection, service);
        }
        catch
        {
            Kill(daemon);
            throw;
        }
    }

    public void KillDaemon() => Kill(_daemon);

    public ValueTask DisposeAsync()
    {
        try
        {
            _serviceConnection.Dispose();
        }
        catch
        {
            // best effort
        }

        Kill(_daemon);
        _daemon.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string? FindDaemon()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "dbus-daemon");
            if (File.Exists(candidate))
                return candidate;
        }

        return File.Exists("/usr/bin/dbus-daemon") ? "/usr/bin/dbus-daemon" : null;
    }
}

/// <summary>需要真实 dbus-daemon 的测试；不可用时标记为跳过而不是通过。</summary>
internal sealed class DbusFactAttribute : FactAttribute
{
    public DbusFactAttribute()
    {
        if (!BluezDbusHarness.IsAvailable)
            Skip = "dbus-daemon is not available on this machine";
    }
}
