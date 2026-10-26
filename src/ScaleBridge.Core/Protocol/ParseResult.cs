namespace ScaleBridge.Core.Protocol;

/// <summary>解析失败的原因。名称是稳定的（共享向量 JSON 里用的就是这些名字）。</summary>
public enum ParseErrorCode
{
    None = 0,
    /// <summary>数据短于该特征的最小长度（连必选字段都不全）。</summary>
    TooShort,
    /// <summary>Flags 声明存在的可选字段，数据里却放不下（截断包）。</summary>
    Truncated,
    /// <summary>Body Composition 多包测量（Multiple Packet Measurement 位）——阶段1暂未支持。</summary>
    MultiPacketNotSupported,
}

/// <summary>解析成功但值得注意的情况。名称稳定。</summary>
public enum ParseWarningCode
{
    /// <summary>Flags / Feature 里的保留位（RFU）被置位。</summary>
    ReservedBitsSet,
    /// <summary>数据比 Flags 描述的更长，多余字节被忽略。</summary>
    TrailingBytes,
    /// <summary>时间戳字段存在，但年/月/日/时/分/秒不构成合法日期时间，已按「未知」处理。</summary>
    InvalidTimestamp,
    /// <summary>测量不成功(0xFFFF)时，除时间戳与用户 ID 外的可选字段按规范应被禁用，Flags 却声明了它们，已忽略。</summary>
    FieldsIgnoredMeasurementUnsuccessful,
    /// <summary>可选字段取值 0xFFFF。规范没有为该字段定义此特殊值；本实现保守地按「未知」(null) 处理。</summary>
    OptionalFieldMarkedUnknown,
    /// <summary>Feature 里的分辨率码是保留值(8–15 / 4–7)，按「未知」处理。</summary>
    ReservedResolutionCode,
}

/// <summary>解析结果：成功带 Value，失败带 ErrorCode + 可读信息。解析器绝不抛异常。</summary>
public sealed class ParseResult<T> where T : class
{
    private static readonly IReadOnlyList<ParseWarningCode> NoWarnings = Array.Empty<ParseWarningCode>();

    private ParseResult(T? value, ParseErrorCode code, string? message, IReadOnlyList<ParseWarningCode> warnings)
    {
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
        Warnings = warnings;
    }

    public T? Value { get; }
    public ParseErrorCode ErrorCode { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<ParseWarningCode> Warnings { get; }
    public bool IsSuccess => ErrorCode == ParseErrorCode.None && Value is not null;

    public static ParseResult<T> Ok(T value, IReadOnlyList<ParseWarningCode>? warnings = null) =>
        new(value, ParseErrorCode.None, null, warnings is { Count: > 0 } ? warnings : NoWarnings);

    public static ParseResult<T> Fail(ParseErrorCode code, string message) =>
        new(null, code, message, NoWarnings);
}
