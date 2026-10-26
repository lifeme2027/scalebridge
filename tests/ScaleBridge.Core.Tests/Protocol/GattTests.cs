using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests.Protocol;

public class GattDateTimeTests
{
    [Fact]
    public void Parses_little_endian_year()
    {
        // 2026 = 0x07EA → EA 07
        var dt = GattDateTime.TryParse(new byte[] { 0xEA, 0x07, 10, 26, 8, 30, 59 });
        Assert.Equal(new DateTime(2026, 10, 26, 8, 30, 59), dt);
        Assert.Equal(DateTimeKind.Unspecified, dt!.Value.Kind);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0)]          // 全 0 = unknown
    [InlineData(2026, 0, 1, 0, 0, 0, 0)]       // 月 0
    [InlineData(2026, 13, 1, 0, 0, 0, 0)]      // 月 13
    [InlineData(2026, 1, 0, 0, 0, 0, 0)]       // 日 0
    [InlineData(2026, 2, 29, 0, 0, 0, 0)]      // 2026 非闰年
    [InlineData(2026, 4, 31, 0, 0, 0, 0)]
    [InlineData(2026, 1, 1, 24, 0, 0, 0)]
    [InlineData(2026, 1, 1, 0, 60, 0, 0)]
    [InlineData(2026, 1, 1, 0, 0, 60, 0)]
    [InlineData(1581, 1, 1, 0, 0, 0, 0)]       // 规范年份下限 1582
    [InlineData(10000, 1, 1, 0, 0, 0, 0)]      // 上限 9999
    public void Rejects_impossible_dates(int year, int mo, int d, int h, int mi, int s, int _)
    {
        var b = new byte[] { (byte)(year & 0xFF), (byte)(year >> 8), (byte)mo, (byte)d, (byte)h, (byte)mi, (byte)s };
        Assert.Null(GattDateTime.TryParse(b));
    }

    [Theory]
    [InlineData(2024, 2, 29)]
    [InlineData(1582, 1, 1)]
    [InlineData(9999, 12, 31)]
    public void Accepts_boundary_dates(int y, int m, int d)
    {
        var b = GattDateTime.Encode(new DateTime(y, m, d, 23, 59, 59));
        Assert.Equal(new DateTime(y, m, d, 23, 59, 59), GattDateTime.TryParse(b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Short_input_returns_null_not_exception(int len) =>
        Assert.Null(GattDateTime.TryParse(new byte[len]));

    [Fact]
    public void Encode_round_trips()
    {
        var v = new DateTime(2031, 7, 4, 1, 2, 3);
        Assert.Equal(v, GattDateTime.TryParse(GattDateTime.Encode(v)));
    }
}

public class GattUuidsTests
{
    [Fact]
    public void Short_uuids_expand_over_the_bluetooth_base_uuid()
    {
        Assert.Equal(new Guid("0000181d-0000-1000-8000-00805f9b34fb"), GattUuids.WeightScaleService);
        Assert.Equal(new Guid("0000181b-0000-1000-8000-00805f9b34fb"), GattUuids.BodyCompositionService);
        Assert.Equal(new Guid("00002a9d-0000-1000-8000-00805f9b34fb"), GattUuids.WeightMeasurement);
        Assert.Equal(new Guid("00002a9e-0000-1000-8000-00805f9b34fb"), GattUuids.WeightScaleFeature);
        Assert.Equal(new Guid("00002a9c-0000-1000-8000-00805f9b34fb"), GattUuids.BodyCompositionMeasurement);
        Assert.Equal(new Guid("00002a9b-0000-1000-8000-00805f9b34fb"), GattUuids.BodyCompositionFeature);
    }

    [Theory]
    [InlineData((ushort)0x181D)]
    [InlineData((ushort)0x2A9C)]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0xFFFF)]
    public void Short_round_trips(ushort s) => Assert.Equal(s, GattUuids.TryGetShort(GattUuids.FromShort(s)));

    [Fact]
    public void Vendor_uuid_has_no_short_form() =>
        Assert.Null(GattUuids.TryGetShort(new Guid("0000ffe0-1234-1000-8000-00805f9b34fb")));

    [Theory]
    [InlineData("181D")]
    [InlineData("0x181d")]
    [InlineData("  181D ")]
    [InlineData("0000181d-0000-1000-8000-00805f9b34fb")]
    public void TryParse_accepts_common_spellings(string text)
    {
        Assert.True(GattUuids.TryParse(text, out var g));
        Assert.Equal(GattUuids.WeightScaleService, g);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("181")]
    [InlineData("zzzz")]
    public void TryParse_rejects_garbage(string? text) => Assert.False(GattUuids.TryParse(text, out _));
}
