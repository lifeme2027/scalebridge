namespace ScaleBridge.Core.Models;

/// <summary>两条记录的关系（NOTES「解析策略与已知分歧」Q17）。</summary>
public enum MergeDecision
{
    /// <summary>看不出是同一次称重（或明确不是），各自独立，不标注。</summary>
    Unrelated = 0,
    /// <summary>像是同一次称重但证据不够：保留两条，在较晚的一条上标注「可能重复」。</summary>
    PossibleSame = 1,
    /// <summary>证据充分：合并成一条。</summary>
    Merge = 2,
}

/// <summary>
/// Q17：同一次称重可能同时从 0x181D（WSS）与 0x181B（BCS）各到一包。保守合并规则（实验性——协议层面没有「这两包是同一次」的标识，
/// 以下是启发式，未经任何真机验证）。任何一条不满足就不合并；拿不准就保留两条并标注，宁可多一条也不错并。
/// 纯函数，不依赖仓库。去重键（Q13）不在这里，也不受这里影响。
/// </summary>
public static class MeasurementMerge
{
    /// <summary>没有时间戳时，两包的接收时间相差不超过该值才可能是同一次（同一次称重的两个通知通常在几十毫秒内相继到达）。</summary>
    public static readonly TimeSpan NoTimestampWindow = TimeSpan.FromSeconds(2);

    public static MergeDecision Evaluate(Measurement existing, Measurement incoming)
    {
        // 前提：同设备；一条 WSS、一条 BCS（已经合并过的、或来源相同的不再合并——同一来源的两包是两次称重）。
        if (existing.DeviceId != incoming.DeviceId) return MergeDecision.Unrelated;
        if (existing.Source == incoming.Source || existing.Source == MeasurementSource.Both || incoming.Source == MeasurementSource.Both)
            return MergeDecision.Unrelated;
        // 两边都给了用户且不同：明确是不同的人。
        if (existing.UserId is { } ua && incoming.UserId is { } ub && ua != ub) return MergeDecision.Unrelated;

        var tsA = Second(existing.DeviceTimestamp);
        var tsB = Second(incoming.DeviceTimestamp);
        var bothTs = tsA is not null && tsB is not null;
        var neitherTs = tsA is null && tsB is null;
        var tsEqual = bothTs && tsA == tsB;
        var recvClose = (existing.ReceivedAt - incoming.ReceivedAt).Duration() <= NoTimestampWindow;
        // 既没有相同的设备时间戳、接收时间也不相近：不相关。
        if (!tsEqual && !recvClose) return MergeDecision.Unrelated;

        var weightsEqual = existing.WeightKg is not null && incoming.WeightKg is not null &&
            existing.WeightKg == incoming.WeightKg && existing.RawWeight == incoming.RawWeight && existing.OriginalUnit == incoming.OriginalUnit;
        var weightMissing = existing.WeightKg is null || incoming.WeightKg is null;
        var bothWeightsDiffer = !weightsEqual && !weightMissing;
        var clockSuspect = MeasurementPolicy.IsDeviceClockSuspect(existing) || MeasurementPolicy.IsDeviceClockSuspect(incoming);
        var userOneSided = existing.UserId.HasValue != incoming.UserId.HasValue;
        var fieldConflict = Conflicts(existing.Bmi, incoming.Bmi) || Conflicts(existing.HeightMeters, incoming.HeightMeters);

        // 体重一致，或（只在时间戳相同且设备时钟可信时）一边没带体重——此时靠时间戳这一个证据，所以要求时钟可信。
        var weightOk = weightsEqual || (weightMissing && tsEqual && !clockSuspect);
        // 时间戳相同才算「同一次」的前提是设备时钟可信；时钟可疑（如电池耗尽复位后卡在固定值）时，相同时间戳不足以证明，还要求接收时间相近。
        var timeOk = (tsEqual && (!clockSuspect || recvClose)) || (neitherTs && recvClose);
        var mergeable = !userOneSided && !fieldConflict && weightOk && timeOk;
        if (mergeable) return MergeDecision.Merge;

        // 没到合并的程度，但值得提醒：体重相同 / 有一边缺体重 / 时间戳相同。
        // 例外：接收时间相近但体重明确不同且时间戳不能证明同一次 → 更像两次连续称重，不标注。
        if (bothWeightsDiffer && !tsEqual) return MergeDecision.Unrelated;
        return MergeDecision.PossibleSame;
    }

    /// <summary>把 <paramref name="incoming"/> 的字段补进 <paramref name="existing"/>。仅在 <see cref="Evaluate"/> 返回 Merge 时调用：此时同名字段要么相等要么一边为空，所以不会覆盖任何已有的值。</summary>
    public static Measurement Combine(Measurement existing, Measurement incoming)
    {
        var useExistingWeight = existing.WeightKg is not null;
        return existing with
        {
            Source = MeasurementSource.Both,
            ReceivedAt = existing.ReceivedAt <= incoming.ReceivedAt ? existing.ReceivedAt : incoming.ReceivedAt,
            DeviceTimestamp = existing.DeviceTimestamp ?? incoming.DeviceTimestamp,
            WeightKg = useExistingWeight ? existing.WeightKg : incoming.WeightKg,
            OriginalWeight = useExistingWeight ? existing.OriginalWeight : incoming.OriginalWeight,
            OriginalUnit = useExistingWeight ? existing.OriginalUnit : incoming.OriginalUnit,
            RawWeight = useExistingWeight ? existing.RawWeight : incoming.RawWeight,
            Bmi = existing.Bmi ?? incoming.Bmi,
            HeightMeters = existing.HeightMeters ?? incoming.HeightMeters,
            BodyFatPercent = existing.BodyFatPercent ?? incoming.BodyFatPercent,
            MusclePercent = existing.MusclePercent ?? incoming.MusclePercent,
            MuscleMassKg = existing.MuscleMassKg ?? incoming.MuscleMassKg,
            FatFreeMassKg = existing.FatFreeMassKg ?? incoming.FatFreeMassKg,
            SoftLeanMassKg = existing.SoftLeanMassKg ?? incoming.SoftLeanMassKg,
            BodyWaterMassKg = existing.BodyWaterMassKg ?? incoming.BodyWaterMassKg,
            BasalMetabolismKj = existing.BasalMetabolismKj ?? incoming.BasalMetabolismKj,
            ImpedanceOhm = existing.ImpedanceOhm ?? incoming.ImpedanceOhm,
            UserId = existing.UserId ?? incoming.UserId,
            DeviceName = existing.DeviceName ?? incoming.DeviceName,
            IsExperimental = existing.IsExperimental || incoming.IsExperimental,
            IsDemo = existing.IsDemo || incoming.IsDemo,
        };
    }

    private static DateTime? Second(DateTime? t) => t is { } v ? new DateTime(v.Ticks - v.Ticks % TimeSpan.TicksPerSecond, v.Kind) : null;
    private static bool Conflicts(decimal? a, decimal? b) => a is not null && b is not null && a != b;
}
