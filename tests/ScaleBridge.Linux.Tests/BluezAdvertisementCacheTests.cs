using ScaleBridge.Core.Protocol;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezAdvertisementCacheTests
{
    private const string Hci0 = "/org/bluez/hci0";
    private const string Hci1 = "/org/bluez/hci1";
    private static readonly DateTimeOffset Seen = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Apply_MergesDeltas_AndInvalidatedRemovesProps()
    {
        var cache = new BluezAdvertisementCache(Hci0);
        var device = BluezTestData.Device(Hci0, "aa:bb:cc:dd:ee:ff", name: "Scale", rssi: -40);
        Assert.True(cache.Apply(device));

        Assert.True(cache.Apply(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Device1,
            BluezTestData.Props(("RSSI", (short)-55), ("UUIDs", new[] { BluezTestData.Uuid(GattUuids.WeightScaleService) })),
            [])));

        Assert.True(cache.TryBuild(device.Path, Seen, out var built));
        Assert.Equal("AA:BB:CC:DD:EE:FF", built.Id);
        Assert.Equal("Scale", built.Name);
        Assert.Equal(-55, built.Rssi);
        Assert.Equal(GattUuids.WeightScaleService, Assert.Single(built.ServiceUuids));

        Assert.True(cache.Apply(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Device1,
            BluezTestData.Props(),
            ["Name", "RSSI"])));

        Assert.True(cache.TryBuild(device.Path, Seen, out built));
        Assert.Null(built.Name);
        Assert.Null(built.Rssi);
        Assert.False(cache.Apply(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Device1,
            BluezTestData.Props(("Alias", "ignored")),
            [])));
    }

    [Fact]
    public void Name_IgnoresAlias_ServiceDataKeysJoinUuids_AndManufacturerBytesAreCopied()
    {
        var cache = new BluezAdvertisementCache(Hci0);
        var payload = new byte[] { 1, 2, 3 };
        var weight = GattUuids.WeightScaleService;
        var body = GattUuids.BodyCompositionService;
        var device = BluezTestData.Device(
            Hci0,
            "AA:BB:CC:DD:EE:01",
            uuids: [BluezTestData.Uuid(weight), "not-a-uuid", BluezTestData.Uuid(weight)],
            manufacturerData: new Dictionary<ushort, byte[]> { [0x1234] = payload },
            serviceData: new Dictionary<string, byte[]>
            {
                [BluezTestData.Uuid(body)] = [9],
                ["zzzz"] = [8],
                [BluezTestData.Uuid(weight)] = [7],
            });

        var props = new Dictionary<string, object?>(device.Interfaces[BluezNames.Device1])
        {
            ["Alias"] = "AliasName",
        };
        Assert.True(cache.Apply(new BluezObject(device.Path, new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            [BluezNames.Device1] = props,
        })));

        Assert.True(cache.TryBuild(device.Path, Seen, out var built));
        Assert.Null(built.Name);
        Assert.False(built.IsDemo);
        Assert.Equal(Seen, built.SeenAt);
        Assert.Equal([weight, body], built.ServiceUuids);
        Assert.Equal(payload, built.ManufacturerData[0x1234]);
        payload[0] = 99;
        Assert.Equal(1, built.ManufacturerData[0x1234][0]);
        Assert.Null(built.Rssi);
    }

    [Fact]
    public void Matches_NullOrEmptyFilterIsAll_OtherwiseAnyOverlap()
    {
        var cache = new BluezAdvertisementCache(Hci0);
        var weight = GattUuids.WeightScaleService;
        var device = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:02", uuids: [BluezTestData.Uuid(weight)]);
        cache.Apply(device);

        Assert.True(cache.Matches(device.Path, null));
        Assert.True(cache.Matches(device.Path, []));
        Assert.True(cache.Matches(device.Path, [weight, Guid.NewGuid()]));
        Assert.False(cache.Matches(device.Path, [GattUuids.BodyCompositionService]));
        Assert.False(cache.Matches(Hci0 + "/dev_00_00_00_00_00_00", null));
    }

    [Fact]
    public void OtherAdapter_AndInvalidAddress_AreRejected()
    {
        var cache = new BluezAdvertisementCache(Hci0);
        var other = BluezTestData.Device(Hci1, "AA:BB:CC:DD:EE:03", name: "Other");
        Assert.False(cache.Apply(other));
        Assert.False(cache.TryBuild(other.Path, Seen, out _));

        var path = Hci0 + "/dev_AA_BB_CC_DD_EE_04";
        var bad = new BluezObject(path, new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            [BluezNames.Device1] = BluezTestData.Props(("Address", "nope"), ("Adapter", Hci0), ("Name", "X")),
        });
        Assert.True(cache.Apply(bad));
        Assert.False(cache.TryBuild(path, Seen, out _));

        var prefixOnly = new BluezObject(path, new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            [BluezNames.Device1] = BluezTestData.Props(("Address", "aa:bb:cc:dd:ee:04"), ("Name", "Ok")),
        });
        Assert.True(cache.Apply(prefixOnly));
        Assert.True(cache.TryBuild(path, Seen, out var built));
        Assert.Equal("AA:BB:CC:DD:EE:04", built.Id);

        cache.Remove(path);
        Assert.False(cache.TryBuild(path, Seen, out _));
    }

    [Fact]
    public void NonDeviceInterface_IsNotRelevant()
    {
        var cache = new BluezAdvertisementCache(Hci0);
        var device = BluezTestData.Device(Hci0, "AA:BB:CC:DD:EE:05", name: "Scale");
        cache.Apply(device);
        Assert.False(cache.Apply(new BluezPropertiesChanged(
            device.Path,
            BluezNames.Adapter1,
            BluezTestData.Props(("Name", "nope")),
            ["RSSI"])));
        Assert.True(cache.TryBuild(device.Path, Seen, out var built));
        Assert.Equal("Scale", built.Name);
    }
}
