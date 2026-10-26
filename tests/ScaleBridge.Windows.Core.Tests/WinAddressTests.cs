namespace ScaleBridge.Windows.Core.Tests;

public class WinAddressTests
{
    [Fact]
    public void Format_UsesUpperCaseColonSeparatedBytes() =>
        Assert.Equal("AA:BB:CC:DD:EE:FF", WinAddress.Format(0xAABBCCDDEEFF));

    [Fact]
    public void Format_PadsLeadingZeroBytes() =>
        Assert.Equal("00:00:00:00:00:01", WinAddress.Format(1));

    [Fact]
    public void Format_RejectsMoreThan48Bits() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WinAddress.Format(0x1_0000_0000_0000UL));

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("  aabbccddeeff ")]
    public void TryParse_AcceptsCommonForms(string text)
    {
        Assert.True(WinAddress.TryParse(text, out var a));
        Assert.Equal(0xAABBCCDDEEFFUL, a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AA:BB-CC:DD:EE:FF")]
    [InlineData("AABBCCDDEEF")]
    [InlineData("not an address")]
    public void TryParse_RejectsGarbage(string? text) => Assert.False(WinAddress.TryParse(text, out _));

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        const ulong v = 0x0123456789ABUL;
        Assert.True(WinAddress.TryParse(WinAddress.Format(v), out var back));
        Assert.Equal(v, back);
    }
}
