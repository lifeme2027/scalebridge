namespace ScaleBridge.Core.Protocol;

/// <summary>
/// GATT「Date Time」结构（7 字节，小端）：year(uint16) month day hours minutes seconds(各 uint8)。
/// Weight Scale / Body Composition 的 Time Stamp 字段使用同一格式，且规范规定年/月/日不得为 0（未知）。
/// </summary>
public static class GattDateTime
{
    public const int Size = 7;

    /// <summary>解析 7 字节；不是合法日期时间（年 1582–9999、月日时分秒越界或日历上不存在）返回 null。结果 Kind=Unspecified（设备本地时间，不带时区）。</summary>
    public static DateTime? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size) return null;
        int year = data[0] | (data[1] << 8);
        int month = data[2], day = data[3], hour = data[4], minute = data[5], second = data[6];
        if (year is < 1582 or > 9999) return null;
        if (month is < 1 or > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        if (hour > 23 || minute > 59 || second > 59) return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    public static byte[] Encode(DateTime value) => new[]
    {
        (byte)(value.Year & 0xFF), (byte)(value.Year >> 8),
        (byte)value.Month, (byte)value.Day, (byte)value.Hour, (byte)value.Minute, (byte)value.Second,
    };
}
