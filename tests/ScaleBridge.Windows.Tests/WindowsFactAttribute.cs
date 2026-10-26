namespace ScaleBridge.Windows.Tests;

/// <summary>非 Windows 或系统版本不够时跳过（双保险；主要的跳过由 csproj 的 IsTestProject 条件完成）。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Skip = "需要 Windows 10 build 19041+";
        }
    }
}
