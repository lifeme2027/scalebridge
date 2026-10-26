using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.WinRt;

namespace ScaleBridge.Windows;

/// <summary>Windows 真实蓝牙适配器的入口。<b>实验性（仅按规范实现，未真机验证）</b>。</summary>
public static class WindowsBle
{
    /// <summary>当前系统是否可能使用 WinRT 蓝牙（Windows 10 1903 / build 19041 及以上）。</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public const string ExperimentalLabel = "实验性（仅按规范实现，未真机验证）";

    /// <summary>创建适配器。创建本身不访问蓝牙；真正的 WinRT 调用发生在扫描/连接时。</summary>
    public static IBleAdapter CreateAdapter(WindowsBleOptions? options = null) =>
        new WindowsBleAdapter(new WinRtBluetoothPlatform(), options);
}
