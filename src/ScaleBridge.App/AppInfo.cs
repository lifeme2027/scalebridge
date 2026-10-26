using System.Reflection;
using System.Text.RegularExpressions;

namespace ScaleBridge.App;

/// <summary>
/// 版本与构建提交。版本号来自 Directory.Build.props 的 Version；提交 SHA 由 .NET SDK 在构建时写进
/// AssemblyInformationalVersion（形如 <c>0.1.0+&lt;40 位 SHA&gt;</c>，取自构建时的 git 检出；也可用 MSBuild 属性 SourceRevisionId 显式指定）。
/// 没有注入（例如从没有 .git 的源码压缩包构建）时提交显示 <see cref="Unknown"/>，不猜、不伪造。
/// </summary>
public static class AppInfo
{
    public const string Unknown = "unknown";

    // SDK 写入的是十六进制 SHA（7~40 位）；不符合这个形状的内容一律当作没有注入。
    private static readonly Regex CommitPattern = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Version { get; } = Parse(Raw()).Version;
    public static string Commit { get; } = Parse(Raw()).Commit;

    private static string? Raw() =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>把 informational version 拆成（版本，提交）。提交缺失或形状不对时为 <see cref="Unknown"/>。</summary>
    public static (string Version, string Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return (Unknown, Unknown);
        var s = informationalVersion.Trim();
        var plus = s.IndexOf('+');
        if (plus < 0) return (s, Unknown);
        var version = plus == 0 ? Unknown : s[..plus];
        var commit = s[(plus + 1)..];
        return (version, CommitPattern.IsMatch(commit) ? commit.ToLowerInvariant() : Unknown);
    }

    /// <summary><c>--version</c> 的输出（两行）。</summary>
    public static string VersionText(string? informationalVersion = null)
    {
        var (v, c) = informationalVersion is null ? (Version, Commit) : Parse(informationalVersion);
        return $"ScaleBridge {v}\ncommit: {c}";
    }
}
