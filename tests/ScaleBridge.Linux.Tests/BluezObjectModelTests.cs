using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezObjectModelTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Hci1 = "/org/bluez/hci1";
    private const string Mac = "AA:BB:CC:DD:EE:FF";

    [Fact]
    public void FindAdapter_PicksFirstByPath_AndPreferred_AndMissingPreferred()
    {
        var objects = new[]
        {
            BluezTestData.Adapter(Hci1, "hci1", true),
            BluezTestData.Adapter(Hci0, "hci0", true),
        };

        Assert.Equal(Hci0, BluezObjectModel.FindAdapterPath(objects));
        Assert.Equal(Hci1, BluezObjectModel.FindAdapterPath(objects, "hci1"));
        Assert.Null(BluezObjectModel.FindAdapterPath(objects, "hci9"));
        Assert.Null(BluezObjectModel.FindAdapterPath([]));
    }

    [Fact]
    public void DeviceId_RoundTrips_AndRejectsInjection()
    {
        var path = BluezObjectModel.DeviceIdToPath(Hci0, "aa:bb:cc:dd:ee:ff");
        Assert.Equal(Hci0 + "/dev_AA_BB_CC_DD_EE_FF", path);
        Assert.Equal(Mac, BluezObjectModel.PathToDeviceId(path));

        Assert.Throws<ArgumentException>(() => BluezObjectModel.DeviceIdToPath(Hci0, "../x"));
        Assert.Throws<ArgumentException>(() => BluezObjectModel.DeviceIdToPath(Hci0, "AA:BB"));
        Assert.Throws<ArgumentException>(() => BluezObjectModel.DeviceIdToPath("../org/bluez/hci0", Mac));
        Assert.Throws<ArgumentException>(() => BluezObjectModel.PathToDeviceId(Hci0 + "/dev_not_a_mac"));
    }

    [Fact]
    public void GetServices_MapsFlags_OnStandardScale()
    {
        var objects = BluezTestData.StandardScale(Hci0, Mac);
        var devicePath = objects[0].Path;
        Assert.Equal(Hci0 + "/dev_AA_BB_CC_DD_EE_FF/service0010/char0011", objects[2].Path);

        var services = BluezObjectModel.GetServices(objects, devicePath);
        Assert.Equal(2, services.Count);

        var weight = Assert.Single(services, s => s.Uuid == GattUuids.WeightScaleService);
        Assert.Equal(GattCharacteristicProperties.Indicate, CharOf(weight, GattUuids.WeightMeasurement).Properties);
        Assert.Equal(GattCharacteristicProperties.Read, CharOf(weight, GattUuids.WeightScaleFeature).Properties);

        var body = Assert.Single(services, s => s.Uuid == GattUuids.BodyCompositionService);
        Assert.Equal(GattCharacteristicProperties.Indicate, CharOf(body, GattUuids.BodyCompositionMeasurement).Properties);
        Assert.Equal(GattCharacteristicProperties.Read, CharOf(body, GattUuids.BodyCompositionFeature).Properties);
    }

    [Fact]
    public void FindCharacteristic_HitMissIsolationAndCase()
    {
        var scale = BluezTestData.StandardScale(Hci0, Mac);
        var other = BluezTestData.StandardScale(Hci0, "11:22:33:44:55:66");
        var all = scale.Concat(other).ToArray();
        var device = scale[0].Path;

        var hit = BluezObjectModel.FindCharacteristicPath(
            all, device, GattUuids.WeightScaleService, GattUuids.WeightMeasurement);
        Assert.Equal(device + "/service0010/char0011", hit);

        Assert.Null(BluezObjectModel.FindCharacteristicPath(
            all, device, GattUuids.WeightScaleService, GattUuids.BodyCompositionFeature));

        var otherHit = BluezObjectModel.FindCharacteristicPath(
            all, other[0].Path, GattUuids.WeightScaleService, GattUuids.WeightMeasurement);
        Assert.Equal(other[0].Path + "/service0010/char0011", otherHit);
        Assert.NotEqual(hit, otherHit);

        var upper = scale.Select(o => RewriteUuids(o)).ToArray();
        Assert.Equal(hit, BluezObjectModel.FindCharacteristicPath(
            upper, device, GattUuids.WeightScaleService, GattUuids.WeightMeasurement));
    }

    [Fact]
    public void ParseFlags_IgnoresUnknown_AndCombinesKnown()
    {
        var flags = BluezObjectModel.ParseFlags(
            ["READ", " write-without-response ", "broadcast", "notify", null!]);
        Assert.Equal(
            GattCharacteristicProperties.Read
            | GattCharacteristicProperties.WriteWithoutResponse
            | GattCharacteristicProperties.Notify,
            flags);
        Assert.Equal(GattCharacteristicProperties.None, BluezObjectModel.ParseFlags([]));
        Assert.Equal(GattCharacteristicProperties.Write, BluezObjectModel.ParseFlags(["write"]));
    }

    [Fact]
    public void TryParseUuid_AcceptsGuid_RejectsShortAndBlank()
    {
        Assert.True(BluezObjectModel.TryParseUuid(GattUuids.WeightScaleService.ToString("D").ToUpperInvariant(), out var uuid));
        Assert.Equal(GattUuids.WeightScaleService, uuid);
        Assert.False(BluezObjectModel.TryParseUuid("181D", out _));
        Assert.False(BluezObjectModel.TryParseUuid("  ", out _));
        Assert.False(BluezObjectModel.TryParseUuid(null, out _));
    }

    private static GattCharacteristicInfo CharOf(GattServiceInfo service, Guid uuid) =>
        Assert.Single(service.Characteristics, c => c.Uuid == uuid);

    private static BluezObject RewriteUuids(BluezObject obj)
    {
        var interfaces = obj.Interfaces.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value.ToDictionary(
                p => p.Key,
                p => p.Key == "UUID" && p.Value is string text ? (object?)text.ToUpperInvariant() : p.Value));
        return obj with { Interfaces = interfaces };
    }
}
