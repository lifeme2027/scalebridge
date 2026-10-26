using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Ble;

[Flags]
public enum ScaleProtocols
{
    None = 0,
    /// <summary>Bluetooth SIG Weight Scale Service (0x181D)。</summary>
    WeightScale = 1,
    /// <summary>Bluetooth SIG Body Composition Service (0x181B)。</summary>
    BodyComposition = 2,
}

/// <summary>根据广播的服务 UUID 判断设备可能使用的标准协议。注意：这只说明「广播了该服务」，不代表任何真机验证过。</summary>
public static class DeviceClassifier
{
    public static ScaleProtocols Classify(IEnumerable<Guid> serviceUuids)
    {
        var result = ScaleProtocols.None;
        foreach (var u in serviceUuids)
        {
            if (u == GattUuids.WeightScaleService) result |= ScaleProtocols.WeightScale;
            else if (u == GattUuids.BodyCompositionService) result |= ScaleProtocols.BodyComposition;
        }
        return result;
    }

    public static ScaleProtocols Classify(AdvertisedDevice device) => Classify(device.ServiceUuids);

    /// <summary>默认扫描过滤：只看标准体重秤/体成分服务。</summary>
    public static IReadOnlyList<Guid> StandardServiceFilter { get; } =
        new[] { GattUuids.WeightScaleService, GattUuids.BodyCompositionService };
}
