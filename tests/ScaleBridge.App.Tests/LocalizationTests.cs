using System.Text.RegularExpressions;
using ScaleBridge.App.Localization;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.App.Tests;

public class LocalizationTests
{
    private static readonly Loc Loc = new();

    [Fact]
    public void ZhAndEnHaveTheSameKeys()
    {
        var zh = Loc.Table(true).Keys.ToHashSet();
        var en = Loc.Table(false).Keys.ToHashSet();
        Assert.Empty(zh.Except(en));
        Assert.Empty(en.Except(zh));
        Assert.True(zh.Count > 50);
    }

    [Fact]
    public void NoEmptyValuesAndPlaceholdersMatchBetweenLanguages()
    {
        foreach (var (key, zh) in Loc.Table(true))
        {
            var en = Loc.Table(false)[key];
            Assert.False(string.IsNullOrWhiteSpace(zh), $"zh '{key}' is empty");
            Assert.False(string.IsNullOrWhiteSpace(en), $"en '{key}' is empty");
            Assert.Equal(Placeholders(zh), Placeholders(en));
        }
    }

    [Fact]
    public void EnglishValuesContainNoCjk_ExceptNothing()
    {
        foreach (var (key, en) in Loc.Table(false))
            Assert.False(en.Any(c => c is >= '\u4e00' and <= '\u9fff'), $"en '{key}' contains Chinese characters");
    }

    [Fact]
    public void ChineseValuesAreActuallyTranslated()
    {
        // 大部分中文文案应含汉字（允许像 "Fake" 这样的专名混在其中）
        var untranslated = Loc.Table(true).Where(kv => !kv.Value.Any(c => c is >= '\u4e00' and <= '\u9fff')).Select(kv => kv.Key).ToList();
        Assert.Empty(untranslated);
    }

    [Fact]
    public void EveryXamlKeyExists()
    {
        var root = RepoRoot.Find();
        var keys = new HashSet<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "ScaleBridge.App"), "*.axaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{loc:T\s+([A-Za-z0-9_.]+)\}"))
                keys.Add(m.Groups[1].Value);
        Assert.NotEmpty(keys);
        foreach (var k in keys) Assert.True(Loc.Table(true).ContainsKey(k), $"missing zh key used in XAML: {k}");
    }

    [Fact]
    public void EveryViewModelKeyExists()
    {
        var root = RepoRoot.Find();
        var keys = new HashSet<string>();
        var rx = new Regex(@"(?:\bL\[|\b_loc\[|\bFormat\(|\bSetStatus\(|\bSetNotice\(|\bl\[)""([A-Z][A-Za-z]+\.[A-Za-z0-9]+)""");
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "ScaleBridge.App", "ViewModels"), "*.cs"))
            foreach (Match m in rx.Matches(File.ReadAllText(f))) keys.Add(m.Groups[1].Value);
        // 以 "Status.xxx" 形式出现在 switch 表达式里的键
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "ScaleBridge.App", "ViewModels"), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"""((?:Status|Live|Conn|Tag|Dlg|Device|Lang|Adapter)\.[A-Za-z0-9]+)"""))
                keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count > 20);
        foreach (var k in keys) Assert.True(Loc.Table(true).ContainsKey(k) && Loc.Table(false).ContainsKey(k), $"missing key used by ViewModel: {k}");
    }

    [Fact]
    public void EveryBleErrorKindHasAMessage()
    {
        foreach (var kind in Enum.GetValues<BleErrorKind>())
        {
            Assert.True(Loc.Table(true).ContainsKey("Err." + kind), $"zh Err.{kind}");
            Assert.True(Loc.Table(false).ContainsKey("Err." + kind), $"en Err.{kind}");
        }
    }

    [Fact]
    public void SwitchingLanguageRaisesIndexerChangeAndChangesText()
    {
        var loc = new Loc();
        loc.SetChinese(false);
        var raised = new List<string?>();
        loc.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("Devices", loc["Tab.Devices"]);
        loc.SetChinese(true);
        Assert.Equal("设备", loc["Tab.Devices"]);
        Assert.Contains("Item", raised);
        Assert.Contains("Item[]", raised);
        Assert.Equal("zh", loc.LanguageCode);
        Assert.Equal("Tab.Unknown.Key", loc["Tab.Unknown.Key"]);
    }

    private static string Placeholders(string s) =>
        string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x));
}
