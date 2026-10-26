using System.Collections;
using System.Text.RegularExpressions;

namespace ScaleBridge.Linux.Bluez;

/// <summary>对已规范化属性字典的宽容读取：缺失或类型不符时返回 false，不抛异常。</summary>
internal static class BluezProps
{
    /// <summary>读取字符串。</summary>
    public static bool TryGetString(IReadOnlyDictionary<string, object?>? props, string key, out string value)
    {
        value = "";
        if (!TryRaw(props, key, out var raw) || raw is not string text)
            return false;
        value = text;
        return true;
    }

    /// <summary>读取布尔值。</summary>
    public static bool TryGetBool(IReadOnlyDictionary<string, object?>? props, string key, out bool value)
    {
        value = false;
        if (!TryRaw(props, key, out var raw) || raw is not bool flag)
            return false;
        value = flag;
        return true;
    }

    /// <summary>读取 Int16。仅接受 short。</summary>
    public static bool TryGetInt16(IReadOnlyDictionary<string, object?>? props, string key, out short value)
    {
        value = 0;
        if (!TryRaw(props, key, out var raw) || raw is not short number)
            return false;
        value = number;
        return true;
    }

    /// <summary>读取字节数组。仅接受 byte[]。</summary>
    public static bool TryGetBytes(IReadOnlyDictionary<string, object?>? props, string key, out byte[] value)
    {
        value = [];
        if (!TryRaw(props, key, out var raw) || raw is not byte[] bytes)
            return false;
        value = bytes;
        return true;
    }

    /// <summary>读取字符串数组。接受 string[] 或仅含字符串的序列。</summary>
    public static bool TryGetStrings(IReadOnlyDictionary<string, object?>? props, string key, out string[] value)
    {
        value = [];
        if (!TryRaw(props, key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case string[] texts:
                value = texts;
                return true;
            case IEnumerable<string> texts:
                value = texts.ToArray();
                return true;
            case object[] items when AllStrings(items, out var parsed):
                value = parsed;
                return true;
            case IEnumerable items when AllStrings(items, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>读取 ManufacturerData（公司标识 → 载荷）。</summary>
    public static bool TryGetManufacturerData(
        IReadOnlyDictionary<string, object?>? props,
        string key,
        out IReadOnlyDictionary<ushort, byte[]> value)
    {
        value = EmptyManufacturer;
        if (!TryRaw(props, key, out var raw) || raw is null)
            return false;
        return TryManufacturer(raw, out value);
    }

    /// <summary>读取 ServiceData（UUID 字符串 → 载荷）。</summary>
    public static bool TryGetServiceData(
        IReadOnlyDictionary<string, object?>? props,
        string key,
        out IReadOnlyDictionary<string, byte[]> value)
    {
        value = EmptyService;
        if (!TryRaw(props, key, out var raw) || raw is null)
            return false;
        return TryService(raw, out value);
    }

    private static readonly IReadOnlyDictionary<ushort, byte[]> EmptyManufacturer =
        new Dictionary<ushort, byte[]>();

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyService =
        new Dictionary<string, byte[]>();

    private static bool TryRaw(IReadOnlyDictionary<string, object?>? props, string key, out object? raw)
    {
        raw = null;
        if (props is null || key is null)
            return false;
        return props.TryGetValue(key, out raw) && raw is not null;
    }

    private static bool AllStrings(IEnumerable items, out string[] value)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            if (item is not string text)
            {
                value = [];
                return false;
            }

            list.Add(text);
        }

        value = list.ToArray();
        return true;
    }

    private static bool TryManufacturer(object raw, out IReadOnlyDictionary<ushort, byte[]> value)
    {
        value = EmptyManufacturer;
        if (raw is IReadOnlyDictionary<ushort, byte[]> ready)
        {
            value = ready;
            return true;
        }

        if (raw is IEnumerable<KeyValuePair<ushort, byte[]>> pairs)
        {
            value = pairs.ToDictionary(p => p.Key, p => p.Value);
            return true;
        }

        if (raw is not IEnumerable enumerable || raw is string)
            return false;

        var map = new Dictionary<ushort, byte[]>();
        foreach (var entry in enumerable)
        {
            if (!TryPair(entry, out var keyObj, out var valObj))
                return false;
            if (!TryCompanyId(keyObj, out var id) || valObj is not byte[] payload)
                return false;
            map[id] = payload;
        }

        value = map;
        return true;
    }

    private static bool TryService(object raw, out IReadOnlyDictionary<string, byte[]> value)
    {
        value = EmptyService;
        if (raw is IReadOnlyDictionary<string, byte[]> ready)
        {
            value = ready;
            return true;
        }

        if (raw is IEnumerable<KeyValuePair<string, byte[]>> pairs)
        {
            value = pairs.ToDictionary(p => p.Key, p => p.Value);
            return true;
        }

        if (raw is not IEnumerable enumerable || raw is string)
            return false;

        var map = new Dictionary<string, byte[]>();
        foreach (var entry in enumerable)
        {
            if (!TryPair(entry, out var keyObj, out var valObj))
                return false;
            if (keyObj is not string id || valObj is not byte[] payload)
                return false;
            map[id] = payload;
        }

        value = map;
        return true;
    }

    private static bool TryPair(object entry, out object? key, out object? val)
    {
        key = null;
        val = null;
        switch (entry)
        {
            case KeyValuePair<ushort, byte[]> us:
                key = us.Key;
                val = us.Value;
                return true;
            case KeyValuePair<string, byte[]> ss:
                key = ss.Key;
                val = ss.Value;
                return true;
            case KeyValuePair<object, object?> oo:
                key = oo.Key;
                val = oo.Value;
                return true;
            case DictionaryEntry de:
                key = de.Key;
                val = de.Value;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCompanyId(object? key, out ushort id)
    {
        switch (key)
        {
            case ushort u:
                id = u;
                return true;
            case short s when s >= 0:
                id = (ushort)s;
                return true;
            default:
                id = 0;
                return false;
        }
    }
}

/// <summary>日志与异常文本用的地址脱敏。</summary>
internal static class BluezRedaction
{
    private static readonly Regex DevSegment = new(
        @"dev_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}_[0-9A-Fa-f]{2}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MacAddress = new(
        @"^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>把路径中的 dev_XX_XX_XX_XX_XX_XX 收成 dev_XX_XX_XX_XX_**_**。</summary>
    public static string Path(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? "";
        return DevSegment.Replace(path, static m => m.Value[..16] + "**_**");
    }

    /// <summary>把 MAC 收成 AA:BB:CC:DD:**:**；无法识别时返回 &lt;redacted&gt;。</summary>
    public static string Address(string address)
    {
        if (string.IsNullOrEmpty(address) || !MacAddress.IsMatch(address))
            return "<redacted>";
        return address[..12] + "**:**";
    }
}
