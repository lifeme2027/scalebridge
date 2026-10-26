using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Protocol;

/// <summary>Body Composition Measurement（0x2A9C）解析出的协议级字段。</summary>
public sealed record BodyCompositionMeasurementData(
    ushort Flags,
    UnitSystem Unit,
    /// <summary>Body Fat Percentage 为 0xFFFF（Measurement Unsuccessful）。此时 BodyFatPercent 为 null。</summary>
    bool MeasurementUnsuccessful,
    /// <summary>体脂率，分辨率 0.1 %。</summary>
    decimal? BodyFatPercent,
    DateTime? TimeStamp,
    int? UserId,
    /// <summary>基础代谢，单位 kJ，分辨率 1。</summary>
    int? BasalMetabolismKj,
    /// <summary>肌肉率，0.1 %。</summary>
    decimal? MusclePercent,
    MassValue? MuscleMass,
    MassValue? FatFreeMass,
    MassValue? SoftLeanMass,
    MassValue? BodyWaterMass,
    /// <summary>阻抗，0.1 Ω。</summary>
    decimal? ImpedanceOhm,
    MassValue? Weight,
    LengthValue? Height)
{
    public bool MultiplePacket => (Flags & 0x1000) != 0;
}
