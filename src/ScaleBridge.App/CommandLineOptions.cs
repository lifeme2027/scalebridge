using ScaleBridge.App.ViewModels;

namespace ScaleBridge.App;

/// <summary>
/// 命令行：<c>--scenario &lt;id&gt;</c> 选择演示场景（并切到 Fake 适配器）；<c>--list-scenarios</c> 列出场景后退出；
/// <c>--fast</c> 演示脚本几乎不等待；<c>--smoke</c> 无人值守端到端自检（扫描→连接→收到数据→写入历史）后打印 SCALEBRIDGE_SMOKE_OK 并退出；
/// <c>--lang zh|en</c> 覆盖语言；<c>--version</c> 打印版本与构建提交后退出；<c>--help</c>。
/// </summary>
public sealed record CommandLineOptions
{
    public string? Scenario { get; init; }
    public bool ListScenarios { get; init; }
    public bool Fast { get; init; }
    public bool Smoke { get; init; }
    public bool Help { get; init; }
    public bool Version { get; init; }
    public bool Autoplay { get; init; }
    public int? Tab { get; init; }
    /// <summary>扫描超时（秒）。null = 用默认值；0 = 不限时。</summary>
    public int? ScanTimeoutSeconds { get; init; }
    public string? Language { get; init; }
    public string? Error { get; init; }

    public const string UsageText =
        "ScaleBridge — desktop Bluetooth weight-scale receiver (phase 1: demo data only)\n" +
        "  --scenario <id>    use the Fake demo adapter with this scenario\n" +
        "  --list-scenarios   print demo scenario ids and exit\n" +
        "  --fast             demo scripts run (almost) without delays\n" +
        "  --smoke            unattended self-check, prints SCALEBRIDGE_SMOKE_OK and exits\n" +
        "  --autoplay         after start, scan + connect the first demo device automatically (keeps running)\n" +
        "  --tab 0-3          start on this tab (0 devices, 1 live, 2 history, 3 settings)\n" +
        "  --scan-timeout <s> stop scanning after this many seconds (default 30, 3 with --fast; 0 = never)\n" +
        "  --lang zh|en       override UI language\n" +
        "  --version          print version and build commit (unknown when not injected) and exit\n" +
        "  --help             this text\n" +
        "Env: SCALEBRIDGE_DATA_DIR overrides the data directory; SCALEBRIDGE_SCAN_TIMEOUT sets the scan timeout in seconds.";

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CommandLineOptions();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    if (i + 1 >= args.Count) return o with { Error = "--scenario needs a value" };
                    o = o with { Scenario = args[++i] };
                    break;
                case "--lang":
                    if (i + 1 >= args.Count || args[i + 1] is not ("zh" or "en")) return o with { Error = "--lang needs zh or en" };
                    o = o with { Language = args[++i] };
                    break;
                case "--list-scenarios": o = o with { ListScenarios = true }; break;
                case "--autoplay": o = o with { Autoplay = true }; break;
                case "--tab":
                    if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out var tab) || tab is < 0 or > 3) return o with { Error = "--tab needs 0..3" };
                    o = o with { Tab = tab };
                    i++;
                    break;
                case "--scan-timeout":
                    if (i + 1 >= args.Count || !TryParseSeconds(args[i + 1], out var secs)) return o with { Error = "--scan-timeout needs a whole number of seconds, 0..3600 (0 = never)" };
                    o = o with { ScanTimeoutSeconds = secs };
                    i++;
                    break;
                case "--fast": o = o with { Fast = true }; break;
                case "--smoke": o = o with { Smoke = true }; break;
                case "--version": o = o with { Version = true }; break;
                case "-h": case "--help": o = o with { Help = true }; break;
                default: return o with { Error = $"unknown argument '{args[i]}'" };
            }
        }
        return o;
    }

    private static bool TryParseSeconds(string text, out int seconds) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds <= 3600;

    /// <summary>
    /// 扫描超时的取值顺序：命令行 <c>--scan-timeout</c> → 环境变量 <c>SCALEBRIDGE_SCAN_TIMEOUT</c>（秒，非法值忽略）→ 默认
    /// （30 秒；演示加速模式 <c>--fast/--smoke/--autoplay</c> 下 3 秒）。0 表示不限时。
    /// </summary>
    public TimeSpan ResolveScanTimeout(string? envValue)
    {
        if (ScanTimeoutSeconds is { } s) return TimeSpan.FromSeconds(s);
        if (TryParseSeconds(envValue?.Trim() ?? "", out var e)) return TimeSpan.FromSeconds(e);
        return Fast || Smoke || Autoplay ? TimeSpan.FromSeconds(3) : MainViewModel.DefaultScanTimeout;
    }
}
