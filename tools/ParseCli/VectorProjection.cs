using System.Globalization;
using System.Text.Json.Nodes;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.ParseCli;

/// <summary>把 Core 的解析结果投影成与 shared-vectors/*.json 的 "expect" 完全同构的 JSON，便于逐字段比较。</summary>
public static class VectorProjection
{
    private static JsonNode? Dec(decimal? v) => v is null ? null : JsonValue.Create(v.Value.ToString(CultureInfo.InvariantCulture));
    private static JsonNode? Str(string? v) => v is null ? null : JsonValue.Create(v);
    private static JsonNode? Int(int? v) => v is null ? null : JsonValue.Create(v.Value);
    private static JsonNode? Ts(DateTime? v) => v is null ? null : JsonValue.Create(v.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
    private static string Unit(UnitSystem u) => u == UnitSystem.Si ? "si" : "imperial";

    private static JsonNode? Mass(MassValue? m) => m is null ? null : new JsonObject
    {
        ["raw"] = (int)m.Raw, ["original"] = Dec(m.Original), ["kilograms"] = Dec(m.Kilograms),
    };

    private static JsonNode? Length(LengthValue? l) => l is null ? null : new JsonObject
    {
        ["raw"] = (int)l.Raw, ["original"] = Dec(l.Original), ["meters"] = Dec(l.Meters),
    };

    public static JsonObject Project(WeightScaleFeature f) => new()
    {
        ["timeStampSupported"] = f.TimeStampSupported, ["multipleUsersSupported"] = f.MultipleUsersSupported,
        ["bmiSupported"] = f.BmiSupported,
        ["weightResolutionCode"] = f.WeightResolutionCode, ["heightResolutionCode"] = f.HeightResolutionCode,
        ["weightResolutionKg"] = Dec(f.WeightResolutionKg), ["weightResolutionLb"] = Dec(f.WeightResolutionLb),
        ["heightResolutionMeters"] = Dec(f.HeightResolutionMeters), ["heightResolutionInches"] = Dec(f.HeightResolutionInches),
    };

    public static JsonObject Project(WeightScaleMeasurementData m) => new()
    {
        ["unit"] = Unit(m.Unit), ["measurementUnsuccessful"] = m.MeasurementUnsuccessful,
        ["weight"] = Mass(m.Weight), ["timestamp"] = Ts(m.TimeStamp), ["userId"] = Int(m.UserId),
        ["bmi"] = Dec(m.Bmi), ["height"] = Length(m.Height),
    };

    public static JsonObject Project(BodyCompositionFeature f) => new()
    {
        ["timeStampSupported"] = f.TimeStampSupported, ["multipleUsersSupported"] = f.MultipleUsersSupported,
        ["basalMetabolismSupported"] = f.BasalMetabolismSupported, ["musclePercentageSupported"] = f.MusclePercentageSupported,
        ["muscleMassSupported"] = f.MuscleMassSupported, ["fatFreeMassSupported"] = f.FatFreeMassSupported,
        ["softLeanMassSupported"] = f.SoftLeanMassSupported, ["bodyWaterMassSupported"] = f.BodyWaterMassSupported,
        ["impedanceSupported"] = f.ImpedanceSupported, ["weightSupported"] = f.WeightSupported,
        ["heightSupported"] = f.HeightSupported,
        ["massResolutionCode"] = f.MassResolutionCode, ["heightResolutionCode"] = f.HeightResolutionCode,
        ["massResolutionKg"] = Dec(f.MassResolutionKg), ["massResolutionLb"] = Dec(f.MassResolutionLb),
        ["heightResolutionMeters"] = Dec(f.HeightResolutionMeters), ["heightResolutionInches"] = Dec(f.HeightResolutionInches),
    };

    public static JsonObject Project(BodyCompositionMeasurementData m) => new()
    {
        ["unit"] = Unit(m.Unit), ["measurementUnsuccessful"] = m.MeasurementUnsuccessful,
        ["bodyFatPercent"] = Dec(m.BodyFatPercent), ["timestamp"] = Ts(m.TimeStamp), ["userId"] = Int(m.UserId),
        ["basalMetabolismKj"] = Int(m.BasalMetabolismKj), ["musclePercent"] = Dec(m.MusclePercent),
        ["muscleMass"] = Mass(m.MuscleMass), ["fatFreeMass"] = Mass(m.FatFreeMass),
        ["softLeanMass"] = Mass(m.SoftLeanMass), ["bodyWaterMass"] = Mass(m.BodyWaterMass),
        ["impedanceOhm"] = Dec(m.ImpedanceOhm), ["weight"] = Mass(m.Weight), ["height"] = Length(m.Height),
    };

    // --details：Core 数据模型里有、但 shared-vectors 的 expect 不含的原始位字段。
    // 测量：Flags 原始值（Weight Scale 1 字节 / Body Composition 2 字节）；Feature：32 位 Raw。

    public static JsonObject Details(WeightScaleMeasurementData m) => new() { ["flags"] = (int)m.Flags };
    public static JsonObject Details(BodyCompositionMeasurementData m) => new() { ["flags"] = (int)m.Flags };
    public static JsonObject Details(WeightScaleFeature f) => new() { ["raw"] = f.Raw.ToString(CultureInfo.InvariantCulture) };
    public static JsonObject Details(BodyCompositionFeature f) => new() { ["raw"] = f.Raw.ToString(CultureInfo.InvariantCulture) };

    /// <summary>深度比较：字符串两边都能解析成 decimal 时按数值比较（"70.005" == "70.0050"），其它按值比较。返回差异描述或 null。</summary>
    public static string? Diff(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? null : $"{path}: expected {Show(expected)} but was {Show(actual)}";

        if (expected is JsonObject eo)
        {
            if (actual is not JsonObject ao) return $"{path}: expected object but was {Show(actual)}";
            foreach (var key in eo.Select(kv => kv.Key).Union(ao.Select(kv => kv.Key)))
            {
                var d = Diff(eo.ContainsKey(key) ? eo[key] : null, ao.ContainsKey(key) ? ao[key] : null, $"{path}.{key}");
                if (d is not null) return d;
                if (eo.ContainsKey(key) != ao.ContainsKey(key)) return $"{path}.{key}: key present on one side only";
            }
            return null;
        }

        var ev = expected.AsValue();
        var av = actual.AsValue();
        if (ev.TryGetValue<string>(out var es) && av.TryGetValue<string>(out var asv))
        {
            if (decimal.TryParse(es, NumberStyles.Float, CultureInfo.InvariantCulture, out var ed) &&
                decimal.TryParse(asv, NumberStyles.Float, CultureInfo.InvariantCulture, out var ad))
                return ed == ad ? null : $"{path}: expected {es} but was {asv}";
            return es == asv ? null : $"{path}: expected \"{es}\" but was \"{asv}\"";
        }
        return ev.ToJsonString() == av.ToJsonString() ? null : $"{path}: expected {Show(expected)} but was {Show(actual)}";
    }

    private static string Show(JsonNode? n) => n is null ? "null" : n.ToJsonString();
}
