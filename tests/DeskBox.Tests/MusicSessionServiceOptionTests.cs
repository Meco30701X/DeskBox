using DeskBox.Services;
namespace DeskBox.Tests;
public sealed class MusicSessionServiceOptionTests
{
    [Theory]
    [InlineData("QQMusic.exe", "QQ音乐")]
    [InlineData("cloudmusic.exe", "网易云音乐")]
    [InlineData("KuGou.exe", "酷狗音乐")]
    [InlineData("msedge.exe", "Microsoft Edge")]
    [InlineData("chrome.exe", "Google Chrome")]
    [InlineData("firefox.exe", "Mozilla Firefox")]
    [InlineData("Contoso.Player.exe", "Contoso.Player")]
    public void GetSourceDisplayName_ProducesFriendlyPlayerNames(
        string sourceAppUserModelId,
        string expected)
    {
        Assert.Equal(expected, MusicSessionService.GetSourceDisplayName(sourceAppUserModelId));
    }
    [Fact]
    public void CreateSessionId_DisambiguatesSessionsFromTheSameApplication()
    {
        string first = MusicSessionService.CreateSessionId("chrome.exe", 0);
        string second = MusicSessionService.CreateSessionId("chrome.exe", 1);
        Assert.NotEqual(first, second);
        Assert.StartsWith("chrome.exe", first, StringComparison.Ordinal);
        Assert.StartsWith("chrome.exe", second, StringComparison.Ordinal);
    }
    [Fact]
    public void DisambiguateSourceDisplayNames_NumbersOnlyDuplicateSources()
    {
        IReadOnlyList<string> result = MusicSessionService.DisambiguateSourceDisplayNames(
        [
            "Google Chrome",
            "QQ音乐",
            "Google Chrome"
        ]);
        Assert.Equal(
        [
            "Google Chrome (1)",
            "QQ音乐",
            "Google Chrome (2)"
        ], result);
    }
    [Theory]
    [InlineData("晴天 - 周杰伦", "cloudmusic", "晴天", "周杰伦")]
    [InlineData("稻香 - 周杰伦 - 网易云音乐", "cloudmusic", "稻香", "周杰伦")]
    [InlineData("网易云音乐", "cloudmusic", "", "")]
    [InlineData("七里香 - 周杰伦", "qqmusic", "七里香", "周杰伦")]
    [InlineData("QQ音乐", "qqmusic", "", "")]
    [InlineData("泡沫 - G.E.M.邓紫棋 - 酷狗音乐", "kugou", "泡沫", "G.E.M.邓紫棋")]
    [InlineData("酷狗音乐", "kugou", "", "")]
    public void ParseWindowTitle_ExtractsTitleAndArtistForSupportedPlayers(
        string rawWindowTitle,
        string playerKey,
        string expectedTitle,
        string expectedArtist)
    {
        DesktopPlayerDefinition definition = DesktopMusicFallbackProvider.SupportedPlayers
            .First(p => p.PlayerKey == playerKey);
        (string title, string artist) = DesktopMusicFallbackProvider.ParseWindowTitle(rawWindowTitle, definition);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
    }
    [Theory]
    [InlineData(true, false, false, false, "play", true)]
    [InlineData(false, true, false, false, "pause", true)]
    [InlineData(false, false, true, false, "previous", true)]
    [InlineData(false, false, false, true, "next", true)]
    [InlineData(false, false, false, false, "play", false)]
    [InlineData(true, true, true, true, "seek", false)]
    public void IsTransportControlEnabled_OnlyAcceptsMatchingTransportCapability(
        bool canPlay,
        bool canPause,
        bool canGoPrevious,
        bool canGoNext,
        string operation,
        bool expected)
    {
        Assert.Equal(
            expected,
            MusicSessionService.IsTransportControlEnabled(
                canPlay,
                canPause,
                canGoPrevious,
                canGoNext,
                operation));
    }

    [Fact]
    public void CreateFallbackSessionId_IsRecognizedAsFallbackSession()
    {
        string sessionId = DesktopMusicFallbackProvider.CreateFallbackSessionId("cloudmusic.exe", 0);
        Assert.True(DesktopMusicFallbackProvider.IsFallbackSessionId(sessionId));
        Assert.False(DesktopMusicFallbackProvider.IsFallbackSessionId("cloudmusic.exe\u001F0"));
    }
}
