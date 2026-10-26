using System.Text.RegularExpressions;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Tests;

public class AppInfoTests
{
    private const string Sha = "f196baf4f9510805eee3571105f09305e9360671";

    [Fact]
    public void Commit_after_plus_is_reported_as_is()
    {
        Assert.Equal(("0.1.0", Sha), AppInfo.Parse("0.1.0+" + Sha));
        Assert.Equal("ScaleBridge 0.1.0\ncommit: " + Sha, AppInfo.VersionText("0.1.0+" + Sha));
    }

    [Theory]
    [InlineData("0.1.0")]                 // 没有注入（比如没有 .git 的源码压缩包）
    [InlineData("0.1.0+")]
    [InlineData("0.1.0+not-a-sha")]       // 形状不对的不当提交
    [InlineData("0.1.0+abc")]             // 太短
    [InlineData("0.1.0+f196baf4f9510805eee3571105f09305e93606710")] // 41 位
    public void Missing_or_malformed_commit_is_unknown_never_invented(string raw)
    {
        var (version, commit) = AppInfo.Parse(raw);
        Assert.Equal("0.1.0", version);
        Assert.Equal("unknown", commit);
        Assert.EndsWith("commit: unknown", AppInfo.VersionText(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_version_attribute_at_all_is_unknown(string? raw) => Assert.Equal(("unknown", "unknown"), AppInfo.Parse(raw));

    [Fact]
    public void Built_assembly_has_version_and_either_a_real_sha_or_unknown()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);
        Assert.Matches(new Regex("^([0-9a-f]{7,40}|unknown)$"), AppInfo.Commit);
        // 在 git 检出里构建（CI 与开发机）时 SDK 必须已经把提交写进去了。
        if (Directory.Exists(Path.Combine(RepoRoot.Find(), ".git")))
            Assert.NotEqual("unknown", AppInfo.Commit);
    }

    [Theory]
    [InlineData(LanguageSetting.En, "About: ScaleBridge")]
    [InlineData(LanguageSetting.Zh, "关于：ScaleBridge")]
    public void Settings_about_line_shows_version_and_commit_in_both_languages(LanguageSetting lang, string prefix)
    {
        using var app = new TestApp(lang: lang);
        var text = app.Read(() => app.Vm.AboutText);
        Assert.StartsWith(prefix, text);
        Assert.Contains(AppInfo.Version, text);
        Assert.Contains(AppInfo.Commit, text);
    }
}
