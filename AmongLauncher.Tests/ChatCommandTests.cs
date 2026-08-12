using AmongLauncher.Services.Lobby;
using Xunit;

namespace AmongLauncher.Tests;

public class ChatCommandTests
{
    [Fact]
    public void Detect_RepostCommand_ReturnsRepost()
    {
        var result = ChatCommandDetector.Detect("/repost");
        Assert.Equal(ChatCommand.Repost, result.Command);
        Assert.Null(result.Argument);
    }

    [Fact]
    public void Detect_RepostCommandCaseInsensitive_ReturnsRepost()
    {
        var result = ChatCommandDetector.Detect("/REPOST");
        Assert.Equal(ChatCommand.Repost, result.Command);
    }

    [Fact]
    public void Detect_RepostCommandMixedCase_ReturnsRepost()
    {
        var result = ChatCommandDetector.Detect("/Repost");
        Assert.Equal(ChatCommand.Repost, result.Command);
    }

    [Fact]
    public void Detect_DisbandCommand_ReturnsDisband()
    {
        var result = ChatCommandDetector.Detect("/disband");
        Assert.Equal(ChatCommand.Disband, result.Command);
        Assert.Null(result.Argument);
    }

    [Fact]
    public void Detect_DisbandCommandCaseInsensitive_ReturnsDisband()
    {
        var result = ChatCommandDetector.Detect("/DISBAND");
        Assert.Equal(ChatCommand.Disband, result.Command);
    }

    [Fact]
    public void Detect_DisbandCommandMixedCase_ReturnsDisband()
    {
        var result = ChatCommandDetector.Detect("/Disband");
        Assert.Equal(ChatCommand.Disband, result.Command);
    }

    [Fact]
    public void Detect_PostLobbyCommand_ReturnsPostLobby()
    {
        var result = ChatCommandDetector.Detect("/postlobby");
        Assert.Equal(ChatCommand.PostLobby, result.Command);
        Assert.Null(result.Argument);
    }

    [Fact]
    public void Detect_PostLobbyCommandWithArgument_ReturnsArgument()
    {
        var result = ChatCommandDetector.Detect("/postlobby Looking for players");
        Assert.Equal(ChatCommand.PostLobby, result.Command);
        Assert.Equal("Looking for players", result.Argument);
    }

    [Fact]
    public void Detect_PostLobbyCommandCaseInsensitive_ReturnsPostLobby()
    {
        var result = ChatCommandDetector.Detect("/POSTLOBBY");
        Assert.Equal(ChatCommand.PostLobby, result.Command);
    }

    [Fact]
    public void Detect_NonCommandMessage_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("Hello everyone!");
        Assert.Equal(ChatCommand.None, result.Command);
        Assert.Null(result.Argument);
    }

    [Fact]
    public void Detect_EmptyMessage_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_WhitespaceMessage_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("   ");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_NullMessage_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect(null!);
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_MessageWithCommandNotAtStart_IgnoresCommand()
    {
        var result = ChatCommandDetector.Detect("say /repost");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_RepostWithExtraText_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("/repost please");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_DisbandWithExtraText_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("/disband now");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_CommandWithLeadingWhitespace_ReturnsCommand()
    {
        var result = ChatCommandDetector.Detect("  /repost");
        Assert.Equal(ChatCommand.Repost, result.Command);
    }

    [Fact]
    public void Detect_CommandWithTrailingWhitespace_ReturnsCommand()
    {
        var result = ChatCommandDetector.Detect("/repost  ");
        Assert.Equal(ChatCommand.Repost, result.Command);
    }

    [Fact]
    public void Detect_CommandWithBothWhitespace_ReturnsCommand()
    {
        var result = ChatCommandDetector.Detect("  /disband  ");
        Assert.Equal(ChatCommand.Disband, result.Command);
    }

    [Fact]
    public void Detect_PartialCommand_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("/re");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_UnknownCommand_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("/unknown");
        Assert.Equal(ChatCommand.None, result.Command);
    }

    [Fact]
    public void Detect_SlashWithoutCommand_ReturnsNone()
    {
        var result = ChatCommandDetector.Detect("/");
        Assert.Equal(ChatCommand.None, result.Command);
    }
}
