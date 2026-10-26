using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Protocol;

/// <summary>Weight Scale Measurement（0x2A9D）解析出的协议级字段（尚未映射成应用的 Measurement）。</summary>
public sealed record WeightScaleMeasurementData(
    byte Flags,
    UnitSystem Unit,
    /// <summary>Weight 字段为 0xFFFF（Measurement Unsuccessful）。此时 Weight 为 null。</summary>
    bool MeasurementUnsuccessful,
    MassValue? Weight,
    DateTime? TimeStamp,
    /// <summary>User ID；字段不存在或 0xFF（unknown user）时为 null。</summary>
    int? UserId,
    /// <summary>BMI，分辨率 0.1。</summary>
    decimal? Bmi,
    LengthValue? Height)
{
    public bool TimeStampPresent => (Flags & 0x02) != 0;
    public bool UserIdPresent => (Flags & 0x04) != 0;
    public bool BmiAndHeightPresent => (Flags & 0x08) != 0;
}
