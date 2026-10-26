using ScaleBridge.Core.Protocol;
using ScaleBridge.Core.Tests.Support;

namespace ScaleBridge.Core.Tests;

/// <summary>把 NOTES.md「解析策略与已知分歧」中有争议的解析策略各钉一条明确断言（Q3/Q4/Q5/Q8/Q9/Q20/Q21），与 shared-vectors 互为补充。</summary>
public class ParsePolicyTests
{
    private static ParseResult<WeightScaleMeasurementData> Wss(string hex) => WeightScaleParser.ParseMeasurement(Hex.Parse(hex));
    private static ParseResult<BodyCompositionMeasurementData> Bcs(string hex) => BodyCompositionParser.ParseMeasurement(Hex.Parse(hex));

    // Q20：userId 0xFF 与「字段缺失」在解析结果里都是 null；原始字节需要时由调用方保留
    [Fact]
    public void Q20_user_id_0xFF_and_absent_user_id_both_give_null()
    {
        Assert.Null(Wss("04 00 10 FF").Value!.UserId);
        Assert.Null(Wss("00 00 10").Value!.UserId);
        Assert.Equal(0, Wss("04 00 10 00").Value!.UserId);   // 0 是合法用户号，不是「未知」
        Assert.Equal(254, Wss("04 00 10 FE").Value!.UserId);
    }

    // Q21：错误码优先级
    [Fact]
    public void Q21_error_priority_is_multipacket_then_tooshort_then_truncated()
    {
        Assert.Equal(ParseErrorCode.MultiPacketNotSupported, Bcs("00 10").ErrorCode);      // 只看 Flags 就能认出多包
        Assert.Equal(ParseErrorCode.TooShort, Bcs("00").ErrorCode);                         // 连 Flags 都不全
        Assert.Equal(ParseErrorCode.TooShort, Bcs("00 00 D7").ErrorCode);                   // 不足最小长度 4
        Assert.Equal(ParseErrorCode.Truncated, Bcs("02 00 D7 00 EA 07").ErrorCode);         // 声明了 7 字节时间戳却放不下
        Assert.Equal(ParseErrorCode.TooShort, Wss("00 59").ErrorCode);
        Assert.Equal(ParseErrorCode.Truncated, Wss("02 59 36 EA 07").ErrorCode);
    }

    // Q8：测量不成功时，Flags 声明的其余字段（即使字节不存在）被忽略并给警告，不判错
    [Fact]
    public void Q8_unsuccessful_with_declared_but_absent_fields_is_accepted_with_warning()
    {
        var r = Wss("08 FF FF");
        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.MeasurementUnsuccessful);
        Assert.Contains(ParseWarningCode.FieldsIgnoredMeasurementUnsuccessful, r.Warnings);
        Assert.Null(r.Value.Bmi);
    }

    // Q9：可选字段 0xFFFF = 未知（null + 警告），不当作字面值
    [Fact]
    public void Q9_optional_field_0xFFFF_is_unknown_not_a_literal()
    {
        var r = Wss("08 00 10 FF FF 10 03");     // BMI = 0xFFFF
        Assert.True(r.IsSuccess);
        Assert.Null(r.Value!.Bmi);
        Assert.Contains(ParseWarningCode.OptionalFieldMarkedUnknown, r.Warnings);
        Assert.NotNull(r.Value.Height);           // 同包的 Height 不受影响，字段顺序不变
    }

    // Q3：保留位只警告，且不改变字段解析顺序
    [Fact]
    public void Q3_reserved_bits_warn_but_do_not_change_field_layout()
    {
        var plain = Wss("04 00 10 07").Value!;
        var withRfu = Wss("F4 00 10 07");          // 位 4–7 置位
        Assert.True(withRfu.IsSuccess);
        Assert.Contains(ParseWarningCode.ReservedBitsSet, withRfu.Warnings);
        Assert.Equal(plain.Weight, withRfu.Value!.Weight);
        Assert.Equal(7, withRfu.Value.UserId);
    }

    // Q5：多余字节只警告，绝不当作下一个字段
    [Fact]
    public void Q5_trailing_bytes_warn_and_are_never_parsed_as_fields()
    {
        var r = Wss("00 00 10 07 07 07");
        Assert.True(r.IsSuccess);
        Assert.Contains(ParseWarningCode.TrailingBytes, r.Warnings);
        Assert.Null(r.Value!.UserId);
    }

    // Q4：任何构不成合法日期时间的时间戳（含全 0）一律 null + InvalidTimestamp，其余字段照常解析
    [Theory]
    [InlineData("02 00 10 00 00 00 00 00 00 00")]   // 全 0
    [InlineData("02 00 10 EA 07 00 01 00 00 00")]   // 月 = 0
    [InlineData("02 00 10 EA 07 02 1E 00 00 00")]   // 2 月 30 日
    [InlineData("02 00 10 2D 06 0A 1A 00 00 00")]   // 年 1581（< 1582）
    public void Q4_invalid_timestamps_give_null_with_warning(string hex)
    {
        var r = Wss(hex);
        Assert.True(r.IsSuccess);
        Assert.Null(r.Value!.TimeStamp);
        Assert.Contains(ParseWarningCode.InvalidTimestamp, r.Warnings);
        Assert.NotNull(r.Value.Weight);
    }

    [Fact]
    public void Q4_year_1582_is_the_valid_lower_bound()
        => Assert.Equal(new DateTime(1582, 10, 26), Wss("02 00 10 2E 06 0A 1A 00 00 00").Value!.TimeStamp);
}
