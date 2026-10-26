using System.Runtime.InteropServices;
using Avalonia;
using ScaleBridge.Ble.Fake;

namespace ScaleBridge.App;

internal static class Program
{
    internal static CommandLineOptions Options { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Options = CommandLineOptions.Parse(args);
        // Windows 下 WinExe 没有控制台：从命令行启动时接上父进程的控制台，--help / --list-scenarios / --smoke 的输出才看得到
        if (OperatingSystem.IsWindows() && (Options.Help || Options.Version || Options.ListScenarios || Options.Smoke || Options.Error is not null || Options.Scenario is not null))
            AttachConsole(ParentProcess);
        if (Options.Error is not null)
        {
            Console.Error.WriteLine(Options.Error);
            Console.Error.WriteLine(CommandLineOptions.UsageText);
            return 2;
        }
        if (Options.Version) { Console.WriteLine(AppInfo.VersionText()); return 0; }
        if (Options.Help) { Console.WriteLine(CommandLineOptions.UsageText); return 0; }
        if (Options.ListScenarios)
        {
            foreach (var s in FakeScenarioLibrary.All) Console.WriteLine($"{s.Id}\t{s.Title.En} / {s.Title.Zh}");
            return 0;
        }
        if (Options.Scenario is not null && FakeScenarioLibrary.Find(Options.Scenario) is null)
        {
            Console.Error.WriteLine($"unknown scenario '{Options.Scenario}'. Known: {string.Join(", ", FakeScenarioLibrary.All.Select(s => s.Id))}");
            return 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private const int ParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
