using System.Globalization;

namespace ScaleBridge.Windows;

/// <summary>WinRT 的 BluetoothAddress（ulong，低 48 位）与 Core 里 MAC 风格设备 Id（"AA:BB:CC:DD:EE:FF"）互转。</summary>
public static class WinAddress
{
    public const ulong MaxAddress = 0xFFFF_FFFF_FFFFUL;

    public static string Format(ulong address)
    {
        if (address > MaxAddress)
            throw new ArgumentOutOfRangeException(nameof(address), "蓝牙地址最多 48 位。");
        return string.Create(17, address, static (span, a) =>
        {
            const string hex = "0123456789ABCDEF";
            for (var i = 0; i < 6; i++)
            {
                var b = (byte)(a >> (8 * (5 - i)));
                var pos = i * 3;
                span[pos] = hex[b >> 4];
                span[pos + 1] = hex[b & 0xF];
                if (i < 5) span[pos + 2] = ':';
            }
        });
    }

    /// <summary>接受 "AA:BB:CC:DD:EE:FF"、"AA-BB-CC-DD-EE-FF"、"AABBCCDDEEFF"（大小写均可，首尾空白忽略）。</summary>
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        ReadOnlySpan<char> digits;
        if (s.Length == 17 && (s[2] is ':' or '-'))
        {
            var sep = s[2];
            Span<char> buf = stackalloc char[12];
            for (var i = 0; i < 6; i++)
            {
                if (i < 5 && s[i * 3 + 2] != sep) return false;
                buf[i * 2] = s[i * 3];
                buf[i * 2 + 1] = s[i * 3 + 1];
            }
            return ulong.TryParse(buf, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
        }
        if (s.Length == 12)
        {
            digits = s;
            return ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
        }
        return false;
    }
}
