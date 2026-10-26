namespace ScaleBridge.Data;

/// <summary>应用数据目录。只在本机，不上传任何地方。</summary>
public static class AppPaths
{
    /// <summary>环境变量：覆盖数据目录（测试与 CI 冒烟用）。</summary>
    public const string DataDirEnvVar = "SCALEBRIDGE_DATA_DIR";

    /// <summary>Windows：%LOCALAPPDATA%\ScaleBridge；Linux：$XDG_DATA_HOME/ScaleBridge（默认 ~/.local/share/ScaleBridge）。</summary>
    public static string DataDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable(DataDirEnvVar);
            var dir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScaleBridge");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "scalebridge.db");
}
