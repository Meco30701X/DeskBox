using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class NeteaseCloudMusicCdpClientTests
{
    // Verbatim reply captured from NetEase CloudMusic 3.1.42 (Chrome 91 DevTools protocol) for a
    // Runtime.evaluate call made with returnByValue=true. The remote value is wrapped in a
    // {"type","value"} object; reading the wrapper as the value itself made every read return null,
    // which left the music tile at position 0 with every control disabled.
    private const string SnapshotReply = """"{"id":1,"result":{"result":{"type":"string","value":"{\"progress\":\"00:11 / 03:38\",\"isPlaying\":false,\"positionSec\":10.9,\"durationSec\":218,\"title\":\"因为爱情\",\"artist\":\"陈奕迅 / 王菲\",\"album\":\"Stranger Under My Skin\",\"coverUrl\":\"https://p4.music.126.net/9-PMVvRGgYw4uZYwteDkiQ==/109951171057274.jpg\",\"playingMode\":\"playRandom\"}"}}}"""";

    [Fact]
    public void TryParseEvaluateResponse_UnwrapsStringReturnValue()
    {
        Assert.True(NeteaseCloudMusicCdpClient.TryParseEvaluateResponse(SnapshotReply, 1, out string? value));

        Assert.NotNull(value);
        using var document = System.Text.Json.JsonDocument.Parse(value!);
        var root = document.RootElement;
        Assert.Equal(10.9, root.GetProperty("positionSec").GetDouble());
        Assert.Equal(218, root.GetProperty("durationSec").GetDouble());
        Assert.Equal("因为爱情", root.GetProperty("title").GetString());
        Assert.False(root.GetProperty("isPlaying").GetBoolean());
        Assert.Equal("playRandom", root.GetProperty("playingMode").GetString());
    }

    [Theory]
    [InlineData(""""{"id":7,"result":{"result":{"type":"boolean","value":true}}}"""", "true")]
    [InlineData(""""{"id":7,"result":{"result":{"type":"boolean","value":false}}}"""", "false")]
    [InlineData(""""{"id":7,"result":{"result":{"type":"number","value":150.052}}}"""", "150.052")]
    [InlineData(""""{"id":7,"result":{"result":{"type":"string","value":"02:30 / 03:53"}}}"""", "02:30 / 03:53")]
    [InlineData(""""{"id":7,"result":{"result":{"type":"undefined"}}}"""", null)]
    [InlineData(""""{"id":7,"result":{"result":{"type":"object","value":null}}}"""", null)]
    [InlineData(""""{"id":7,"error":{"code":-32601,"message":"Not found"}}"""", null)]
    [InlineData(""""{"id":7,"result":{"result":{"type":"object","className":"ReferenceError"},"exceptionDetails":{"text":"Uncaught"}}}"""", null)]
    [InlineData("not json", null)]
    public void TryParseEvaluateResponse_MapsRemoteValues(string reply, string? expected)
    {
        Assert.True(NeteaseCloudMusicCdpClient.TryParseEvaluateResponse(reply, 7, out string? value));

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(""""{"id":8,"result":{"result":{"type":"string","value":"other request"}}}"""")]
    [InlineData(""""{"method":"Runtime.consoleAPICalled","params":{"type":"log"}}"""")]
    [InlineData(""""{"result":{"result":{"type":"string","value":"no id"}}}"""")]
    [InlineData(""""{"id":"7","result":{"result":{"type":"string","value":"string id"}}}"""")]
    public void TryParseEvaluateResponse_IgnoresMessagesThatAreNotTheReplyForThisRequest(string reply)
    {
        Assert.False(NeteaseCloudMusicCdpClient.TryParseEvaluateResponse(reply, 7, out string? value));

        Assert.Null(value);
    }
    [Theory]
    [InlineData("play", "tid_playbar_play_btn")]
    [InlineData("pause", "tid_playbar_play_btn")]
    [InlineData("previous", "tid_playbar_prev_btn")]
    [InlineData("next", "tid_playbar_next_btn")]
    public void BuildControlExpression_WrapsSelectorInDoubleQuotes(string operation, string testId)
    {
        string selector = NeteaseCloudMusicCdpClient.BuildControlSelector(operation);
        string expression = NeteaseCloudMusicCdpClient.BuildControlExpression(selector);

        // The selector contains single quotes; a JavaScript string literal built from it must
        // use double quotes or Runtime.evaluate fails with a SyntaxError and no click happens.
        Assert.Equal($"[data-testid='{testId}']", selector);
        string expectedCall = "document.querySelector(\"" + selector + "\");";
        Assert.Contains(expectedCall, expression, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('" + selector + "');", expression, StringComparison.Ordinal);
        Assert.Contains("button.click();", expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("seek")]
    [InlineData("shuffle")]
    public void BuildControlSelector_RejectsUnsupportedOperations(string operation)
    {
        Assert.Equal(string.Empty, NeteaseCloudMusicCdpClient.BuildControlSelector(operation));
    }

    [Fact]
    public void TryParseEvaluateResponse_IsTheReplyShapeControlAndSnapshotRelyOn()
    {
        // TryControlAsync treats the literal string "true" as a successful click.
        Assert.True(NeteaseCloudMusicCdpClient.TryParseEvaluateResponse(
            """"{"id":3,"result":{"result":{"type":"boolean","value":true}}}"""",
            3,
            out string? value));

        Assert.Equal("true", value);
    }
    [Theory]
    [InlineData("playRandom", MusicPlaybackMode.Shuffle)]
    [InlineData("playCycle", MusicPlaybackMode.Repeat)]
    [InlineData("playOneCycle", MusicPlaybackMode.Repeat)]
    [InlineData("playOrder", MusicPlaybackMode.Normal)]
    [InlineData("playFm", MusicPlaybackMode.Normal)]
    [InlineData("", MusicPlaybackMode.Normal)]
    public void MapPlayingMode_MapsNetEaseModes(string neteaseMode, MusicPlaybackMode expected)
    {
        Assert.Equal(expected, NeteaseCloudMusicCdpClient.MapPlayingMode(neteaseMode));
    }

    [Theory]
    [InlineData("playRandom")]
    [InlineData("playCycle")]
    [InlineData("playOrder")]
    public void BuildSwitchModeExpression_TargetsTheRequestedMode(string targetMode)
    {
        string expression = NeteaseCloudMusicCdpClient.BuildSwitchModeExpression(targetMode);

        Assert.Contains("type: 'playing/switchPlayingMode'", expression, StringComparison.Ordinal);
        Assert.Contains("playingMode: '" + targetMode + "'", expression, StringComparison.Ordinal);
    }
    [Fact]
    public void BuildSeekExpression_DispatchesThroughEveryAnchor()
    {
        string expression = NeteaseCloudMusicCdpClient.BuildSeekExpression("119.9");

        Assert.Contains("type: 'playing/setPlayingPosition'", expression, StringComparison.Ordinal);
        Assert.Contains("payload: { duration: 119.9 }", expression, StringComparison.Ordinal);
        foreach (string anchor in new[] { "#page_pc_mini_bar", "[data-testid='tid_playbar_play_btn']" })
        {
            Assert.Contains(anchor, expression, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Client_NeverInjectsSyntheticMouseInputIntoNetEase()
    {
        // A press or drag sent at computed coordinates lands on whatever NetEase shows at that moment.
        // It was observed clicking through links out of the app, so the client must stay free of it.
        string source = ReadProductionSource("Services/NeteaseCloudMusicCdpClient.cs");

        Assert.DoesNotContain("Input.dispatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dispatchMouseEvent", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatchKeyEvent", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadProductionSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "DeskBox", relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("DeskBox source root not found", relativePath);
    }
}
