using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

internal sealed record NeteaseCdpPlaybackSnapshot(
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    string ProgressText,
    string Title,
    string Artist,
    string Album,
    string CoverUrl,
    MusicPlaybackMode PlaybackMode);

internal sealed class NeteaseCloudMusicCdpClient : IDisposable
{
    private const int DefaultPort = 9223;
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private ClientWebSocket? _webSocket;
    private string? _targetWebSocketUrl;
    private int _nextMessageId;
    private bool _isDisposed;

    public bool IsConnected => _webSocket is { State: WebSocketState.Open };

    public async Task<NeteaseCdpPlaybackSnapshot?> TryGetSnapshotAsync()
    {
        const string expression = """
            (() => {
              const progress = Array.from(document.querySelectorAll('.cmd-button-content'))
                .map(x => (x.textContent || '').trim())
                .find(x => /^\d{1,2}:\d{2}(?::\d{2})?\s*\/\s*\d{1,2}:\d{2}(?::\d{2})?$/.test(x)) || '';
              const playLog = document.querySelector('[data-testid="tid_playbar_play_btn"]')?.getAttribute('data-log') || '';
              const input = document.querySelector('#page_pc_mini_bar input[type="range"]');
              const inputVal = input ? Number(input.value || 0) : 0;
              const inputMax = input ? Number(input.max || 0) : 0;
              const titleNode = document.querySelector('[data-testid="tid_minibar_title"] .main-title .title');
              const authorNode = document.querySelector('[data-testid="tid_minibar_title"] .author');
              const coverNode = document.querySelector('[data-testid="tid_btn_pc_minibar_vinyl"] img');

              let title = titleNode ? (titleNode.textContent || '').trim() : '';
              let artist = authorNode ? (authorNode.textContent || '').trim() : '';
              let album = '';
              let coverUrl = '';
              let playingState = null;
              let playingMode = '';
              let fiberDuration = 0;

              try {
                const barEl = document.getElementById('page_pc_mini_bar');
                const fiberKey = barEl ? Object.keys(barEl).find(k => k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$')) : null;
                let f = fiberKey ? barEl[fiberKey] : null;
                while (f) {
                  const p = f.memoizedProps;
                  if (p) {
                    if (!title && typeof p.resourceName === 'string') title = p.resourceName.trim();
                    if (!artist) {
                      if (typeof p.resourceArtists === 'string') artist = p.resourceArtists.trim();
                      else if (Array.isArray(p.resourceArtists)) {
                        artist = p.resourceArtists.map(a => a && a.name ? a.name.trim() : '').filter(Boolean).join(' / ');
                      }
                    }
                    if (!album) {
                      album = p.curPlaying?.track?.album?.name || p.curTrack?.album?.name || '';
                    }
                    if (!coverUrl && typeof p.resourceCoverUrl === 'string') {
                      coverUrl = p.resourceCoverUrl;
                    }
                    if (playingState === null && typeof p.playingState === 'number') {
                      playingState = p.playingState;
                    }
                    if (!playingMode && typeof p.playingMode === 'string') {
                      playingMode = p.playingMode;
                    }
                    if (!(fiberDuration > 0) && typeof p.resourceDuration === 'number' && p.resourceDuration > 0) {
                      fiberDuration = p.resourceDuration;
                    }
                  }
                  f = f.return;
                }
              } catch (e) {}

              if (!coverUrl && coverNode) {
                const rawSrc = coverNode.getAttribute('src') || '';
                const httpIdx = rawSrc.indexOf('http');
                if (httpIdx >= 0) {
                  coverUrl = rawSrc.slice(httpIdx).split('?')[0];
                }
              }

              const isPlaying = playingState !== null
                ? playingState === 2
                : /"type"\s*:\s*"pause"/.test(playLog);

              return JSON.stringify({
                progress,
                isPlaying,
                positionSec: input && Number.isFinite(inputVal) && inputVal >= 0 ? inputVal : -1,
                durationSec: Number.isFinite(inputMax) && inputMax > 0 ? inputMax : (fiberDuration > 0 ? fiberDuration : 0),
                title,
                artist,
                album,
                coverUrl,
                playingMode
              });
            })()
            """;

        string? json = await TryEvaluateStringAsync(expression, safeToRetry: true);
        if (json is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string progressText = root.TryGetProperty("progress", out JsonElement progressElement) &&
                progressElement.ValueKind == JsonValueKind.String
                ? progressElement.GetString() ?? string.Empty
                : string.Empty;
            bool isPlaying = root.TryGetProperty("isPlaying", out JsonElement playingElement) &&
                playingElement.ValueKind == JsonValueKind.True;
            double positionSec = root.TryGetProperty("positionSec", out JsonElement posEl) &&
                posEl.ValueKind == JsonValueKind.Number
                ? posEl.GetDouble()
                : 0;
            double durationSec = root.TryGetProperty("durationSec", out JsonElement durEl) &&
                durEl.ValueKind == JsonValueKind.Number
                ? durEl.GetDouble()
                : 0;
            string title = root.TryGetProperty("title", out JsonElement titleEl) &&
                titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString() ?? string.Empty
                : string.Empty;
            string artist = root.TryGetProperty("artist", out JsonElement artistEl) &&
                artistEl.ValueKind == JsonValueKind.String
                ? artistEl.GetString() ?? string.Empty
                : string.Empty;
            string album = root.TryGetProperty("album", out JsonElement albumEl) &&
                albumEl.ValueKind == JsonValueKind.String
                ? albumEl.GetString() ?? string.Empty
                : string.Empty;
            string coverUrl = root.TryGetProperty("coverUrl", out JsonElement coverEl) &&
                coverEl.ValueKind == JsonValueKind.String
                ? coverEl.GetString() ?? string.Empty
                : string.Empty;
            string playingModeStr = root.TryGetProperty("playingMode", out JsonElement modeEl) &&
                modeEl.ValueKind == JsonValueKind.String
                ? modeEl.GetString() ?? string.Empty
                : string.Empty;

            (TimeSpan parsedPos, TimeSpan parsedDur) = DesktopMusicFallbackProvider.ParseNeteaseProgressText(progressText);
            TimeSpan duration = durationSec > 0
                ? TimeSpan.FromSeconds(durationSec)
                : parsedDur;
            TimeSpan position = durationSec > 0 && positionSec >= 0
                ? TimeSpan.FromSeconds(Math.Min(positionSec, durationSec))
                : parsedPos;

            if (duration <= TimeSpan.Zero && string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            MusicPlaybackMode playbackMode = MapPlayingMode(playingModeStr);

            return new NeteaseCdpPlaybackSnapshot(
                isPlaying,
                position,
                duration,
                progressText,
                title,
                artist,
                album,
                coverUrl,
                playbackMode);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> TryControlAsync(string operation)
    {
        string selector = BuildControlSelector(operation);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        string? result = await TryEvaluateStringAsync(BuildControlExpression(selector));
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildControlSelector(string operation)
    {
        return operation switch
        {
            "play" or "pause" => "[data-testid='tid_playbar_play_btn']",
            "previous" => "[data-testid='tid_playbar_prev_btn']",
            "next" => "[data-testid='tid_playbar_next_btn']",
            _ => string.Empty
        };
    }

    // Kept separate from the transport so a test can pin the selector quoting: the selectors
    // contain single quotes, so wrapping them in single quotes yields a JavaScript syntax error
    // and the click silently never reaches NetEase.
    internal static string BuildControlExpression(string selector)
    {
        return $$"""
            (() => {
              const button = document.querySelector("{{selector}}");
              if (!button || button.getAttribute('aria-disabled') === 'true') return false;
              button.click();
              return true;
            })()
            """;
    }

    public async Task<bool> TrySeekAsync(TimeSpan position)
    {
        double seconds = Math.Max(0, position.TotalSeconds);
        string invariantSeconds = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // First try direct Redux dispatch for exact millisecond seek without relying on window layout.
        string dispatchExpression = $$"""
            (() => {
              try {
                const barEl = document.getElementById('page_pc_mini_bar');
                const fiberKey = barEl ? Object.keys(barEl).find(k => k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$')) : null;
                let f = fiberKey ? barEl[fiberKey] : null;
                while (f) {
                  if (typeof f.memoizedProps?.dispatch === 'function') {
                    f.memoizedProps.dispatch({
                      type: 'playing/setPlayingPosition',
                      payload: { duration: {{invariantSeconds}} }
                    });
                    return true;
                  }
                  f = f.return;
                }
              } catch (e) {}
              return false;
            })()
            """;

        string? dispatchResult = await TryEvaluateStringAsync(dispatchExpression, safeToRetry: true);
        if (string.Equals(dispatchResult, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string rectExpression = """
            (() => {
              const slider = document.querySelector('#page_pc_mini_bar .slider-default');
              const input = document.querySelector('#page_pc_mini_bar input[type="range"]');
              if (!slider || !input) return null;
              const rect = slider.getBoundingClientRect();
              return JSON.stringify({
                left: rect.left,
                top: rect.top,
                width: rect.width,
                height: rect.height,
                max: Number(input.max || 0)
              });
            })()
            """;

        string? rectJson = await TryEvaluateStringAsync(rectExpression, safeToRetry: true);
        if (string.IsNullOrWhiteSpace(rectJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rectJson);
            JsonElement root = document.RootElement;
            double left = root.GetProperty("left").GetDouble();
            double top = root.GetProperty("top").GetDouble();
            double width = root.GetProperty("width").GetDouble();
            double height = root.GetProperty("height").GetDouble();
            double maximum = root.GetProperty("max").GetDouble();
            if (width <= 0 || height <= 0 || maximum <= 0)
            {
                return false;
            }

            double ratio = Math.Clamp(seconds / maximum, 0.0, 1.0);
            double x = left + width * ratio;
            double y = top + height / 2;
            return await TryDispatchSliderMouseAsync(x, y);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Switches NetEase to the requested playback mode. The store dispatch is exact, but the page can
    /// occasionally reject the lookup (the mini-bar is re-mounted while it re-renders). Every attempt is
    /// therefore verified against the mode NetEase reports afterwards; if it did not take effect, the real
    /// mode button in the play bar is clicked until NetEase lands on the requested mode.
    /// </summary>
    public async Task<bool> TryChangePlaybackModeAsync(MusicPlaybackMode playbackMode)
    {
        string targetMode = playbackMode switch
        {
            MusicPlaybackMode.Shuffle => "playRandom",
            MusicPlaybackMode.Repeat => "playCycle",
            _ => "playOrder"
        };

        bool dispatched = string.Equals(
            await TryEvaluateStringAsync(BuildSwitchModeExpression(targetMode), safeToRetry: true),
            "true",
            StringComparison.OrdinalIgnoreCase);

        // NetEase distinguishes list repeat (playCycle) from single repeat (playOneCycle) while the
        // tile exposes one "repeat" state, so either satisfies it.
        if (await IsModeAppliedAsync(playbackMode))
        {
            return true;
        }

        if (dispatched)
        {
            // The store updates synchronously, but the re-render that exposes the new mode on the fiber
            // can be batched; give it a moment before judging the dispatch as ineffective.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            if (await IsModeAppliedAsync(playbackMode))
            {
                return true;
            }
        }

        RecordFailure(dispatched
            ? $"switchPlayingMode to {targetMode} did not take effect"
            : $"switchPlayingMode dispatch unavailable for {targetMode}");

        // Fallback: the play-bar mode button cycles through the modes; click until it matches.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!string.Equals(
                    await TryEvaluateStringAsync(BuildModeButtonClickExpression()),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            if (await IsModeAppliedAsync(playbackMode))
            {
                return true;
            }
        }

        return false;
    }

    private readonly object _failureLogGate = new();
    private readonly Queue<string> _failureLog = new();
    private const int FailureLogCapacity = 40;

    /// <summary>
    /// Recent reasons why an evaluation or control yielded nothing (bounded). NetEase exposes no
    /// diagnostics for its page, so this is the only trail when the tile misses a read.
    /// </summary>
    internal IReadOnlyList<string> DrainFailureLog()
    {
        lock (_failureLogGate)
        {
            List<string> entries = [.. _failureLog];
            _failureLog.Clear();
            return entries;
        }
    }

    private void RecordFailure(string reason)
    {
        lock (_failureLogGate)
        {
            _failureLog.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + reason);
            while (_failureLog.Count > FailureLogCapacity)
            {
                _failureLog.Dequeue();
            }
        }
    }

    /// <summary>Explains a reply that matched the request but carried no usable value.</summary>
    internal static string DescribeNullReply(string response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                return "DevTools error: " + error.GetRawText();
            }

            if (root.TryGetProperty("result", out JsonElement result) &&
                result.TryGetProperty("exceptionDetails", out JsonElement exception))
            {
                return exception.TryGetProperty("exception", out JsonElement inner) &&
                    inner.TryGetProperty("description", out JsonElement description)
                    ? "JS exception: " + description.GetString()
                    : "JS exception: " + exception.GetRawText();
            }

            return "unserializable reply: " + Truncate(response);
        }
        catch (JsonException)
        {
            return "malformed reply: " + Truncate(response);
        }

        static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
    }

    internal static string BuildSwitchModeExpression(string targetMode)
    {
        return $$"""
            (() => {
              try {
                const barEl = document.getElementById('page_pc_mini_bar');
                const fiberKey = barEl ? Object.keys(barEl).find(k => k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$')) : null;
                let f = fiberKey ? barEl[fiberKey] : null;
                while (f) {
                  if (typeof f.memoizedProps?.dispatch === 'function') {
                    f.memoizedProps.dispatch({
                      type: 'playing/switchPlayingMode',
                      payload: { playingMode: '{{targetMode}}', triggerScene: 'miniBar', HeartBeatFlage: true }
                    });
                    return true;
                  }
                  f = f.return;
                }
              } catch (e) {}
              return false;
            })()
            """;
    }

    private static string BuildModeButtonClickExpression()
    {
        return """
            (() => {
              const button = document.querySelector('[data-testid="tid_playmode_btn"]');
              if (!button || button.getAttribute('aria-disabled') === 'true') return false;
              button.click();
              return true;
            })()
            """;
    }

    private async Task<bool> IsModeAppliedAsync(MusicPlaybackMode playbackMode)
    {
        string? result = await TryEvaluateStringAsync(ReadPlayingModeExpression, safeToRetry: true);
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            return MapPlayingMode(document.RootElement.GetString() ?? string.Empty) == playbackMode;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const string ReadPlayingModeExpression = """
        (() => {
          try {
            const barEl = document.getElementById('page_pc_mini_bar');
            const fiberKey = barEl ? Object.keys(barEl).find(k => k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$')) : null;
            let f = fiberKey ? barEl[fiberKey] : null;
            while (f) {
              const mode = f.memoizedProps?.playingMode;
              if (typeof mode === 'string') return JSON.stringify(mode);
              f = f.return;
            }
          } catch (e) {}
          return JSON.stringify('');
        })()
        """;

    internal static MusicPlaybackMode MapPlayingMode(string playingMode)
    {
        return playingMode switch
        {
            "playRandom" => MusicPlaybackMode.Shuffle,
            "playCycle" or "playOneCycle" => MusicPlaybackMode.Repeat,
            _ => MusicPlaybackMode.Normal
        };
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> and returns its remote value as a string ("true"/"false"
    /// for booleans), or null when no value was produced.
    /// </summary>
    /// <param name="expression">JavaScript expression that evaluates to a serializable value.</param>
    /// <param name="safeToRetry">
    /// True when running the expression twice has the same effect as running it once (reads, seeks, a
    /// switch to an explicit mode). NetEase drops the DevTools socket about every 30 seconds without a
    /// close handshake, so a request that straddles the drop is re-issued on a fresh connection. A
    /// toggle (play/pause button, cycling mode button) is only re-issued when it was never sent.
    /// </param>
    private async Task<string?> TryEvaluateStringAsync(string expression, bool safeToRetry = false)
    {
        if (_isDisposed)
        {
            return null;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            (bool sent, bool failed, string? value) = await EvaluateOnceAsync(expression);
            if (!failed)
            {
                return value;
            }

            if (attempt == 0 && (safeToRetry || !sent))
            {
                RecordFailure("transport failure" + (sent ? " after the request was sent" : " before the request was sent") +
                    "; retrying on a fresh connection");
                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>One request/reply round trip. Never throws; transport faults are reported in the tuple.</summary>
    private async Task<(bool Sent, bool Failed, string? Value)> EvaluateOnceAsync(string expression)
    {
        bool sent = false;
        await _stateLock.WaitAsync();
        try
        {
            if (!await TryConnectAsync())
            {
                RecordFailure("DevTools endpoint unreachable on port " + DefaultPort);
                return (false, true, null);
            }

            int id = Interlocked.Increment(ref _nextMessageId);
            string request = JsonSerializer.Serialize(new
            {
                id,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression,
                    returnByValue = true
                }
            });

            byte[] payload = Encoding.UTF8.GetBytes(request);
            await _webSocket!.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
            sent = true;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                string response = await ReceiveTextAsync(_webSocket, timeout.Token);
                if (!TryParseEvaluateResponse(response, id, out string? value))
                {
                    // Not the reply for this request (e.g. an intervening DevTools event); keep
                    // reading until it arrives. The timeout bounds the wait.
                    continue;
                }

                if (value is null)
                {
                    RecordFailure(DescribeNullReply(response));
                }

                return (true, false, value);
            }
        }
        catch (Exception ex)
        {
            RecordFailure("evaluate failed (" + ex.GetType().Name + ": " + ex.Message + ")" +
                (sent ? " after the request was sent" : " before the request was sent"));
            await DisconnectAsync();
            return (sent, true, null);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Parses a DevTools <c>Runtime.evaluate</c> reply. Returns false only when the message is not the
    /// reply for <paramref name="id"/>; a reply that carries an error, a JS exception or an unserializable
    /// value yields true with a null value.
    /// </summary>
    internal static bool TryParseEvaluateResponse(string response, int id, out string? value)
    {
        value = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out JsonElement idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                idElement.GetInt32() != id)
            {
                return false;
            }

            if (root.TryGetProperty("error", out _) ||
                !root.TryGetProperty("result", out JsonElement resultElement) ||
                resultElement.TryGetProperty("exceptionDetails", out _) ||
                !resultElement.TryGetProperty("result", out JsonElement wrapper))
            {
                return true;
            }

            // With returnByValue the remote value is wrapped: {"type":"string","value":"..."}.
            // The wrapper itself is a JSON object, so the inner "value" must be unwrapped first.
            JsonElement valueElement = wrapper.ValueKind == JsonValueKind.Object &&
                wrapper.TryGetProperty("value", out JsonElement inner)
                ? inner
                : default;

            value = valueElement.ValueKind switch
            {
                JsonValueKind.String => valueElement.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => valueElement.GetRawText(),
                _ => null
            };
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns true when something answers HTTP on the DevTools port, which means NetEase was already
    /// started with the bridge (or another program owns the port). Used to decide whether restarting
    /// NetEase with the bridge could help; it must not be decided from a failed snapshot read alone.
    /// </summary>
    public static async Task<bool> IsBridgeEndpointOpenAsync()
    {
        try
        {
            using HttpResponseMessage response = await s_httpClient.GetAsync(
                $"http://127.0.0.1:{DefaultPort}/json/version");
            return response.Content is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_webSocket is { State: WebSocketState.Open })
        {
            return true;
        }

        await DisconnectAsync();
        if (_targetWebSocketUrl is null)
        {
            _targetWebSocketUrl = await TryResolveTargetWebSocketUrlAsync();
        }

        if (_targetWebSocketUrl is null)
        {
            return false;
        }

        var socket = new ClientWebSocket();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(new Uri(_targetWebSocketUrl), timeout.Token);
            _webSocket = socket;
            return true;
        }
        catch
        {
            socket.Dispose();
            _targetWebSocketUrl = null;
            return false;
        }
    }

    private static async Task<string?> TryResolveTargetWebSocketUrlAsync()
    {
        try
        {
            using HttpResponseMessage response = await s_httpClient.GetAsync(
                $"http://127.0.0.1:{DefaultPort}/json/list");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync();
            using JsonDocument document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement target in document.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("type", out JsonElement typeElement) ||
                    typeElement.GetString() != "page" ||
                    !target.TryGetProperty("url", out JsonElement urlElement) ||
                    !(urlElement.GetString() ?? string.Empty).Contains(
                        "orpheus://orpheus/pub/app.html",
                        StringComparison.OrdinalIgnoreCase) ||
                    !target.TryGetProperty("webSocketDebuggerUrl", out JsonElement socketElement) ||
                    socketElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                return socketElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<bool> TryDispatchSliderMouseAsync(double x, double y)
    {
        if (_isDisposed)
        {
            return false;
        }

        await _stateLock.WaitAsync();
        try
        {
            if (!await TryConnectAsync())
            {
                return false;
            }

            return await SendMouseEventAsync("mousePressed", x, y, buttons: 0) &&
                await SendMouseEventAsync("mouseMoved", x, y, buttons: 1) &&
                await SendMouseEventAsync("mouseReleased", x, y, buttons: 0);
        }
        catch
        {
            await DisconnectAsync();
            return false;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<bool> SendMouseEventAsync(
        string type,
        double x,
        double y,
        int buttons)
    {
        int id = Interlocked.Increment(ref _nextMessageId);
        string request = JsonSerializer.Serialize(new
        {
            id,
            method = "Input.dispatchMouseEvent",
            @params = new
            {
                type,
                x,
                y,
                button = "left",
                buttons,
                clickCount = 1
            }
        });

        byte[] payload = Encoding.UTF8.GetBytes(request);
        await _webSocket!.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string response = await ReceiveTextAsync(_webSocket!, timeout.Token);
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.TryGetProperty("id", out JsonElement idElement) &&
            idElement.GetInt32() == id &&
            !document.RootElement.TryGetProperty("error", out _);
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The DevTools WebSocket closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private async Task DisconnectAsync()
    {
        if (_webSocket is null)
        {
            return;
        }

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "DeskBox shutdown",
                    timeout.Token);
            }
        }
        catch
        {
        }
        finally
        {
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _stateLock.Wait();
        try
        {
            _webSocket?.Dispose();
            _webSocket = null;
            _targetWebSocketUrl = null;
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }
}
