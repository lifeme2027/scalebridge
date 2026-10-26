using ScaleBridge.Core.Ble;

namespace ScaleBridge.App.Services;

/// <summary>编译期平台开关：只有 Windows 目标框架的构建才引用 ScaleBridge.Windows。</summary>
internal static class PlatformAdapters
{
    /// <summary>真实 Windows 蓝牙适配器工厂；本构建/本系统不支持时返回 null（界面上就不会出现这个选项）。</summary>
    public static Func<IBleAdapter>? WindowsFactory()
    {
#if WINDOWS
        return ScaleBridge.Windows.WindowsBle.IsSupported ? () => ScaleBridge.Windows.WindowsBle.CreateAdapter() : null;
#else
        return null;
#endif
    }
}
