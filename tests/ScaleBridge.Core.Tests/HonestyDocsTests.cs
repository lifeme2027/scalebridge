using System.Text.RegularExpressions;

namespace ScaleBridge.Core.Tests;

/// <summary>
/// 诚实红线的自动检查：文档和界面资源里不能出现「支持小米/Renpho/芯海…」「已真机验证」之类的说法，
/// 并且 README/NOTES 必须带标准的「实验性（仅按规范实现，未真机验证）」标签、README 必须提到「演示数据」。
/// 没有任何设备在真机上验证过，所以这些说法永远不该出现；确有一天验证了，再来改这里并写明验证记录。
/// </summary>
public class HonestyDocsTests
{
    public const string ExperimentalLabel = "实验性（仅按规范实现，未真机验证）";

    // 「不支持」「未支持」「没有支持」「暂不支持」都不算宣称；英文 "not support"/"no support"/"unsupported" 同理
    private static readonly Regex[] ForbiddenClaims =
    {
        new(@"(?<![不未没无])支持\s*(?:的)?\s*(?:小米|Xiaomi|Renpho|云麦|Chipsea|芯海|OKOK|Mi\s*Scale|华为|荣耀)", RegexOptions.IgnoreCase),
        new(@"(?<!not )(?<!no )(?<!un)\bsupports?\s+(?:Xiaomi|Renpho|Chipsea|OKOK|Mi\s*Scale|Huawei)\b", RegexOptions.IgnoreCase),
        new(@"(?<![未没尚不])已(?:经)?(?:在)?真机验证"),
        new(@"(?<!not )(?<!un)\bverified on (?:real )?hardware\b", RegexOptions.IgnoreCase),
        new(@"(?<![未没不])(?:已|经)\s*验证(?:通过)?的(?:设备|型号|秤)"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScaleBridge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ScaleBridge.sln not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> UserFacingFiles()
    {
        var root = RepoRoot();
        var skip = new[] { "node_modules", $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}" };
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (skip.Any(s => f.Contains(s))) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".md" or ".axaml" or ".resx" or ".xml" or ".html" or ".txt") yield return f;
        }
    }

    [Theory]
    [InlineData("本程序支持小米体重秤", true)]
    [InlineData("支持 Xiaomi 秤", true)]
    [InlineData("ScaleBridge supports Xiaomi scales", true)]
    [InlineData("已真机验证", true)]
    [InlineData("Verified on real hardware", true)]
    [InlineData("当前不支持小米", false)]
    [InlineData("尚未支持 Renpho", false)]
    [InlineData("Does not support Xiaomi", false)]
    [InlineData("not verified on real hardware", false)]
    [InlineData("没有任何真机验证", false)]
    [InlineData("未真机验证", false)]
    [InlineData("支持标准 Weight Scale 协议（实验性）", false)]
    public void Guard_patterns_detect_claims_and_allow_negations(string text, bool isClaim)
        => Assert.Equal(isClaim, ForbiddenClaims.Any(r => r.IsMatch(text)));

    [Fact]
    public void Docs_and_ui_resources_make_no_unverified_support_claims()
    {
        var files = UserFacingFiles().ToList();
        Assert.Contains(files, f => f.EndsWith("README.md"));
        Assert.Contains(files, f => f.EndsWith("NOTES.md"));
        var offenders = new List<string>();
        foreach (var f in files)
        {
            var n = 0;
            foreach (var line in File.ReadLines(f))
            {
                n++;
                foreach (var r in ForbiddenClaims)
                    if (r.IsMatch(line)) offenders.Add($"{Path.GetRelativePath(RepoRoot(), f)}:{n}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "Unverified 'support/verified' claims found:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("README.md", ExperimentalLabel)]
    [InlineData("README.md", "演示数据")]
    [InlineData("NOTES.md", ExperimentalLabel)]
    [InlineData("NOTES.md", "演示数据")]
    public void Top_level_docs_carry_the_required_labels(string file, string mustContain)
        => Assert.Contains(mustContain, File.ReadAllText(Path.Combine(RepoRoot(), file)));
}
