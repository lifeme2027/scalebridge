using ScaleBridge.Ble.Fake;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Services;

/// <summary>
/// 按平台挑选适配器：
/// <list type="bullet">
/// <item>Fake（演示数据）：所有平台都有，永远是默认值。</item>
/// <item>"windows"：只有 Windows 版构建（net8.0-windows10.0.19041.0）才会注入真实 WinRT 适配器；实验性（仅按规范实现，未真机验证）。</item>
/// <item>Linux：BlueZ 适配器目前没有真实的 D-Bus 实现（构造函数是 internal），不接入界面，Linux 上只有 Fake。</item>
/// </list>
/// </summary>
public sealed class AdapterFactory : IAdapterFactory
{
    public const string FakeId = "fake";
    public const string WindowsId = "windows";
    public const string LinuxId = "linux";

    private readonly double _delayScale;
    private readonly Func<IBleAdapter>? _windowsFactory;

    /// <param name="delayScale">演示脚本延时倍率（--fast 时接近 0）。</param>
    /// <param name="windowsFactory">创建真实 Windows 适配器的工厂；非 Windows 构建或系统版本不够时为 null。</param>
    public AdapterFactory(double delayScale = 1.0, Func<IBleAdapter>? windowsFactory = null)
    {
        _delayScale = delayScale;
        _windowsFactory = windowsFactory;
    }

    public IReadOnlyList<string> AvailableAdapterIds =>
        _windowsFactory is null ? new[] { FakeId } : new[] { FakeId, WindowsId };

    public IBleAdapter? Create(AppSettings settings) => settings.AdapterId switch
    {
        FakeId => new FakeBleAdapter(FakeScenarioLibrary.Get(settings.ScenarioId), _delayScale),
        WindowsId => _windowsFactory?.Invoke(),
        _ => null,
    };
}
