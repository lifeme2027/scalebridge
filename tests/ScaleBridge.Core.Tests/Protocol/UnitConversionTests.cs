using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests.Protocol;

public class UnitConversionTests
{
    [Theory]
    [InlineData(0, 0.000)]
    [InlineData(1, 0.005)]
    [InlineData(2, 0.010)]
    [InlineData(14001, 70.005)]
    [InlineData(20000, 100.000)]
    [InlineData(65534, 327.670)]
    public void Si_mass_is_raw_times_5_grams(int raw, double expectedKg)
    {
        var m = UnitConversion.DecodeMass((ushort)raw, UnitSystem.Si);
        Assert.Equal((decimal)expectedKg, m.Kilograms);
        Assert.Equal(m.Kilograms, m.Original);
        Assert.Equal(raw, m.Raw);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(15432)]
    [InlineData(65534)]
    public void Imperial_mass_is_exact_decimal(int raw)
    {
        var m = UnitConversion.DecodeMass((ushort)raw, UnitSystem.Imperial);
        Assert.Equal(raw * 0.01m, m.Original);
        Assert.Equal(raw * 0.01m * 0.45359237m, m.Kilograms);
    }

    [Fact]
    public void Imperial_one_pound_is_exactly_the_international_pound()
    {
        Assert.Equal(0.45359237m, UnitConversion.DecodeMass(100, UnitSystem.Imperial).Kilograms);
    }

    [Fact]
    public void Imperial_conversion_has_no_accumulated_error_over_every_raw_value()
    {
        // 两位小数 × 八位小数 = 十位小数，decimal 能精确表示：逐个值与整数运算结果比较（raw * 45359237 / 10^10）。
        for (int raw = 0; raw <= 0xFFFE; raw++)
        {
            var kg = UnitConversion.DecodeMass((ushort)raw, UnitSystem.Imperial).Kilograms;
            Assert.Equal((long)raw * 45359237L / 10_000_000_000m, kg);
        }
    }

    [Fact]
    public void Summing_many_small_masses_does_not_drift()
    {
        decimal sum = 0;
        for (int i = 0; i < 10_000; i++) sum += UnitConversion.DecodeMass(1, UnitSystem.Imperial).Kilograms;
        Assert.Equal(10_000 * 0.0045359237m, sum);
    }

    [Theory]
    [InlineData(1750, 1.750)]
    [InlineData(1, 0.001)]
    [InlineData(2500, 2.500)]
    public void Si_height_is_raw_times_one_millimetre(int raw, double expectedM)
    {
        var h = UnitConversion.DecodeHeight((ushort)raw, UnitSystem.Si);
        Assert.Equal((decimal)expectedM, h.Meters);
        Assert.Equal(h.Meters, h.Original);
    }

    [Theory]
    [InlineData(690, "69.0", "1.7526")]
    [InlineData(1, "0.1", "0.00254")]
    [InlineData(720, "72.0", "1.8288")]
    public void Imperial_height_is_raw_times_a_tenth_inch(int raw, string inches, string meters)
    {
        var h = UnitConversion.DecodeHeight((ushort)raw, UnitSystem.Imperial);
        Assert.Equal(decimal.Parse(inches, System.Globalization.CultureInfo.InvariantCulture), h.Original);
        Assert.Equal(decimal.Parse(meters, System.Globalization.CultureInfo.InvariantCulture), h.Meters);
    }

    [Theory]
    [InlineData("70.005", WeightUnit.Kg, "70.005")]
    [InlineData("70", WeightUnit.Kg, "70.0")]
    [InlineData("0.45359237", WeightUnit.Lb, "1.00")]
    [InlineData("70", WeightUnit.Lb, "154.32")]
    [InlineData("0", WeightUnit.Lb, "0.00")]
    public void FormatWeight_is_culture_invariant(string kg, WeightUnit unit, string expected)
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(expected, UnitConversion.FormatWeight(decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture), unit));
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void ForDisplay_rounds_half_away_from_zero_and_does_not_mutate_the_stored_value()
    {
        decimal stored = 70.0055m;
        Assert.Equal(70.006m, UnitConversion.ForDisplay(stored, WeightUnit.Kg));
        Assert.Equal(70.0055m, stored);
    }

    [Fact]
    public void Round_trip_lb_kg_lb_is_stable_to_display_precision()
    {
        for (int raw = 0; raw <= 60000; raw += 37)
        {
            var lb = raw * 0.01m;
            var back = Math.Round(UnitConversion.KilogramsToPounds(UnitConversion.PoundsToKilograms(lb)), 2);
            Assert.Equal(lb, back);
        }
    }
}
