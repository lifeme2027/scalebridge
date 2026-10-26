using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScaleBridge.Core.Protocols;
using ScaleBridge.Core.Tests.Support;

namespace ScaleBridge.Core.Tests;

/// <summary>
/// 读取 shared-vectors/vendor/*.json，按 frameType 调用厂商解析器并与 expect 对照。
/// </summary>
public class VendorVectorsTests
{
    private static readonly string[] ExpectKeys =
    {
        "protocol", "rawWeight", "rawUnit", "originalWeight", "weightKg",
        "timestamp", "impedanceRaw", "stabilized", "weightRemoved", "isFinal",
    };

    private static readonly Lazy<string> Dir = new(FindDir);

    private static string FindDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "shared-vectors", "vendor");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("shared-vectors/vendor/ not found above " + AppContext.BaseDirectory);
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
    public void Vector_matches_vendor_parser(string file, string name)
    {
        var v = Find(file, name);
        var data = Hex.Parse(v["hex"]!.GetValue<string>());
        var reject = v["reject"]!.GetValue<bool>();
        var result = Parse(v, data);

        if (reject)
        {
            Assert.False(result.IsSuccess, $"{name}: expected rejection");
            Assert.Null(result.Value);
            Assert.Equal(Enum.Parse<VendorParseErrorCode>(v["errorCode"]!.GetValue<string>()), result.ErrorCode);
            return;
        }

        Assert.True(result.IsSuccess, $"{name}: expected success but got {result.ErrorCode} {result.ErrorMessage}");
        var expectedWarnings = v["warnings"]!.AsArray()
            .Select(w => Enum.Parse<VendorParseWarningCode>(w!.GetValue<string>()))
            .ToHashSet();
        var actualWarnings = result.Warnings.ToHashSet();
        Assert.True(expectedWarnings.SetEquals(actualWarnings),
            $"{name}: warnings {string.Join(",", actualWarnings)} != {string.Join(",", expectedWarnings)}");

        var expect = v["expect"]!.AsObject();
        var expectKeys = expect.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectKeys.SetEquals(ExpectKeys),
            $"{name}: expect keys [{string.Join(",", expectKeys.OrderBy(k => k, StringComparer.Ordinal))}]");

        var reading = result.Value!;
        Assert.Equal(Enum.Parse<VendorProtocol>(expect["protocol"]!.GetValue<string>()), reading.Protocol);
        Assert.Equal(expect["rawWeight"]!.GetValue<int>(), reading.RawWeight);
        Assert.Equal(Enum.Parse<VendorWeightUnit>(expect["rawUnit"]!.GetValue<string>()), reading.RawUnit);
        Assert.Equal(ParseDecimal(expect["originalWeight"]!), reading.OriginalWeight);
        Assert.Equal(ParseDecimal(expect["weightKg"]!), reading.WeightKg);
        AssertTimestamp(expect["timestamp"], reading.Timestamp, name);
        AssertImpedance(expect["impedanceRaw"], reading.ImpedanceRaw);
        Assert.Equal(expect["stabilized"]!.GetValue<bool>(), reading.Stabilized);
        Assert.Equal(expect["weightRemoved"]!.GetValue<bool>(), reading.WeightRemoved);
        Assert.Equal(expect["isFinal"]!.GetValue<bool>(), reading.IsFinal);
    }

    /// <summary>按 frameType 选择解析器。新增厂商时在此加分支。</summary>
    private static VendorParseResult<VendorWeightReading> Parse(JsonObject vector, byte[] data) =>
        vector["frameType"]!.GetValue<string>() switch
        {
            "xiaomi-mi-scale-v1-10" => XiaomiScaleParser.ParseMiScaleFrame(data),
            "xiaomi-body-composition-scale-2-13" => XiaomiScaleParser.ParseBodyCompositionScale2Frame(data),
            "xiaomi-service-data-dispatch" => XiaomiScaleParser.ParseServiceData(ParseUuid16(vector["service"]!.GetValue<string>()), data),
            "chipsea-okok-v11-broadcast-19" => ChipseaOkokParser.ParseV11Broadcast(data),
            "chipsea-okok-v20-broadcast-15" => ChipseaOkokParser.ParseV20Broadcast(data, AssumedUnit(vector)),
            "chipsea-okok-manufacturer-data-dispatch" => ChipseaOkokParser.ParseManufacturerData(data, AssumedUnit(vector)),
            var other => throw new InvalidOperationException($"unknown frameType '{other}' in {vector["name"]}"),
        };

    /// <summary>可选 context.assumedUnit（Kg/Lb/Jin/StoneLb）；缺省为 null。</summary>
    private static VendorWeightUnit? AssumedUnit(JsonObject vector)
    {
        if (vector["context"] is not JsonObject ctx || ctx["assumedUnit"] is not JsonNode node
            || node.GetValueKind() == JsonValueKind.Null)
            return null;
        return Enum.Parse<VendorWeightUnit>(node.GetValue<string>());
    }

    private static ushort ParseUuid16(string hex)
    {
        var bytes = Hex.Parse(hex);
        if (bytes.Length != 2) throw new FormatException("service UUID must be 2 bytes: " + hex);
        return (ushort)(bytes[0] << 8 | bytes[1]);
    }

    private static decimal ParseDecimal(JsonNode node) =>
        decimal.Parse(node.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static void AssertTimestamp(JsonNode? node, DateTime? actual, string name)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }
        var parsed = DateTime.ParseExact(node.GetValue<string>(), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (actual is not DateTime value)
            throw new Xunit.Sdk.XunitException($"{name}: expected timestamp {parsed:o}");
        Assert.Equal(parsed, value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
    }

    private static void AssertImpedance(JsonNode? node, int? actual)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            Assert.Null(actual);
        else
            Assert.Equal(node.GetValue<int>(), actual);
    }

    [Fact]
    public void Files_declare_format_and_names_are_unique()
    {
        var files = Files().ToList();
        Assert.NotEmpty(files);
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, doc) in files)
        {
            Assert.Equal("scalebridge-vectors/1", doc["format"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(doc["suite"]!.GetValue<string>()));
            foreach (var n in doc["vectors"]!.AsArray())
            {
                var o = n!.AsObject();
                foreach (var key in new[] { "name", "service", "hex", "reject", "frameType" })
                    Assert.True(o.ContainsKey(key), $"{file}: vector missing '{key}'");
                Assert.True(all.Add(o["name"]!.GetValue<string>()), $"duplicate vector name {o["name"]}");
                if (o["reject"]!.GetValue<bool>()) Assert.True(o.ContainsKey("errorCode"));
                else Assert.True(o.ContainsKey("expect") && o.ContainsKey("warnings"));
            }
        }
    }
}
