using System.Globalization;

namespace ScaleBridge.Core.Models;

/// <summary>体重合理性分类（只用于标注与是否自动入库，不改变存储的数值）。</summary>
public enum WeightPlausibility
{
    /// <summary>这条记录没有体重（例如只有体脂的 Body Composition 包）。</summary>
    NotApplicable = 0,
    Normal = 1,
    /// <summary>恰好 0：协议上合法，但不含信息，不自动入库。</summary>
    Zero = 2,
    /// <summary>小于 2 kg：入库但标「异常」（婴儿秤等可能合法，所以不丢弃）。</summary>
    TooLight = 3,
    /// <summary>大于 200 kg：入库但标「异常」。</summary>
    TooHeavy = 4,
}

/// <summary>
/// 数据管线的明文策略（NOTES.md「解析策略与已知分歧」Q13/Q14/Q16 的实现）。
/// 全是纯函数，便于单测；协议解析本身（Protocol/）不依赖这里。
/// </summary>
public static class MeasurementPolicy
{
    /// <summary>≥ 该值（含）才算正常。2.000 kg 恰好正常。</summary>
    public const decimal MinPlausibleKg = 2m;
    /// <summary>≤ 该值（含）才算正常。200.000 kg 恰好正常。</summary>
    public const decimal MaxPlausibleKg = 200m;

    /// <summary>设备时间比接收时间晚超过这个时长，视为「设备时钟可疑」。</summary>
    public static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromHours(24);
    /// <summary>设备时间比接收时间早超过这个时长，视为「设备时钟可疑」（常见：电池耗尽后复位到 2000 年）。</summary>
    public static readonly TimeSpan MaxPastClockSkew = TimeSpan.FromDays(366);

    public static WeightPlausibility ClassifyWeight(decimal? kilograms)
    {
        if (kilograms is not { } kg) return WeightPlausibility.NotApplicable;
        if (kg == 0m) return WeightPlausibility.Zero;
        if (kg < MinPlausibleKg) return WeightPlausibility.TooLight;
        if (kg > MaxPlausibleKg) return WeightPlausibility.TooHeavy;
        return WeightPlausibility.Normal;
    }

    public static bool IsAbnormal(WeightPlausibility p) => p is WeightPlausibility.TooLight or WeightPlausibility.TooHeavy;

    /// <summary>体重恰为 0 的记录不自动入库（界面仍在实时区提示）；其余都入库。</summary>
    public static bool ShouldAutoSave(Measurement m) => ClassifyWeight(m.WeightKg) != WeightPlausibility.Zero;

    /// <summary>
    /// 去重键。只有设备给了有效时间戳时才有键（没有时间戳的两次相同读数是两次真实称重，不能合并，返回 null）。
    /// 键 = 设备 + 来源特征 + 用户 + 设备时间戳（到秒）+ 全部测量值的原始/换算结果。
    /// 同一时间戳但数值不同 → 键不同 → 两条都保留（不静默覆盖）。
    /// </summary>
    public static string? DedupKey(Measurement m)
    {
        if (m.DeviceTimestamp is not { } ts) return null;
        var inv = CultureInfo.InvariantCulture;
        static string V(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? "-";
        return string.Join('|',
            m.DeviceId,
            ((int)m.Source).ToString(inv),
            m.UserId?.ToString(inv) ?? "-",
            ts.ToString("yyyy-MM-ddTHH:mm:ss", inv),
            m.RawWeight?.ToString(inv) ?? "-",
            V(m.Bmi), V(m.HeightMeters), V(m.BodyFatPercent), V(m.MusclePercent),
            V(m.MuscleMassKg), V(m.FatFreeMassKg), V(m.SoftLeanMassKg), V(m.BodyWaterMassKg),
            m.BasalMetabolismKj?.ToString(inv) ?? "-", V(m.ImpedanceOhm));
    }

    /// <summary>
    /// 设备时间戳明显不可信：比接收时的本地墙钟时间晚超过 24 小时或早超过 366 天。
    /// 设备时间是「设备本地时间（无时区）」，所以与接收时刻的本地墙钟（<see cref="DateTimeOffset.DateTime"/>）比较，不做时区换算。
    /// 没有设备时间戳时返回 false。
    /// </summary>
    public static bool IsDeviceClockSuspect(Measurement m)
    {
        if (m.DeviceTimestamp is not { } ts) return false;
        var diff = ts - m.ReceivedAt.DateTime;
        return diff > MaxFutureClockSkew || -diff > MaxPastClockSkew;
    }
}

/// <summary>入库结果。</summary>
public enum IngestOutcome
{
    Saved = 1,
    /// <summary>与已入库记录的去重键相同，未重复保存。</summary>
    Duplicate = 2,
    /// <summary>体重为 0，按策略不自动入库。</summary>
    SkippedZeroWeight = 3,
    /// <summary>与已入库的一条记录（另一个服务来的同一次称重，Q17）合并；<see cref="IngestResult.Measurement"/> 是合并后的那条（主键为原记录的主键）。</summary>
    Merged = 4,
}

public sealed record IngestResult(IngestOutcome Outcome, Measurement Measurement, WeightPlausibility Plausibility, bool DeviceClockSuspect)
{
    public bool IsAbnormal => MeasurementPolicy.IsAbnormal(Plausibility);
}

/// <summary>
/// 把收到的测量按 <see cref="MeasurementPolicy"/> 过滤后写入仓库。去重键由仓库持久化（见 <see cref="IMeasurementRepository.HasDedupKey"/>），
/// 所以去重跨连接/跨重启有效，构造时不再读取全部历史。删除/清空历史会同时释放对应的键（之后重发同一条数据会再次入库）。
/// 顺序：零体重不入库 → Q13 去重 → Q17 合并判断（<see cref="MeasurementMerge"/>）→ 新增。非线程安全，调用方在同一线程使用。
/// </summary>
public sealed class MeasurementIngestor
{
    /// <summary>无时间戳时按接收时间配对用：只记住最近这么多条本 ingestor 写入/合并过的记录（有时间戳的另外按设备+时间戳查库）。</summary>
    private const int RecentCapacity = 16;

    private readonly IMeasurementRepository _repo;
    private readonly List<Measurement> _recent = new();

    public MeasurementIngestor(IMeasurementRepository repo) => _repo = repo;

    public IngestResult Ingest(Measurement m)
    {
        var plaus = MeasurementPolicy.ClassifyWeight(m.WeightKg);
        var suspect = MeasurementPolicy.IsDeviceClockSuspect(m);
        if (!MeasurementPolicy.ShouldAutoSave(m))
            return new IngestResult(IngestOutcome.SkippedZeroWeight, m, plaus, suspect);

        var key = MeasurementPolicy.DedupKey(m);
        if (key is not null && _repo.HasDedupKey(key))
            return new IngestResult(IngestOutcome.Duplicate, m, plaus, suspect);

        // Q17：找「另一个服务来的同一次称重」。恰好一个候选判定为合并才合并；有任何别的相关候选（歧义）或只是「像」→ 保留两条并标注。
        var related = Candidates(m)
            .Select(c => (Candidate: c, Decision: MeasurementMerge.Evaluate(c, m)))
            .Where(x => x.Decision != MergeDecision.Unrelated)
            .ToList();
        if (related.Count == 1 && related[0].Decision == MergeDecision.Merge)
        {
            var merged = _repo.Replace(MeasurementMerge.Combine(related[0].Candidate, m), key);
            if (merged is not null)
            {
                Remember(merged);
                return new IngestResult(IngestOutcome.Merged, merged, plaus, suspect);
            }
        }

        if (related.Count > 0)
            m = m with { PossibleSameAsId = related.OrderBy(x => x.Decision == MergeDecision.Merge ? 0 : 1).First().Candidate.Id };
        var saved = _repo.Add(m);
        Remember(saved);
        return new IngestResult(IngestOutcome.Saved, saved, plaus, suspect);
    }

    /// <summary>清掉「最近记录」缓存（界面删除/清空历史之后调用；缓存里的记录合并前本来也会重新读库确认还在）。</summary>
    public void Reset() => _recent.Clear();

    private IEnumerable<Measurement> Candidates(Measurement m)
    {
        var seen = new HashSet<long>();
        var list = new List<Measurement>();
        void Consider(Measurement? c) { if (c is not null && seen.Add(c.Id)) list.Add(c); }
        // 缓存里的副本只用来预筛（每次读库都要开一个连接，16 条全读太贵）；通过预筛的才重新读库确认还在（可能已被删除）并以库里的为准。
        foreach (var r in _recent.ToList())
            if (MeasurementMerge.Evaluate(r, m) != MergeDecision.Unrelated) Consider(_repo.Get(r.Id));
        if (m.DeviceTimestamp is { } ts)
            foreach (var c in _repo.FindByDeviceTime(m.DeviceId, ts)) Consider(c);
        return list;
    }

    private void Remember(Measurement saved)
    {
        _recent.RemoveAll(x => x.Id == saved.Id);
        _recent.Add(saved);
        if (_recent.Count > RecentCapacity) _recent.RemoveAt(0);
    }
}
