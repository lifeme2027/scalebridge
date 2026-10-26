namespace ScaleBridge.Core.Models;

/// <summary>数据来自哪个标准特征。</summary>
public enum MeasurementSource
{
    /// <summary>Weight Measurement 0x2A9D（Weight Scale Service 0x181D）。</summary>
    WeightScale = 1,
    /// <summary>Body Composition Measurement 0x2A9C（Body Composition Service 0x181B）。</summary>
    BodyComposition = 2,
    /// <summary>同一次称重经 0x181D 与 0x181B 各到一包，按 NOTES「解析策略与已知分歧」Q17 的保守规则合并成一条（实验性，未经真机验证）。</summary>
    Both = 3,
}

/// <summary>
/// 应用层的一条测量记录。体重以 kg 的 decimal 保存，同时保留设备原始单位与原始整数值。
/// 所有 kg/m 值均由 decimal 精确换算，没有二进制浮点累积。
/// </summary>
public sealed record Measurement
{
    /// <summary>数据库主键；尚未保存时为 0。</summary>
    public long Id { get; init; }

    /// <summary>设备给出的时间戳（设备本地时间，无时区）；设备没给或无效时为 null。</summary>
    public DateTime? DeviceTimestamp { get; init; }

    /// <summary>本程序收到数据的时间（带时区偏移）。</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    public decimal? WeightKg { get; init; }
    /// <summary>设备原始单位下的体重（kg 或 lb）。</summary>
    public decimal? OriginalWeight { get; init; }
    public UnitSystem? OriginalUnit { get; init; }
    /// <summary>体重字段的原始 uint16 值。</summary>
    public int? RawWeight { get; init; }

    public decimal? Bmi { get; init; }
    public decimal? HeightMeters { get; init; }
    public decimal? BodyFatPercent { get; init; }
    public decimal? MusclePercent { get; init; }
    public decimal? MuscleMassKg { get; init; }
    public decimal? FatFreeMassKg { get; init; }
    public decimal? SoftLeanMassKg { get; init; }
    public decimal? BodyWaterMassKg { get; init; }
    public int? BasalMetabolismKj { get; init; }
    public decimal? ImpedanceOhm { get; init; }

    public int? UserId { get; init; }

    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
    public MeasurementSource Source { get; init; }

    /// <summary>「实验性（仅按规范实现，未真机验证）」。目前所有协议都是 true。</summary>
    public bool IsExperimental { get; init; } = true;

    /// <summary>是否为演示数据（Fake 适配器）。演示数据绝不是真实体重。</summary>
    public bool IsDemo { get; init; }

    /// <summary>
    /// 「可能与该记录是同一次称重」：Q17 规则认为像但不够确定（例如一边缺时间戳、体重不同但时间戳相同），
    /// 所以没有合并，保留两条并在较晚到的这条上标注指向较早的那条。仅标注，不改变任何数值。
    /// </summary>
    public long? PossibleSameAsId { get; init; }

    /// <summary>用于列表显示/排序的时间：优先设备时间戳，否则接收时间。</summary>
    public DateTime DisplayTime => DeviceTimestamp ?? ReceivedAt.LocalDateTime;
}
