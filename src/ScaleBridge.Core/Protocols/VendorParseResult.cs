namespace ScaleBridge.Core.Protocols;

/// <summary>厂商帧解析失败的原因。名称稳定（共享向量 JSON 使用这些名字）。</summary>
public enum VendorParseErrorCode
{
    None = 0,
    /// <summary>数据短于该帧的固定长度（不解析半包）。</summary>
    TooShort,
    /// <summary>单位标志互相冲突（例如磅与斤同时置位），无法确定单位。</summary>
    AmbiguousUnit,
    /// <summary>服务 UUID 不属于本解析器支持的厂商帧。</summary>
    UnsupportedService,
    /// <summary>校验和错误（预留给后续厂商）。</summary>
    BadChecksum,
    /// <summary>帧头不匹配（预留给后续厂商）。</summary>
    BadHeader,
    /// <summary>字段取值非法（预留给后续厂商）。</summary>
    InvalidValue,
}

/// <summary>厂商帧解析成功但值得注意的情况。名称稳定。</summary>
public enum VendorParseWarningCode
{
    /// <summary>数据比该帧固定长度更长，多余字节被忽略。</summary>
    TrailingBytes,
    /// <summary>时间戳字节存在，但不构成合法日期时间，已按「未知」处理。</summary>
    InvalidTimestamp,
}

/// <summary>厂商帧解析结果：成功带 Value，失败带 ErrorCode + 可读信息。解析器绝不抛异常。</summary>
public sealed class VendorParseResult<T> where T : class
{
    private static readonly IReadOnlyList<VendorParseWarningCode> NoWarnings = Array.Empty<VendorParseWarningCode>();

    private VendorParseResult(T? value, VendorParseErrorCode code, string? message, IReadOnlyList<VendorParseWarningCode> warnings)
    {
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
        Warnings = warnings;
    }

    public T? Value { get; }
    public VendorParseErrorCode ErrorCode { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<VendorParseWarningCode> Warnings { get; }
    public bool IsSuccess => ErrorCode == VendorParseErrorCode.None && Value is not null;

    public static VendorParseResult<T> Ok(T value, IReadOnlyList<VendorParseWarningCode>? warnings = null) =>
        new(value, VendorParseErrorCode.None, null, warnings is { Count: > 0 } ? warnings : NoWarnings);

    public static VendorParseResult<T> Fail(VendorParseErrorCode code, string message) =>
        new(null, code, message, NoWarnings);
}
