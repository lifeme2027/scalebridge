using System.Collections;
using ScaleBridge.Linux.Bluez;
using ScaleBridge.Linux.Tests.Support;

namespace ScaleBridge.Linux.Tests;

public class BluezPropsTests
{
    [Fact]
    public void Getters_ReturnFalse_OnWrongTypeOrMissing()
    {
        var props = BluezTestData.Props(
            ("Name", 1),
            ("Powered", "yes"),
            ("RSSI", 1),
            ("Value", "nope"),
            ("Flags", 12),
            ("ManufacturerData", "nope"),
            ("ServiceData", 3));

        Assert.False(BluezProps.TryGetString(props, "Name", out _));
        Assert.False(BluezProps.TryGetBool(props, "Powered", out _));
        Assert.False(BluezProps.TryGetInt16(props, "RSSI", out _));
        Assert.False(BluezProps.TryGetBytes(props, "Value", out _));
        Assert.False(BluezProps.TryGetStrings(props, "Flags", out _));
        Assert.False(BluezProps.TryGetManufacturerData(props, "ManufacturerData", out _));
        Assert.False(BluezProps.TryGetServiceData(props, "ServiceData", out _));
        Assert.False(BluezProps.TryGetString(props, "Missing", out _));
        Assert.False(BluezProps.TryGetString(null, "Name", out _));

        var ok = BluezTestData.Props(("Name", "Scale"), ("Powered", true), ("RSSI", (short)-40), ("Value", new byte[] { 1 }));
        Assert.True(BluezProps.TryGetString(ok, "Name", out var name) && name == "Scale");
        Assert.True(BluezProps.TryGetBool(ok, "Powered", out var powered) && powered);
        Assert.True(BluezProps.TryGetInt16(ok, "RSSI", out var rssi) && rssi == -40);
        Assert.True(BluezProps.TryGetBytes(ok, "Value", out var bytes) && bytes[0] == 1);
    }

    [Fact]
    public void ManufacturerData_And_ServiceData_AcceptSeveralForms()
    {
        var payload = new byte[] { 0x0A };
        IReadOnlyDictionary<ushort, byte[]> ready = new Dictionary<ushort, byte[]> { [0x004C] = payload };
        var props = BluezTestData.Props(("ManufacturerData", ready));
        Assert.True(BluezProps.TryGetManufacturerData(props, "ManufacturerData", out var map));
        Assert.Equal(payload, map[0x004C]);

        var pairs = new List<KeyValuePair<ushort, byte[]>> { new(0x0001, payload) };
        props = BluezTestData.Props(("ManufacturerData", pairs));
        Assert.True(BluezProps.TryGetManufacturerData(props, "ManufacturerData", out map));
        Assert.Equal(payload, map[1]);

        var table = new Hashtable { [(ushort)0x00FF] = payload };
        props = BluezTestData.Props(("ManufacturerData", table));
        Assert.True(BluezProps.TryGetManufacturerData(props, "ManufacturerData", out map));
        Assert.Equal(payload, map[0x00FF]);

        var loose = new object[] { new KeyValuePair<object, object?>((short)12, payload) };
        props = BluezTestData.Props(("ManufacturerData", loose));
        Assert.True(BluezProps.TryGetManufacturerData(props, "ManufacturerData", out map));
        Assert.Equal(payload, map[12]);

        IReadOnlyDictionary<string, byte[]> services = new Dictionary<string, byte[]> { ["180f"] = payload };
        props = BluezTestData.Props(("ServiceData", services));
        Assert.True(BluezProps.TryGetServiceData(props, "ServiceData", out var svc));
        Assert.Equal(payload, svc["180f"]);

        var servicePairs = new[] { new KeyValuePair<string, byte[]>("181d", payload) };
        props = BluezTestData.Props(("ServiceData", servicePairs));
        Assert.True(BluezProps.TryGetServiceData(props, "ServiceData", out svc));
        Assert.Equal(payload, svc["181d"]);
    }

    [Fact]
    public void Redaction_HidesLastTwoOctets()
    {
        const string path = "/org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF/service0010";
        var redacted = BluezRedaction.Path(path);
        Assert.Equal("/org/bluez/hci0/dev_AA_BB_CC_DD_**_**/service0010", redacted);
        Assert.DoesNotContain("EE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("FF", redacted, StringComparison.Ordinal);

        var address = BluezRedaction.Address("aa:bb:cc:dd:ee:ff");
        Assert.Equal("aa:bb:cc:dd:**:**", address);
        Assert.DoesNotContain("ee", address, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ff", address, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<redacted>", BluezRedaction.Address("../x"));
    }
}
