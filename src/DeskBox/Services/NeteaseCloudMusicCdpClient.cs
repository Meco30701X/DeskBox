using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

internal sealed record NeteaseCdpPlaybackSnapshot(
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    string ProgressText);

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
              return JSON.stringify({
                progress,
                isPlaying: /"type"\s*:\s*"pause"/.test(playLog)
              });
            })()
            """;

        string? json = await TryEvaluateStringAsync(expression);
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

            (TimeSpan position, TimeSpan duration) = DesktopMusicFallbackProvider.ParseNeteaseProgressText(progressText);
            if (duration <= TimeSpan.Zero)
            {
                return null;
            }

            return new NeteaseCdpPlaybackSnapshot(isPlaying, position, duration, progressText);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> TryControlAsync(string operation)
    {
        string selector = operation switch
        {
            "play" or "pause" => "[data-testid='tid_playbar_play_btn']",
            "previous" => "[data-testid='tid_playbar_prev_btn']",
            "next" => "[data-testid='tid_playbar_next_btn']",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        string expression = $$"""
            (() => {
              const button = document.querySelector('{{selector}}');
              if (!button || button.getAttribute('aria-disabled') === 'true') return false;
              button.click();
              return true;
            })()
            """;

        string? result = await TryEvaluateStringAsync(expression);
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> TrySeekAsync(TimeSpan position)
    {
        double seconds = Math.Max(0, position.TotalSeconds);
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

        string? rectJson = await TryEvaluateStringAsync(rectExpression);
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
        string response = await ReceiveTextAsync(_webSocket, timeout.Token);
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.TryGetProperty("id", out JsonElement idElement) &&
            idElement.GetInt32() == id &&
            !document.RootElement.TryGetProperty("error", out _);
    }

    private async Task<string?> TryEvaluateStringAsync(string expression)
    {
        if (_isDisposed)
        {
            return null;
        }

        await _stateLock.WaitAsync();
        try
        {
            if (!await TryConnectAsync())
            {
                return null;
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string response = await ReceiveTextAsync(_webSocket, timeout.Token);
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement idElement) ||
                idElement.GetInt32() != id ||
                !root.TryGetProperty("result", out JsonElement resultElement) ||
                !resultElement.TryGetProperty("result", out JsonElement valueElement))
            {
                return null;
            }

            if (root.TryGetProperty("error", out _) ||
                resultElement.TryGetProperty("exceptionDetails", out _))
            {
                return null;
            }

            return valueElement.ValueKind switch
            {
                JsonValueKind.String => valueElement.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
        catch
        {
            await DisconnectAsync();
            return null;
        }
        finally
        {
            _stateLock.Release();
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
