using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests.Protocol;

public class DeviceClassifierTests
{
    [Fact]
    public void No_services_is_none() => Assert.Equal(ScaleProtocols.None, DeviceClassifier.Classify(Array.Empty<Guid>()));

    [Fact]
    public void Weight_scale_only() =>
        Assert.Equal(ScaleProtocols.WeightScale, DeviceClassifier.Classify(new[] { GattUuids.WeightScaleService }));

    [Fact]
    public void Body_composition_only() =>
        Assert.Equal(ScaleProtocols.BodyComposition, DeviceClassifier.Classify(new[] { GattUuids.BodyCompositionService }));

    [Fact]
    public void Both_services_combine_flags() =>
        Assert.Equal(ScaleProtocols.WeightScale | ScaleProtocols.BodyComposition,
            DeviceClassifier.Classify(new[] { GattUuids.BodyCompositionService, GattUuids.WeightScaleService }));

    [Fact]
    public void Unrelated_services_are_ignored() =>
        Assert.Equal(ScaleProtocols.None, DeviceClassifier.Classify(new[] { GattUuids.FromShort(0x180F), Guid.NewGuid() }));

    [Fact]
    public void Default_filter_lists_exactly_the_two_standard_services() =>
        Assert.Equal(new[] { GattUuids.WeightScaleService, GattUuids.BodyCompositionService },
            DeviceClassifier.StandardServiceFilter);
}
