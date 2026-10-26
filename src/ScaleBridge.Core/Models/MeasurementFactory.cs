using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Models;

/// <summary>产生测量的设备上下文。</summary>
public sealed record DeviceContext(string DeviceId, string? DeviceName, bool IsDemo);

/// <summary>把协议级解析结果映射成应用的 <see cref="Measurement"/>。测量不成功（0xFFFF）时返回 null——不存一条没有数值的记录。</summary>
public static class MeasurementFactory
{
    public static Measurement? FromWeightScale(WeightScaleMeasurementData d, DeviceContext device, DateTimeOffset receivedAt)
    {
        if (d.MeasurementUnsuccessful || d.Weight is null) return null;
        return new Measurement
        {
            DeviceTimestamp = d.TimeStamp,
            ReceivedAt = receivedAt,
            WeightKg = d.Weight.Kilograms,
            OriginalWeight = d.Weight.Original,
            OriginalUnit = d.Unit,
            RawWeight = d.Weight.Raw,
            Bmi = d.Bmi,
            HeightMeters = d.Height?.Meters,
            UserId = d.UserId,
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            Source = MeasurementSource.WeightScale,
            IsExperimental = true,
            IsDemo = device.IsDemo,
        };
    }

    public static Measurement? FromBodyComposition(BodyCompositionMeasurementData d, DeviceContext device, DateTimeOffset receivedAt)
    {
        if (d.MeasurementUnsuccessful) return null;
        return new Measurement
        {
            DeviceTimestamp = d.TimeStamp,
            ReceivedAt = receivedAt,
            WeightKg = d.Weight?.Kilograms,
            OriginalWeight = d.Weight?.Original,
            OriginalUnit = d.Weight is null ? null : d.Unit,
            RawWeight = d.Weight?.Raw,
            HeightMeters = d.Height?.Meters,
            BodyFatPercent = d.BodyFatPercent,
            MusclePercent = d.MusclePercent,
            MuscleMassKg = d.MuscleMass?.Kilograms,
            FatFreeMassKg = d.FatFreeMass?.Kilograms,
            SoftLeanMassKg = d.SoftLeanMass?.Kilograms,
            BodyWaterMassKg = d.BodyWaterMass?.Kilograms,
            BasalMetabolismKj = d.BasalMetabolismKj,
            ImpedanceOhm = d.ImpedanceOhm,
            UserId = d.UserId,
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            Source = MeasurementSource.BodyComposition,
            IsExperimental = true,
            IsDemo = device.IsDemo,
        };
    }
}
