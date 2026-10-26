using Tmds.DBus.Protocol;

namespace ScaleBridge.Linux.Bluez;

/// <summary>把 D-Bus VariantValue 归一化为 IBluezBus 约定的 CLR 类型；不认识的类型返回 null（调用方丢弃该属性）。</summary>
internal static class BluezVariantConverter
{
    /// <summary>a{sv} 转为属性表；无法归一化的属性被省略。</summary>
    public static IReadOnlyDictionary<string, object?> ToProperties(IReadOnlyDictionary<string, VariantValue> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            var value = ToClr(pair.Value);
            if (value is not null)
                result[pair.Key] = value;
        }

        return result;
    }

    /// <summary>
    /// byte/bool/short/ushort/uint/string/object path(string)、ay(byte[])、as 与 ao(string[])、
    /// a{qv}(IReadOnlyDictionary&lt;ushort,byte[]&gt;)、a{sv}(IReadOnlyDictionary&lt;string,byte[]&gt;)。
    /// </summary>
    public static object? ToClr(VariantValue value)
    {
        switch (value.Type)
        {
            case VariantValueType.Variant:
                return ToClr(value.GetVariantValue());
            case VariantValueType.Byte:
                return value.GetByte();
            case VariantValueType.Bool:
                return value.GetBool();
            case VariantValueType.Int16:
                return value.GetInt16();
            case VariantValueType.UInt16:
                return value.GetUInt16();
            case VariantValueType.UInt32:
                return value.GetUInt32();
            case VariantValueType.String:
                return value.GetString();
            case VariantValueType.ObjectPath:
                return value.GetObjectPathAsString();
            case VariantValueType.Array:
                return ArrayToClr(value);
            case VariantValueType.Dictionary:
                return DictionaryToClr(value);
            default:
                return null;
        }
    }

    private static object? ArrayToClr(VariantValue value)
    {
        switch (value.ItemType)
        {
            case VariantValueType.Byte:
                return (byte[])value.GetArray<byte>().Clone();
            case VariantValueType.String:
                return (string[])value.GetArray<string>().Clone();
            case VariantValueType.ObjectPath:
            {
                var count = value.Count;
                var paths = new string[count];
                for (var i = 0; i < count; i++)
                    paths[i] = value.GetItem(i).GetObjectPathAsString();
                return paths;
            }
            default:
                return null;
        }
    }

    private static object? DictionaryToClr(VariantValue value)
    {
        var count = value.Count;
        switch (value.KeyType)
        {
            case VariantValueType.UInt16:
            {
                var map = new Dictionary<ushort, byte[]>(count);
                for (var i = 0; i < count; i++)
                {
                    var entry = value.GetDictionaryEntry(i);
                    if (ToClr(entry.Value) is byte[] bytes)
                        map[entry.Key.GetUInt16()] = bytes;
                }

                return map;
            }
            case VariantValueType.String:
            {
                var map = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var entry = value.GetDictionaryEntry(i);
                    if (ToClr(entry.Value) is byte[] bytes)
                        map[entry.Key.GetString()] = bytes;
                }

                return map;
            }
            default:
                return null;
        }
    }
}
