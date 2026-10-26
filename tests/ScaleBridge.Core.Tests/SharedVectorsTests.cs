using System.Text.Json.Nodes;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Core.Tests.Support;
using ScaleBridge.ParseCli;

namespace ScaleBridge.Core.Tests;

/// <summary>
/// 读取仓库根目录 shared-vectors/*.json（与 Web 版共用的字节向量），逐条用 Core 解析器验证。
/// 格式说明见 shared-vectors/README.md。
/// </summary>
public class SharedVectorsTests
{
    private static readonly Lazy<string> Dir = new(FindDir);

    private static string FindDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "shared-vectors");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("shared-vectors/ not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string File, JsonObject Doc)> Files() =>
        Directory.GetFiles(Dir.Value, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), JsonNode.Parse(File.ReadAllText(f))!.AsObject()));

    public static IEnumerable<object[]> AllVectors() =>
        Files().SelectMany(f => f.Doc["vectors"]!.AsArray().Select(v => new object[] { f.File, v!["name"]!.GetValue<string>() }));

    private static JsonObject Find(string file, string name) =>
        Files().First(f => f.File == file).Doc["vectors"]!.AsArray()
            .Select(v => v!.AsObject()).First(v => v["name"]!.GetValue<string>() == name);

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Vector_matches_core_parser(string file, string name)
    {
        var v = Find(file, name);
        var data = Hex.Parse(v["hex"]!.GetValue<string>());
        var service = v["service"]!.GetValue<string>();
        var characteristic = v["characteristic"]!.GetValue<string>();
        var reject = v["reject"]!.GetValue<bool>();

        (bool ok, ParseErrorCode code, IReadOnlyList<ParseWarningCode> warnings, JsonObject? actual) = (service, characteristic) switch
        {
            ("181D", "2A9E") => Unpack(WeightScaleParser.ParseFeature(data), VectorProjection.Project),
            ("181D", "2A9D") => Unpack(WeightScaleParser.ParseMeasurement(data), VectorProjection.Project),
            ("181B", "2A9B") => Unpack(BodyCompositionParser.ParseFeature(data), VectorProjection.Project),
            ("181B", "2A9C") => Unpack(BodyCompositionParser.ParseMeasurement(data), VectorProjection.Project),
            _ => throw new InvalidOperationException($"unknown service/characteristic {service}/{characteristic} in {name}"),
        };

        if (reject)
        {
            Assert.False(ok, $"{name}: expected rejection");
            Assert.Equal(v["errorCode"]!.GetValue<string>(), code.ToString());
            return;
        }

        Assert.True(ok, $"{name}: expected success but got {code}");
        var expectedWarnings = v["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).OrderBy(w => w, StringComparer.Ordinal);
        Assert.Equal(expectedWarnings, warnings.Select(w => w.ToString()).OrderBy(w => w, StringComparer.Ordinal));
        var diff = VectorProjection.Diff(v["expect"], actual);
        Assert.True(diff is null, $"{name}: {diff}");
    }

    private static (bool, ParseErrorCode, IReadOnlyList<ParseWarningCode>, JsonObject?) Unpack<T>(
        ParseResult<T> r, Func<T, JsonObject> project) where T : class =>
        (r.IsSuccess, r.ErrorCode, r.Warnings, r.IsSuccess ? project(r.Value!) : null);

    [Fact]
    public void Files_are_self_describing_and_names_are_unique()
    {
        var files = Files().ToList();
        Assert.True(files.Count >= 2);
        var all = new HashSet<string>();
        foreach (var (file, doc) in files)
        {
            Assert.Equal("scalebridge-vectors/1", doc["format"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(doc["suite"]!.GetValue<string>()));
            foreach (var n in doc["vectors"]!.AsArray())
            {
                var o = n!.AsObject();
                foreach (var key in new[] { "name", "service", "characteristic", "hex", "reject" })
                    Assert.True(o.ContainsKey(key), $"{file}: vector missing '{key}'");
                Assert.True(all.Add(o["name"]!.GetValue<string>()), $"duplicate vector name {o["name"]}");
                if (o["reject"]!.GetValue<bool>()) Assert.True(o.ContainsKey("errorCode"));
                else Assert.True(o.ContainsKey("expect") && o.ContainsKey("warnings"));
            }
        }
        Assert.True(all.Count >= 200);
    }
}
