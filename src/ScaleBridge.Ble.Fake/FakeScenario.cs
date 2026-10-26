using System.Reflection;
using System.Text.Json;

namespace ScaleBridge.Ble.Fake;

/// <summary>多语言文本。</summary>
public sealed class LocalizedText
{
    public string Zh { get; init; } = "";
    public string En { get; init; } = "";
    public string Get(bool chinese) => chinese ? Zh : En;
}

/// <summary>
/// 一个演示场景（回放脚本）。JSON 格式 <c>scalebridge-scenario/1</c>，内嵌在程序集资源里。
/// 所有数据都是演示数据，不来自任何真实设备。
/// </summary>
public sealed class FakeScenario
{
    public string Schema { get; init; } = "";
    public string Id { get; init; } = "";
    public LocalizedText Title { get; init; } = new();
    public LocalizedText Description { get; init; } = new();
    public List<FakeDevice> Devices { get; init; } = new();
}

public sealed class FakeDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Rssi { get; init; } = -60;
    /// <summary>扫描开始后多少毫秒该设备才出现。</summary>
    public int AppearAfterMs { get; init; }
    /// <summary>广播的服务 UUID（"181D" 或完整 UUID）。</summary>
    public List<string> Services { get; init; } = new();
    /// <summary>厂商数据：公司标识（"0xFFFF"）→ 十六进制载荷。</summary>
    public Dictionary<string, string> ManufacturerData { get; init; } = new();
    /// <summary>可读的 Feature 特征：特征 UUID（"2A9E"）→ 十六进制值。</summary>
    public Dictionary<string, string> Features { get; init; } = new();
    /// <summary>每次连接一份脚本：第 n 次连接用第 n 份（超出时重复最后一份），用来演示重连。</summary>
    public List<FakeConnectionScript> Connections { get; init; } = new();
}

public sealed class FakeConnectionScript
{
    public int ConnectDelayMs { get; init; } = 300;
    /// <summary>非空时连接失败：timeout / unavailable / permission。</summary>
    public string? FailWith { get; init; }
    public List<FakeEvent> Events { get; init; } = new();
}

public sealed class FakeEvent
{
    /// <summary>距上一个事件（首个事件：距订阅开始）的毫秒数。</summary>
    public int DelayMs { get; init; }
    /// <summary>"indicate" 或 "disconnect"。</summary>
    public string Type { get; init; } = "indicate";
    public string? Characteristic { get; init; }
    public string? Hex { get; init; }
    public string? Reason { get; init; }
    /// <summary>仅供测试断言：ok / unsuccessful / error:&lt;ParseErrorCode&gt;。</summary>
    public string? Expect { get; init; }
    public string? Note { get; init; }
}

/// <summary>内嵌场景库。</summary>
public static class FakeScenarioLibrary
{
    public const string DefaultId = "normal-si";
    private static readonly Lazy<IReadOnlyList<FakeScenario>> AllLazy = new(Load);

    public static IReadOnlyList<FakeScenario> All => AllLazy.Value;

    public static FakeScenario? Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public static FakeScenario Get(string? id) => Find(id) ?? Find(DefaultId) ?? All[0];

    public static FakeScenario Parse(string json) =>
        JsonSerializer.Deserialize<FakeScenario>(json, JsonOptions) ?? throw new JsonException("empty scenario");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static IReadOnlyList<FakeScenario> Load()
    {
        var asm = typeof(FakeScenarioLibrary).Assembly;
        var list = new List<FakeScenario>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Scenarios/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            list.Add(Parse(r.ReadToEnd()));
        }
        return list;
    }
}
