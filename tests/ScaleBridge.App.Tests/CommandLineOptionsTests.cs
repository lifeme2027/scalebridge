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
        Assert.False(o.Fast); Assert.False(o.Smoke); Assert.False(o.Help); Assert.False(o.ListScenarios); Assert.False(o.Autoplay); Assert.False(o.Version);
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
        foreach (var opt in new[] { "--scenario", "--list-scenarios", "--fast", "--smoke", "--autoplay", "--tab", "--lang", "--version", "--help", "SCALEBRIDGE_DATA_DIR" })
            Assert.Contains(opt, CommandLineOptions.UsageText);
    }

    [Fact]
    public void AllNineScenariosExistForTheCommandLine()
    {
        var ids = FakeScenarioLibrary.All.Select(s => s.Id).ToHashSet();
        foreach (var id in new[] { "normal-si", "imperial", "timestamp-user-bmi-height", "body-composition", "invalid-0xffff", "truncated-packet", "mid-connection-disconnect", "multi-device", "reconnect" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void VersionFlagIsParsedAlone() => Assert.True(CommandLineOptions.Parse(new[] { "--version" }) is { Version: true, Error: null });

    [Fact]
    public void ScanTimeout_ParsesSeconds_AndZeroMeansNever()
    {
        Assert.Equal(45, CommandLineOptions.Parse(new[] { "--scan-timeout", "45" }).ScanTimeoutSeconds);
        Assert.Equal(0, CommandLineOptions.Parse(new[] { "--scan-timeout", "0" }).ScanTimeoutSeconds);
        Assert.Null(CommandLineOptions.Parse(Array.Empty<string>()).ScanTimeoutSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("3601")]
    [InlineData("99999999999")]
    public void ScanTimeout_RejectsBadValues(string value)
    {
        Assert.NotNull(CommandLineOptions.Parse(new[] { "--scan-timeout", value }).Error);
    }

    [Fact]
    public void ScanTimeout_MissingValueIsAnError() => Assert.NotNull(CommandLineOptions.Parse(new[] { "--scan-timeout" }).Error);

    [Fact]
    public void ScanTimeout_Resolution_CliThenEnvThenDefault()
    {
        var plain = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.Equal(TimeSpan.FromSeconds(30), plain.ResolveScanTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(30), plain.ResolveScanTimeout("garbage"));   // 非法环境变量忽略
        Assert.Equal(TimeSpan.FromSeconds(12), plain.ResolveScanTimeout("12"));
        Assert.Equal(TimeSpan.FromSeconds(30), plain.ResolveScanTimeout("-5"));
        Assert.Equal(TimeSpan.Zero, plain.ResolveScanTimeout("0"));
        // 命令行优先于环境变量
        var cli = CommandLineOptions.Parse(new[] { "--scan-timeout", "7" });
        Assert.Equal(TimeSpan.FromSeconds(7), cli.ResolveScanTimeout("12"));
        // 演示加速模式下默认更短（3 秒），但显式设置仍然优先
        Assert.Equal(TimeSpan.FromSeconds(3), CommandLineOptions.Parse(new[] { "--fast" }).ResolveScanTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(3), CommandLineOptions.Parse(new[] { "--smoke" }).ResolveScanTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(20), CommandLineOptions.Parse(new[] { "--fast", "--scan-timeout", "20" }).ResolveScanTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(9), CommandLineOptions.Parse(new[] { "--fast" }).ResolveScanTimeout("9"));
    }

    [Fact]
    public void UsageMentionsScanTimeout() => Assert.Contains("--scan-timeout", CommandLineOptions.UsageText);
}
