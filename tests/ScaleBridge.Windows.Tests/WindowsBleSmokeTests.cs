using ScaleBridge.Core.Ble;

namespace ScaleBridge.Windows.Tests;

/// <summary>
/// 不需要蓝牙硬件的冒烟测试：在没有蓝牙的 CI 机器上，适配器必须「优雅地」报告不可用，而不是崩溃或卡死。
/// 在有蓝牙的机器上它们同样应当通过（不连接任何设备）。
/// </summary>
public class WindowsBleSmokeTests
{
    [WindowsFact]
    public void IsSupported_OnWindowsRunner() => Assert.True(WindowsBle.IsSupported);

    [WindowsFact]
    public void CreateAdapter_DoesNotTouchBluetooth_AndIsNotDemo()
    {
        var adapter = WindowsBle.CreateAdapter();
        Assert.Equal("windows", adapter.Id);
        Assert.False(adapter.IsDemo);
    }

    [WindowsFact]
    public async Task Availability_CompletesWithKnownValue_WithoutThrowing()
    {
        var adapter = WindowsBle.CreateAdapter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var availability = await adapter.GetAvailabilityAsync(cts.Token);
        Assert.True(Enum.IsDefined(availability));
        Assert.NotEqual(BleAvailability.NotImplemented, availability);
    }

    [WindowsFact]
    public async Task Scan_WhenUnavailable_ThrowsFriendlyBleException_OtherwiseEndsOnCancel()
    {
        var adapter = WindowsBle.CreateAdapter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (await adapter.GetAvailabilityAsync(cts.Token) != BleAvailability.Available)
        {
            var ex = await Assert.ThrowsAnyAsync<BleException>(async () =>
            {
                await foreach (var _ in adapter.ScanAsync(new ScanOptions(), cts.Token)) { }
            });
            Assert.True(ex.Kind is BleErrorKind.BluetoothUnavailable or BleErrorKind.PermissionDenied);
            return;
        }

        using var scan = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in adapter.ScanAsync(new ScanOptions(), scan.Token)) { }
    }

    [WindowsFact]
    public async Task Connect_InvalidId_FailsWithoutBluetooth()
    {
        var adapter = WindowsBle.CreateAdapter();
        await Assert.ThrowsAsync<BleException>(() => adapter.ConnectAsync("not-an-address"));
    }
}
