using AmongLauncher.Services;
using Xunit;

namespace AmongLauncher.Tests;

public class DeepLinkTests
{
    [Fact]
    public void TryParseJoin_QueryForm_ParsesCode()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=ABCDEF");
        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
    }

    [Fact]
    public void TryParseJoin_LowercaseCode_NormalizesToUpper()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=abcdef");
        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
    }

    [Fact]
    public void TryParseJoin_MixedCaseCode_NormalizesToUpper()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=aBcDeF");
        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
    }

    [Fact]
    public void TryParseJoin_PathForm_ParsesCode()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join/ABCDEF");
        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
    }

    [Fact]
    public void TryParseJoin_PathFormLowercase_NormalizesToUpper()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join/abcdef");
        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
    }

    [Fact]
    public void TryParseJoin_InvalidUri_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("not-a-valid-uri");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_WrongScheme_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amongus://join?code=ABCDEF");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_WrongHost_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://install?code=ABCDEF");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_MissingCode_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_EmptyCode_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_TooShortCode_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=ABC");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_TooLongCode_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=ABCDEFGHIJ");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_SpecialChars_ReturnsNull()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=AB!EF");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseJoin_URLEncodedCode_ParsesCorrectly()
    {
        var result = DeepLinkHandler.TryParseJoin("amonglauncher://join?code=ABC%20DEF");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_Install_SingleMod_ParsesUrl()
    {
        var result = DeepLinkHandler.Parse("amongus-launcher://install?mods=https://example.com/mod1.dll");
        Assert.Single(result);
        Assert.Equal("https://example.com/mod1.dll", result[0].Url);
        Assert.Equal("mod1.dll", result[0].FileName);
    }

    [Fact]
    public void Parse_Install_MultipleMods_ParsesAllUrls()
    {
        var result = DeepLinkHandler.Parse(
            "amongus-launcher://install?mods=https://example.com/mod1.dll,https://example.com/mod2.dll");
        Assert.Equal(2, result.Count);
        Assert.Equal("https://example.com/mod1.dll", result[0].Url);
        Assert.Equal("mod1.dll", result[0].FileName);
        Assert.Equal("https://example.com/mod2.dll", result[1].Url);
        Assert.Equal("mod2.dll", result[1].FileName);
    }

    [Fact]
    public void Parse_Install_NoModsParam_ReturnsEmpty()
    {
        var result = DeepLinkHandler.Parse("amongus-launcher://install");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_Install_EmptyModsParam_ReturnsEmpty()
    {
        var result = DeepLinkHandler.Parse("amongus-launcher://install?mods=");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NonInstallHost_ReturnsEmpty()
    {
        var result = DeepLinkHandler.Parse("amongus-launcher://join?mods=https://example.com/mod1.dll");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidUri_ReturnsEmpty()
    {
        var result = DeepLinkHandler.Parse("not-a-valid-uri");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_Install_URLEncodedMods_ParsesCorrectly()
    {
        var result = DeepLinkHandler.Parse(
            "amongus-launcher://install?mods=https%3A%2F%2Fexample.com%2Fmod1.dll");
        Assert.Single(result);
        Assert.Equal("https://example.com/mod1.dll", result[0].Url);
    }

    [Fact]
    public void Parse_Install_InvalidModUrl_SkipsInvalid()
    {
        var result = DeepLinkHandler.Parse(
            "amongus-launcher://install?mods=not-a-url,https://example.com/mod.dll");
        Assert.Single(result);
        Assert.Equal("https://example.com/mod.dll", result[0].Url);
    }

    [Fact]
    public void NormalizeRoomCode_ValidCode_ReturnsUpperCase()
    {
        var result = DeepLinkHandler.NormalizeRoomCode("abc123");
        Assert.NotNull(result);
        Assert.Equal("ABC123", result);
    }

    [Fact]
    public void NormalizeRoomCode_Null_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode(null));
    }

    [Fact]
    public void NormalizeRoomCode_Empty_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode(""));
    }

    [Fact]
    public void NormalizeRoomCode_Whitespace_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode("   "));
    }

    [Fact]
    public void NormalizeRoomCode_TooShort_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode("ABC"));
    }

    [Fact]
    public void NormalizeRoomCode_TooLong_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode("ABCDEFGHIJ"));
    }

    [Fact]
    public void NormalizeRoomCode_WithSpaces_ReturnsNull()
    {
        Assert.Null(DeepLinkHandler.NormalizeRoomCode("AB CDEF"));
    }

    [Fact]
    public void Parse_Install_TrailingComma_IgnoresEmpty()
    {
        var result = DeepLinkHandler.Parse(
            "amongus-launcher://install?mods=https://example.com/mod1.dll,");
        Assert.Single(result);
    }

    [Fact]
    public void Parse_Install_ThreeMods_ParsesAll()
    {
        var result = DeepLinkHandler.Parse(
            "amongus-launcher://install?mods=https://example.com/a.dll,https://example.com/b.dll,https://example.com/c.dll");
        Assert.Equal(3, result.Count);
        Assert.Equal("a.dll", result[0].FileName);
        Assert.Equal("b.dll", result[1].FileName);
        Assert.Equal("c.dll", result[2].FileName);
    }
}
