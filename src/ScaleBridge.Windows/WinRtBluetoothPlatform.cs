using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows.WinRt;

/// <summary>
/// <see cref="IWinBluetoothPlatform"/> 的 WinRT 实现。实验性（仅按规范实现，未真机验证）。
/// 依据：BluetoothAdapter / Radio / BluetoothLEDevice 的 Microsoft 官方文档（见 NOTES.md「Windows 适配器」）。
/// </summary>
internal sealed class WinRtBluetoothPlatform : IWinBluetoothPlatform
{
    public async Task<WinRadioState> GetRadioStateAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
        if (adapter is null) return WinRadioState.NoAdapter;
        if (!adapter.IsLowEnergySupported) return WinRadioState.LowEnergyNotSupported;

        // 只读状态，不调用 Radio.RequestAccessAsync（那会弹系统授权/打扰用户）。
        var radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
        return radio is null ? WinRadioState.Unknown : WinRtMapping.MapRadio(radio.State);
    }

    public IWinAdvertisementWatcher CreateWatcher() => new WinRtAdvertisementWatcher();

    public Task<IWinGattDevice?> OpenDeviceAsync(ulong address, CancellationToken ct) => WinRtGattDevice.OpenAsync(address, ct);
}
