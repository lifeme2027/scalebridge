using System.Text.Json.Nodes;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.ParseCli;

/// <summary>一次解析的结果，已投影成与 shared-vectors 同构的字段。</summary>
public sealed record ParseOutcome(
    bool Reject,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Warnings,
    JsonObject? Expect);

public static class Parsing
{
    public static bool IsSupported(string service, string characteristic) =>
        (Normalize(service), Normalize(characteristic)) is ("181D", "2A9D" or "2A9E") or ("181B", "2A9B" or "2A9C");

    /// <summary>"0x181d" / "181d" → "181D"。</summary>
    public static string Normalize(string uuid)
    {
        var t = uuid.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return t.ToUpperInvariant();
    }

    /// <summary>"00 59 36" / "005936" / "00:59:36" / "00-59-36" → 字节；空串 → 空数组。非法字符或奇数个十六进制位抛 FormatException。</summary>
    public static byte[] ParseHex(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '-' or ':' or ',') continue;
            if (!Uri.IsHexDigit(c)) throw new FormatException($"not a hex digit: '{c}'");
            sb.Append(c);
        }
        if (sb.Length % 2 != 0) throw new FormatException("odd number of hex digits");
        return Convert.FromHexString(sb.ToString());
    }

    public static string FormatHex(ReadOnlySpan<byte> data) => string.Join(' ', data.ToArray().Select(b => b.ToString("X2")));

    /// <summary>解析一段字节。不支持的 service/characteristic 抛 NotSupportedException（调用方当作用法错误）。</summary>
    public static ParseOutcome Parse(string service, string characteristic, ReadOnlySpan<byte> data) =>
        (Normalize(service), Normalize(characteristic)) switch
        {
            ("181D", "2A9E") => From(WeightScaleParser.ParseFeature(data), VectorProjection.Project),
            ("181D", "2A9D") => From(WeightScaleParser.ParseMeasurement(data), VectorProjection.Project),
            ("181B", "2A9B") => From(BodyCompositionParser.ParseFeature(data), VectorProjection.Project),
            ("181B", "2A9C") => From(BodyCompositionParser.ParseMeasurement(data), VectorProjection.Project),
            var other => throw new NotSupportedException(
                $"unsupported service/characteristic {other.Item1}/{other.Item2}; supported: 181D/2A9E 181D/2A9D 181B/2A9B 181B/2A9C"),
        };

    private static ParseOutcome From<T>(ParseResult<T> r, Func<T, JsonObject> project) where T : class =>
        r.IsSuccess
            ? new ParseOutcome(false, null, null, r.Warnings.Select(w => w.ToString()).ToList(), project(r.Value!))
            : new ParseOutcome(true, r.ErrorCode.ToString(), r.ErrorMessage, Array.Empty<string>(), null);

    /// <summary>与 scalebridge-vectors/1 里一条向量同形：reject / errorCode | warnings + expect（另附 errorMessage 便于阅读，比较时忽略）。</summary>
    public static JsonObject ToVectorJson(string? name, string service, string characteristic, byte[] data, ParseOutcome o)
    {
        var obj = new JsonObject();
        if (name is not null) obj["name"] = name;
        obj["service"] = Normalize(service);
        obj["characteristic"] = Normalize(characteristic);
        obj["hex"] = FormatHex(data);
        obj["reject"] = o.Reject;
        if (o.Reject)
        {
            obj["errorCode"] = o.ErrorCode;
            obj["errorMessage"] = o.ErrorMessage;
        }
        else
        {
            var w = new JsonArray();
            foreach (var x in o.Warnings) w.Add(x);
            obj["warnings"] = w;
            obj["expect"] = o.Expect!.DeepClone();
        }
        return obj;
    }

    /// <summary>
    /// 把「期望的向量」与「实际解析结果」比较，返回差异描述（null = 一致）。
    /// 与 SharedVectorsTests 同一规则：reject 与 errorCode 必须相同；成功时警告集合相同、expect 逐键相同（十进制串按数值比较）。
    /// </summary>
    public static string? Compare(JsonObject expectedVector, ParseOutcome actual)
    {
        var expectReject = expectedVector["reject"]?.GetValue<bool>() ?? false;
        if (expectReject)
        {
            if (!actual.Reject) return "expected rejection but parser accepted";
            var code = expectedVector["errorCode"]?.GetValue<string>();
            return code is null || code == actual.ErrorCode ? null : $"errorCode: expected {code} but was {actual.ErrorCode}";
        }
        if (actual.Reject) return $"expected success but parser rejected with {actual.ErrorCode}";
        var ew = (expectedVector["warnings"]?.AsArray() ?? new JsonArray()).Select(n => n!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var aw = actual.Warnings.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!ew.SequenceEqual(aw)) return $"warnings: expected [{string.Join(",", ew)}] but was [{string.Join(",", aw)}]";
        return VectorProjection.Diff(expectedVector["expect"], actual.Expect);
    }
}
