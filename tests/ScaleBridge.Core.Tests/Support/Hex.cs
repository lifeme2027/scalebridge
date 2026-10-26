namespace ScaleBridge.Core.Tests.Support;

internal static class Hex
{
    /// <summary>"0F 00-59 36" / "0F0059" 等 → 字节；空串 → 空数组。</summary>
    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) throw new FormatException("odd number of hex digits: " + text);
        return Convert.FromHexString(clean);
    }
}
