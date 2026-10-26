using ScaleBridge.Core.Protocol;

namespace ScaleBridge.Core.Tests.Protocol;

/// <summary>解析器对任意字节输入都不得抛异常（固定种子，保证可复现）。</summary>
public class RobustnessTests
{
    [Fact]
    public void Random_bytes_never_throw_in_any_parser()
    {
        var rng = new Random(20261026);
        for (int i = 0; i < 20000; i++)
        {
            var buf = new byte[rng.Next(0, 40)];
            rng.NextBytes(buf);
            var ex = Record.Exception(() =>
            {
                WeightScaleParser.ParseMeasurement(buf);
                WeightScaleParser.ParseFeature(buf);
                BodyCompositionParser.ParseMeasurement(buf);
                BodyCompositionParser.ParseFeature(buf);
                GattDateTime.TryParse(buf);
            });
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Successful_results_always_carry_a_value_and_failures_never_do()
    {
        var rng = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 24)];
            rng.NextBytes(buf);
            var w = WeightScaleParser.ParseMeasurement(buf);
            Assert.Equal(w.IsSuccess, w.Value is not null);
            Assert.Equal(w.IsSuccess, w.ErrorCode == ParseErrorCode.None);
            var b = BodyCompositionParser.ParseMeasurement(buf);
            Assert.Equal(b.IsSuccess, b.Value is not null);
        }
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    public void Feature_parsers_accept_any_32_bit_pattern(uint raw)
    {
        var bytes = BitConverter.GetBytes(raw);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Assert.True(WeightScaleParser.ParseFeature(bytes).IsSuccess);
        Assert.True(BodyCompositionParser.ParseFeature(bytes).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Feature_parsers_reject_short_input(int len)
    {
        Assert.Equal(ParseErrorCode.TooShort, WeightScaleParser.ParseFeature(new byte[len]).ErrorCode);
        Assert.Equal(ParseErrorCode.TooShort, BodyCompositionParser.ParseFeature(new byte[len]).ErrorCode);
    }
}
