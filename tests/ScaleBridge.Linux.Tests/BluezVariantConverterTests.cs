using ScaleBridge.Linux.Bluez;
using Tmds.DBus.Protocol;

namespace ScaleBridge.Linux.Tests;

public class BluezVariantConverterTests
{
    [Fact]
    public void Scalars_AreMappedToNormalizedClrTypes()
    {
        Assert.Equal((byte)7, BluezVariantConverter.ToClr((byte)7));
        Assert.Equal(true, BluezVariantConverter.ToClr(true));
        Assert.Equal((short)-61, BluezVariantConverter.ToClr((short)-61));
        Assert.Equal((ushort)76, BluezVariantConverter.ToClr((ushort)76));
        Assert.Equal(5u, BluezVariantConverter.ToClr(5u));
        Assert.Equal("hci0", BluezVariantConverter.ToClr("hci0"));
    }

    [Fact]
    public void ObjectPath_BecomesString()
    {
        Assert.Equal("/org/bluez/hci0", BluezVariantConverter.ToClr(new ObjectPath("/org/bluez/hci0")));
    }

    [Fact]
    public void NestedVariant_IsUnwrapped()
    {
        Assert.Equal((short)-5, BluezVariantConverter.ToClr(VariantValue.Variant((short)-5)));
    }

    [Fact]
    public void Arrays_ByteString_AndObjectPath()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(BluezVariantConverter.ToClr(VariantValue.Array(new byte[] { 1, 2, 3 }))));
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(BluezVariantConverter.ToClr(VariantValue.Array(new[] { "a", "b" }))));
        Assert.Equal(
            new[] { "/x", "/y" },
            Assert.IsType<string[]>(BluezVariantConverter.ToClr(VariantValue.Array(new[] { new ObjectPath("/x"), new ObjectPath("/y") }))));
    }

    [Fact]
    public void UnsupportedTypes_ReturnNull()
    {
        Assert.Null(BluezVariantConverter.ToClr(1.5d));
        Assert.Null(BluezVariantConverter.ToClr(12L));
        Assert.Null(BluezVariantConverter.ToClr(VariantValue.Array(new[] { 1, 2 })));
        Assert.Null(BluezVariantConverter.ToClr(VariantValue.Struct("a", "b")));
    }

    [Fact]
    public void ToProperties_OmitsUnsupportedEntries()
    {
        var raw = new Dictionary<string, VariantValue>
        {
            ["RSSI"] = (short)-40,
            ["Weird"] = 2.5d,
            ["Name"] = "Scale",
        };

        var props = BluezVariantConverter.ToProperties(raw);

        Assert.Equal(2, props.Count);
        Assert.Equal((short)-40, props["RSSI"]);
        Assert.Equal("Scale", props["Name"]);
        Assert.False(props.ContainsKey("Weird"));
    }
}
