using ScaleBridge.App;
using ScaleBridge.Ble.Fake;

namespace ScaleBridge.App.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void NoArgsIsAllDefaults()
    {
        var o = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.Null(o.Scenario); Assert.Null(o.Error); Assert.Null(o.Language); Assert.Null(o.Tab);
        Assert.False(o.Fast); Assert.False(o.Smoke); Assert.False(o.Help); Assert.False(o.ListScenarios); Assert.False(o.Autoplay);
    }

    [Fact]
    public void ParsesAllOptions()
    {
        var o = CommandLineOptions.Parse(new[] { "--scenario", "imperial", "--fast", "--smoke", "--lang", "zh", "--tab", "2", "--autoplay", "--list-scenarios", "--help" });
        Assert.Equal("imperial", o.Scenario);
        Assert.True(o.Fast); Assert.True(o.Smoke); Assert.True(o.Autoplay); Assert.True(o.ListScenarios); Assert.True(o.Help);
        Assert.Equal("zh", o.Language);
        Assert.Equal(2, o.Tab);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("--scenario")]
    [InlineData("--lang")]
    [InlineData("--tab")]
    public void MissingValueIsAnError(string opt) => Assert.NotNull(CommandLineOptions.Parse(new[] { opt }).Error);

    [Theory]
    [InlineData("--lang", "fr")]
    [InlineData("--tab", "4")]
    [InlineData("--tab", "x")]
    [InlineData("--tab", "-1")]
    public void BadValueIsAnError(string opt, string value) => Assert.NotNull(CommandLineOptions.Parse(new[] { opt, value }).Error);

    [Fact]
    public void UnknownArgumentIsAnError() => Assert.Contains("--bogus", CommandLineOptions.Parse(new[] { "--bogus" }).Error);

    [Fact]
    public void UsageMentionsEveryOption()
    {
        foreach (var opt in new[] { "--scenario", "--list-scenarios", "--fast", "--smoke", "--autoplay", "--tab", "--lang", "--help", "SCALEBRIDGE_DATA_DIR" })
            Assert.Contains(opt, CommandLineOptions.UsageText);
    }

    [Fact]
    public void AllNineScenariosExistForTheCommandLine()
    {
        var ids = FakeScenarioLibrary.All.Select(s => s.Id).ToHashSet();
        foreach (var id in new[] { "normal-si", "imperial", "timestamp-user-bmi-height", "body-composition", "invalid-0xffff", "truncated-packet", "mid-connection-disconnect", "multi-device", "reconnect" })
            Assert.Contains(id, ids);
    }
}
