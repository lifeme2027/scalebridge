using ScaleBridge.Ble.Fake;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Services;

/// <summary>
/// 阶段 1 只有 Fake 适配器。"windows" / "linux" 在设置里可见但标注「阶段 2」，选了也只会得到「尚未实现」提示，
/// 不会假装在扫描。
/// </summary>
public sealed class AdapterFactory : IAdapterFactory
{
    public const string FakeId = "fake";
    public const string WindowsId = "windows";
    public const string LinuxId = "linux";

    private readonly double _delayScale;

    /// <param name="delayScale">演示脚本延时倍率（--fast 时接近 0）。</param>
    public AdapterFactory(double delayScale = 1.0) => _delayScale = delayScale;

    public IBleAdapter? Create(AppSettings settings) => settings.AdapterId switch
    {
        FakeId => new FakeBleAdapter(FakeScenarioLibrary.Get(settings.ScenarioId), _delayScale),
        _ => null,
    };
}
