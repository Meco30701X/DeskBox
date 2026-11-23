using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeskBox.Platform;
using Windows.Storage.Streams;
namespace DeskBox.Services;
internal sealed record DesktopPlayerDefinition(
    string PlayerKey,
    string SourceAppUserModelId,
    string DisplayName,
    string[] ProcessNames,
    string[] PreferredWindowClassSubstrings,
    string[] IdleWindowTitles);
internal sealed record DesktopPlayerSnapshot(
    string SessionId,
    string PlayerKey,
    string SourceAppUserModelId,
    string SourceDisplayName,
    nint WindowHandle,
    uint ProcessId,
    string RawWindowTitle,
    string Title,
    string Artist,
    string Album,
    MusicPlaybackState PlaybackState,
    TimeSpan Position,
    TimeSpan Duration,
    IRandomAccessStreamReference? Thumbnail);
internal sealed record DesktopTrackMetadata(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    byte[]? CoverBytes);
internal sealed class DesktopMusicFallbackProvider : IDisposable
{
    internal const string FallbackSessionPrefix = "fallback:";
    private const int WmAppCommand = 0x0319;
    private const int AppCommandMediaNextTrack = 11;
    private const int AppCommandMediaPreviousTrack = 12;
    private const int AppCommandMediaStop = 13;
    private const int AppCommandMediaPlayPause = 14;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaStop = 0xB2;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint ClsctxAll = 23;
    private const float AudioPeakActiveThreshold = 0.0005f;
    internal static readonly IReadOnlyList<DesktopPlayerDefinition> SupportedPlayers =
    [
        new DesktopPlayerDefinition(
            PlayerKey: "cloudmusic",
            SourceAppUserModelId: "cloudmusic.exe",
            DisplayName: "网易云音乐",
            ProcessNames: ["cloudmusic"],
            PreferredWindowClassSubstrings: ["OrpheusBrowserHost", "MiniPlayer", "CefBrowserWindow"],
            IdleWindowTitles: ["网易云音乐", "NetEase Cloud Music", "迷你播放器", "桌面歌词"]),
        new DesktopPlayerDefinition(
            PlayerKey: "qqmusic",
            SourceAppUserModelId: "QQMusic.exe",
            DisplayName: "QQ音乐",
            ProcessNames: ["QQMusic"],
            PreferredWindowClassSubstrings: ["QQMusic_Daemon_Wnd", "TXGuiFoundation"],
            IdleWindowTitles: ["QQ音乐", "QQMusic", "桌面歌词", "迷你模式"]),
        new DesktopPlayerDefinition(
            PlayerKey: "kugou",
            SourceAppUserModelId: "KuGou.exe",
            DisplayName: "酷狗音乐",
            ProcessNames: ["KuGou", "KuGouMusic", "kugou"],
            PreferredWindowClassSubstrings: ["kugou_ui", "KuGou", "SkinWnd"],
            IdleWindowTitles: ["酷狗音乐", "KuGou", "酷狗概念版", "桌面歌词"])
    ];
    private static readonly HttpClient s_httpClient = CreateHttpClient();
    private readonly ConcurrentDictionary<string, DesktopTrackMetadata?> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<DesktopTrackMetadata?>> _inflightMetadataQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrackTimelineTracker> _timelineTrackers = new(StringComparer.Ordinal);
    private readonly object _pollGate = new();
    private Timer? _pollTimer;
    private string _lastObservedSignature = string.Empty;
    private bool _isDisposed;
    public event EventHandler? SessionsChanged;
    public event EventHandler? MediaPropertiesChanged;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? TimelinePropertiesChanged;
    public void StartMonitoring()
    {
        if (_isDisposed || _pollTimer is not null)
        {
            return;
        }
        lock (_pollGate)
        {
            if (_isDisposed || _pollTimer is not null)
            {
                return;
            }
            _pollTimer = new Timer(
                OnPollTimerTick,
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }
    }
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        lock (_pollGate)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }
    public static bool IsFallbackSessionId(string? sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId) &&
            sessionId.StartsWith(FallbackSessionPrefix, StringComparison.Ordinal);
    }
    public static string CreateFallbackSessionId(string sourceAppUserModelId, int ordinal = 0)
    {
        return string.Concat(
            FallbackSessionPrefix,
            MusicSessionService.CreateSessionId(sourceAppUserModelId, ordinal));
    }
    internal static (string Title, string Artist) ParseWindowTitle(
        string? rawWindowTitle,
        DesktopPlayerDefinition player)
    {
        if (string.IsNullOrWhiteSpace(rawWindowTitle))
        {
            return (string.Empty, string.Empty);
        }
        string cleaned = rawWindowTitle.Trim();
        foreach (string idleTitle in player.IdleWindowTitles)
        {
            if (string.Equals(cleaned, idleTitle, StringComparison.OrdinalIgnoreCase))
            {
                return (string.Empty, string.Empty);
            }
        }
        string[] trailingPlayerSuffixes =
        [
            " - 网易云音乐",
            " - QQ音乐",
            " - 酷狗音乐",
            " - 酷狗概念版",
            " - NetEase Cloud Music",
            " - QQMusic"
        ];
        foreach (string suffix in trailingPlayerSuffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                cleaned.Length > suffix.Length)
            {
                cleaned = cleaned[..^suffix.Length].Trim();
                break;
            }
        }
        foreach (string idleTitle in player.IdleWindowTitles)
        {
            if (string.Equals(cleaned, idleTitle, StringComparison.OrdinalIgnoreCase))
            {
                return (string.Empty, string.Empty);
            }
        }
        int separatorIndex = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            string firstPart = cleaned[..separatorIndex].Trim();
            string secondPart = cleaned[(separatorIndex + 3)..].Trim();
            return (firstPart, secondPart);
        }
        return (cleaned, string.Empty);
    }
    public IReadOnlyList<DesktopPlayerSnapshot> GetActivePlayerSnapshots()
    {
        if (_isDisposed)
        {
            return [];
        }
        var results = new List<DesktopPlayerSnapshot>();
        var audioStates = GetProcessAudioStates();
        foreach (DesktopPlayerDefinition definition in SupportedPlayers)
        {
            DesktopPlayerSnapshot? snapshot = TryProbePlayer(definition, audioStates);
            if (snapshot is not null)
            {
                results.Add(snapshot);
            }
        }
        return results;
    }
    public async Task<DesktopTrackMetadata?> EnrichMetadataAsync(
        string playerKey,
        string title,
        string artist)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        string cacheKey = $"{playerKey}\u001F{title.Trim()}\u001F{artist.Trim()}";
        if (_metadataCache.TryGetValue(cacheKey, out DesktopTrackMetadata? cached))
        {
            return cached;
        }
        Task<DesktopTrackMetadata?> queryTask = _inflightMetadataQueries.GetOrAdd(
            cacheKey,
            _ => QueryMetadataCoreAsync(playerKey, title.Trim(), artist.Trim(), cacheKey));
        try
        {
            return await queryTask;
        }
        finally
        {
            _inflightMetadataQueries.TryRemove(cacheKey, out _);
        }
    }
    public bool TryGetCachedMetadata(
        string playerKey,
        string title,
        string artist,
        out DesktopTrackMetadata? metadata)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            metadata = null;
            return false;
        }
        string cacheKey = $"{playerKey}\u001F{title.Trim()}\u001F{artist.Trim()}";
        return _metadataCache.TryGetValue(cacheKey, out metadata);
    }
    public bool TryControl(string? sessionId, string operation)
    {
        IReadOnlyList<DesktopPlayerSnapshot> snapshots = GetActivePlayerSnapshots();
        DesktopPlayerSnapshot? target = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            target = snapshots.FirstOrDefault(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
        target ??= snapshots
            .OrderByDescending(s => s.PlaybackState == MusicPlaybackState.Playing)
            .FirstOrDefault();
        if (target is null)
        {
            return false;
        }
        int appCommand = operation switch
        {
            "play" => AppCommandMediaPlayPause,
            "pause" => AppCommandMediaPlayPause,
            "previous" => AppCommandMediaPreviousTrack,
            "next" => AppCommandMediaNextTrack,
            _ => 0
        };
        byte virtualKey = operation switch
        {
            "play" => VkMediaPlayPause,
            "pause" => VkMediaPlayPause,
            "previous" => VkMediaPrevTrack,
            "next" => VkMediaNextTrack,
            _ => (byte)0
        };
        if (appCommand == 0)
        {
            return false;
        }
        bool sentToWindow = false;
        if (target.WindowHandle != 0 && DesktopMusicNativeMethods.IsWindow(target.WindowHandle))
        {
            nint lParam = (nint)(appCommand << 16);
            sentToWindow = DesktopMusicNativeMethods.PostMessageW(target.WindowHandle, WmAppCommand, target.WindowHandle, lParam);
        }
        if (!sentToWindow && virtualKey != 0)
        {
            SendMediaKey(virtualKey);
            sentToWindow = true;
        }
        else if (virtualKey != 0 && string.Equals(target.PlayerKey, "kugou", StringComparison.Ordinal))
        {
            SendMediaKey(virtualKey);
        }
        if (sentToWindow && _timelineTrackers.TryGetValue(target.SessionId, out TrackTimelineTracker? tracker))
        {
            if (string.Equals(operation, "pause", StringComparison.Ordinal))
            {
                tracker.UpdateState(MusicPlaybackState.Paused);
            }
            else if (string.Equals(operation, "play", StringComparison.Ordinal))
            {
                tracker.UpdateState(MusicPlaybackState.Playing);
            }
        }
        return sentToWindow;
    }
    internal static IRandomAccessStreamReference? CreateStreamReference(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            stream.Seek(0);
            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch
        {
            return null;
        }
    }
    private void OnPollTimerTick(object? state)
    {
        if (_isDisposed)
        {
            return;
        }
        try
        {
            IReadOnlyList<DesktopPlayerSnapshot> snapshots = GetActivePlayerSnapshots();
            string signature = BuildSnapshotsSignature(snapshots);
            if (string.Equals(signature, _lastObservedSignature, StringComparison.Ordinal))
            {
                return;
            }
            _lastObservedSignature = signature;
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
            MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
            TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
            foreach (DesktopPlayerSnapshot snapshot in snapshots)
            {
                if (!string.IsNullOrWhiteSpace(snapshot.Title) &&
                    !TryGetCachedMetadata(snapshot.PlayerKey, snapshot.Title, snapshot.Artist, out _))
                {
                    _ = EnrichAndNotifyAsync(snapshot.PlayerKey, snapshot.Title, snapshot.Artist);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopMusicFallback] Poll error: {ex.Message}");
        }
    }
    private async Task EnrichAndNotifyAsync(string playerKey, string title, string artist)
    {
        DesktopTrackMetadata? metadata = await EnrichMetadataAsync(playerKey, title, artist);
        if (!_isDisposed && metadata is not null)
        {
            MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
            TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private static string BuildSnapshotsSignature(IReadOnlyList<DesktopPlayerSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            "|",
            snapshots.Select(s =>
                $"{s.SessionId}:{s.PlaybackState}:{s.Title}:{s.Artist}:{s.Duration.TotalSeconds:F0}:{(s.Thumbnail is not null ? 1 : 0)}"));
    }
    private DesktopPlayerSnapshot? TryProbePlayer(
        DesktopPlayerDefinition definition,
        IReadOnlyDictionary<uint, MusicPlaybackState> audioStates)
    {
        var matchingPids = new HashSet<uint>();
        foreach (string processName in definition.ProcessNames)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        matchingPids.Add((uint)process.Id);
                    }
                }
            }
            catch
            {
            }
        }
        if (matchingPids.Count == 0)
        {
            return null;
        }
        WindowCandidate? bestWindow = FindBestPlayerWindow(matchingPids, definition);
        string rawWindowTitle = bestWindow?.Title ?? string.Empty;
        (string parsedTitle, string parsedArtist) = ParseWindowTitle(rawWindowTitle, definition);
        DesktopTrackMetadata? localMetadata = null;
        if (string.Equals(definition.PlayerKey, "cloudmusic", StringComparison.Ordinal))
        {
            localMetadata = TryReadNeteaseLocalHistory(parsedTitle, parsedArtist);
            if (string.IsNullOrWhiteSpace(parsedTitle) && localMetadata is not null)
            {
                parsedTitle = localMetadata.Title;
                parsedArtist = localMetadata.Artist;
            }
        }
        MusicPlaybackState playbackState = ResolvePlaybackState(matchingPids, audioStates, parsedTitle);
        string sessionId = CreateFallbackSessionId(definition.SourceAppUserModelId, 0);
        string album = localMetadata?.Album ?? string.Empty;
        TimeSpan duration = localMetadata?.Duration ?? TimeSpan.Zero;
        byte[]? coverBytes = localMetadata?.CoverBytes;
        if (!string.IsNullOrWhiteSpace(parsedTitle))
        {
            if (TryGetCachedMetadata(definition.PlayerKey, parsedTitle, parsedArtist, out DesktopTrackMetadata? cached) &&
                cached is not null)
            {
                if (string.IsNullOrWhiteSpace(album))
                {
                    album = cached.Album;
                }
                if (duration <= TimeSpan.Zero && cached.Duration > TimeSpan.Zero)
                {
                    duration = cached.Duration;
                }
                coverBytes ??= cached.CoverBytes;
            }
            else
            {
                _ = EnrichAndNotifyAsync(definition.PlayerKey, parsedTitle, parsedArtist);
            }
        }
        TrackTimelineTracker tracker = _timelineTrackers.GetOrAdd(
            sessionId,
            _ => new TrackTimelineTracker());
        TimeSpan position = tracker.UpdateAndGetPosition(parsedTitle, parsedArtist, duration, playbackState);
        return new DesktopPlayerSnapshot(
            SessionId: sessionId,
            PlayerKey: definition.PlayerKey,
            SourceAppUserModelId: definition.SourceAppUserModelId,
            SourceDisplayName: definition.DisplayName,
            WindowHandle: bestWindow?.Handle ?? 0,
            ProcessId: bestWindow?.ProcessId ?? matchingPids.First(),
            RawWindowTitle: rawWindowTitle,
            Title: parsedTitle,
            Artist: parsedArtist,
            Album: album,
            PlaybackState: playbackState,
            Position: position,
            Duration: duration,
            Thumbnail: CreateStreamReference(coverBytes));
    }
    private static MusicPlaybackState ResolvePlaybackState(
        HashSet<uint> processIds,
        IReadOnlyDictionary<uint, MusicPlaybackState> audioStates,
        string parsedTitle)
    {
        bool hasPausedSession = false;
        foreach (uint pid in processIds)
        {
            if (audioStates.TryGetValue(pid, out MusicPlaybackState state))
            {
                if (state == MusicPlaybackState.Playing)
                {
                    return MusicPlaybackState.Playing;
                }
                if (state == MusicPlaybackState.Paused)
                {
                    hasPausedSession = true;
                }
            }
        }
        if (hasPausedSession || !string.IsNullOrWhiteSpace(parsedTitle))
        {
            return MusicPlaybackState.Paused;
        }
        return MusicPlaybackState.Stopped;
    }
    private static WindowCandidate? FindBestPlayerWindow(
        HashSet<uint> targetPids,
        DesktopPlayerDefinition definition)
    {
        var candidates = new List<WindowCandidate>();
        DesktopMusicNativeMethods.EnumWindows((hWnd, _) =>
        {
            DesktopMusicNativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (!targetPids.Contains(pid))
            {
                return true;
            }
            string className = GetWindowClassName(hWnd);
            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title) &&
                !definition.PreferredWindowClassSubstrings.Any(c =>
                    className.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            int score = ScoreWindowCandidate(hWnd, className, title, definition);
            if (score > 0)
            {
                candidates.Add(new WindowCandidate(hWnd, pid, className, title, score));
            }
            return true;
        }, 0);
        return candidates
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
    }
    private static int ScoreWindowCandidate(
        nint hWnd,
        string className,
        string title,
        DesktopPlayerDefinition definition)
    {
        int score = 10;
        bool matchesPreferredClass = definition.PreferredWindowClassSubstrings.Any(c =>
            className.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (matchesPreferredClass)
        {
            score += 100;
        }
        if (DesktopMusicNativeMethods.IsWindowVisible(hWnd))
        {
            score += 20;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            score += 30;
            bool isIdleTitle = definition.IdleWindowTitles.Any(idle =>
                string.Equals(title.Trim(), idle, StringComparison.OrdinalIgnoreCase));
            if (!isIdleTitle)
            {
                score += 50;
                if (title.Contains(" - ", StringComparison.Ordinal))
                {
                    score += 80;
                }
            }
        }
        return score;
    }
    private static DesktopTrackMetadata? TryReadNeteaseLocalHistory(
        string expectedTitle,
        string expectedArtist)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return null;
            }
            string historyPath = Path.Combine(localAppData, "Netease", "CloudMusic", "webdata", "file", "history");
            if (!File.Exists(historyPath))
            {
                return null;
            }
            using var stream = new FileStream(
                historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            DesktopTrackMetadata? firstTrack = null;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out JsonElement trackElement) ||
                    trackElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string name = trackElement.TryGetProperty("name", out JsonElement nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                string artist = ExtractNeteaseArtists(trackElement);
                string album = string.Empty;
                if (trackElement.TryGetProperty("album", out JsonElement albumEl) &&
                    albumEl.ValueKind == JsonValueKind.Object &&
                    albumEl.TryGetProperty("name", out JsonElement albumNameEl) &&
                    albumNameEl.ValueKind == JsonValueKind.String)
                {
                    album = albumNameEl.GetString() ?? string.Empty;
                }
                long durationMs = 0;
                if (trackElement.TryGetProperty("duration", out JsonElement durEl) &&
                    durEl.ValueKind == JsonValueKind.Number)
                {
                    durEl.TryGetInt64(out durationMs);
                }
                else if (trackElement.TryGetProperty("dt", out JsonElement dtEl) &&
                    dtEl.ValueKind == JsonValueKind.Number)
                {
                    dtEl.TryGetInt64(out durationMs);
                }
                var metadata = new DesktopTrackMetadata(
                    name,
                    artist,
                    album,
                    durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero,
                    CoverBytes: null);
                firstTrack ??= metadata;
                if (!string.IsNullOrWhiteSpace(expectedTitle) &&
                    name.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return metadata;
                }
            }
            return firstTrack;
        }
        catch
        {
            return null;
        }
    }
    private static string ExtractNeteaseArtists(JsonElement trackElement)
    {
        JsonElement artistsArray = default;
        bool hasArtists =
            (trackElement.TryGetProperty("artists", out artistsArray) && artistsArray.ValueKind == JsonValueKind.Array) ||
            (trackElement.TryGetProperty("ar", out artistsArray) && artistsArray.ValueKind == JsonValueKind.Array);
        if (!hasArtists)
        {
            return string.Empty;
        }
        var names = new List<string>();
        foreach (JsonElement artistEl in artistsArray.EnumerateArray())
        {
            if (artistEl.ValueKind == JsonValueKind.Object &&
                artistEl.TryGetProperty("name", out JsonElement nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                string? artistName = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(artistName))
                {
                    names.Add(artistName);
                }
            }
        }
        return string.Join(" / ", names);
    }
    private async Task<DesktopTrackMetadata?> QueryMetadataCoreAsync(
        string playerKey,
        string title,
        string artist,
        string cacheKey)
    {
        try
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string encodedQuery = Uri.EscapeDataString(query);
            string url = $"http://music.163.com/api/search/get/web?s={encodedQuery}&type=1&offset=0&total=true&limit=5";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using HttpResponseMessage response = await s_httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
            using JsonDocument doc = await JsonDocument.ParseAsync(responseStream);
            if (!doc.RootElement.TryGetProperty("result", out JsonElement resultEl) ||
                resultEl.ValueKind != JsonValueKind.Object ||
                !resultEl.TryGetProperty("songs", out JsonElement songsEl) ||
                songsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            JsonElement? chosenSong = null;
            foreach (JsonElement song in songsEl.EnumerateArray())
            {
                if (song.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                chosenSong ??= song;
                string songName = song.TryGetProperty("name", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;
                if (string.Equals(songName, title, StringComparison.OrdinalIgnoreCase))
                {
                    chosenSong = song;
                    break;
                }
            }
            if (chosenSong is not { } match)
            {
                return null;
            }
            string matchedTitle = match.TryGetProperty("name", out JsonElement titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString() ?? title
                : title;
            string matchedArtist = ExtractNeteaseArtists(match);
            if (string.IsNullOrWhiteSpace(matchedArtist))
            {
                matchedArtist = artist;
            }
            string albumName = string.Empty;
            string? picUrl = null;
            if (match.TryGetProperty("album", out JsonElement albumEl) &&
                albumEl.ValueKind == JsonValueKind.Object)
            {
                if (albumEl.TryGetProperty("name", out JsonElement albumNameEl) &&
                    albumNameEl.ValueKind == JsonValueKind.String)
                {
                    albumName = albumNameEl.GetString() ?? string.Empty;
                }
                if (albumEl.TryGetProperty("picUrl", out JsonElement picEl) &&
                    picEl.ValueKind == JsonValueKind.String)
                {
                    picUrl = picEl.GetString();
                }
            }
            long durationMs = 0;
            if (match.TryGetProperty("duration", out JsonElement durEl) &&
                durEl.ValueKind == JsonValueKind.Number)
            {
                durEl.TryGetInt64(out durationMs);
            }
            long songId = 0;
            if (match.TryGetProperty("id", out JsonElement songIdEl) &&
                songIdEl.ValueKind == JsonValueKind.Number)
            {
                songIdEl.TryGetInt64(out songId);
            }
            if (string.IsNullOrWhiteSpace(picUrl) && songId > 0)
            {
                picUrl = await TryFetchNeteaseSongCoverUrlAsync(songId);
            }
            byte[]? coverBytes = null;
            if (!string.IsNullOrWhiteSpace(picUrl))
            {
                string sizedPicUrl = picUrl.Contains('?', StringComparison.Ordinal)
                    ? picUrl
                    : $"{picUrl}?param=256y256";
                coverBytes = await TryDownloadCoverBytesAsync(sizedPicUrl);
            }
            var metadata = new DesktopTrackMetadata(
                matchedTitle,
                matchedArtist,
                albumName,
                durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero,
                coverBytes);
            _metadataCache[cacheKey] = metadata;
            return metadata;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopMusicFallback] Metadata query failed: {ex.Message}");
            return null;
        }
    }
    private static async Task<string?> TryFetchNeteaseSongCoverUrlAsync(long songId)
    {
        try
        {
            string detailUrl = $"http://music.163.com/api/song/detail/?id={songId}&ids=%5B{songId}%5D";
            using var request = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using HttpResponseMessage response = await s_httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync();
            using JsonDocument doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("songs", out JsonElement songsEl) ||
                songsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (JsonElement song in songsEl.EnumerateArray())
            {
                if (song.TryGetProperty("album", out JsonElement albumEl) &&
                    albumEl.ValueKind == JsonValueKind.Object &&
                    albumEl.TryGetProperty("picUrl", out JsonElement picEl) &&
                    picEl.ValueKind == JsonValueKind.String)
                {
                    return picEl.GetString();
                }
            }
        }
        catch
        {
        }
        return null;
    }
    private static async Task<byte[]?> TryDownloadCoverBytesAsync(string url)
    {
        try
        {
            using HttpResponseMessage response = await s_httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            return bytes.Length > 0 && bytes.Length <= 4 * 1024 * 1024 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return client;
    }
    private static Dictionary<uint, MusicPlaybackState> GetProcessAudioStates()
    {
        var states = new Dictionary<uint, MusicPlaybackState>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            Type? enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType is null)
            {
                return states;
            }
            enumerator = Activator.CreateInstance(enumeratorType) as IMMDeviceEnumerator;
            if (enumerator is null ||
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) < 0 ||
                device is null)
            {
                return states;
            }
            Guid iidManager = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iidManager, ClsctxAll, 0, out object managerObj) < 0)
            {
                return states;
            }
            manager = managerObj as IAudioSessionManager2;
            if (manager is null ||
                manager.GetSessionEnumerator(out sessionEnumerator) < 0 ||
                sessionEnumerator is null ||
                sessionEnumerator.GetCount(out int count) < 0)
            {
                return states;
            }
            for (int i = 0; i < count; i++)
            {
                if (sessionEnumerator.GetSession(i, out IAudioSessionControl? control) < 0 ||
                    control is null)
                {
                    continue;
                }
                try
                {
                    if (control is not IAudioSessionControl2 control2)
                    {
                        continue;
                    }
                    if (control2.IsSystemSoundsSession() == 0 ||
                        control2.GetProcessId(out uint pid) < 0 ||
                        pid == 0)
                    {
                        continue;
                    }
                    _ = control2.GetState(out AudioSessionState sessionState);
                    bool isPlaying = sessionState == AudioSessionState.AudioSessionStateActive;
                    if (isPlaying && control is IAudioMeterInformation meter)
                    {
                        if (meter.GetPeakValue(out float peak) >= 0)
                        {
                            isPlaying = peak >= AudioPeakActiveThreshold || sessionState == AudioSessionState.AudioSessionStateActive;
                        }
                    }
                    MusicPlaybackState mappedState = isPlaying
                        ? MusicPlaybackState.Playing
                        : MusicPlaybackState.Paused;
                    if (!states.TryGetValue(pid, out MusicPlaybackState existing) ||
                        existing != MusicPlaybackState.Playing)
                    {
                        states[pid] = mappedState;
                    }
                }
                finally
                {
                    ReleaseComObject(control);
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(manager);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
        return states;
    }
    private static void ReleaseComObject(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try
            {
                Marshal.ReleaseComObject(obj);
            }
            catch
            {
            }
        }
    }
    private static string GetWindowTitle(nint hWnd)
    {
        int length = DesktopMusicNativeMethods.GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }
        char[] buffer = new char[Math.Min(length + 2, 1024)];
        unsafe
        {
            fixed (char* ptr = buffer)
            {
                int copied = DesktopMusicNativeMethods.GetWindowTextW(hWnd, ptr, buffer.Length);
                return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
            }
        }
    }
    private static string GetWindowClassName(nint hWnd)
    {
        char[] buffer = new char[256];
        unsafe
        {
            fixed (char* ptr = buffer)
            {
                int copied = DesktopMusicNativeMethods.GetClassNameW(hWnd, ptr, buffer.Length);
                return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
            }
        }
    }
    private static void SendMediaKey(byte virtualKey)
    {
        try
        {
            DesktopMusicNativeMethods.keybd_event(virtualKey, 0, KeyeventfExtendedKey, 0);
            DesktopMusicNativeMethods.keybd_event(virtualKey, 0, KeyeventfExtendedKey | KeyeventfKeyUp, 0);
        }
        catch
        {
        }
    }
    private sealed class TrackTimelineTracker
    {
        private readonly object _lock = new();
        private string _trackSignature = string.Empty;
        private TimeSpan _accumulatedPosition = TimeSpan.Zero;
        private DateTimeOffset _lastPlayingTimestamp = DateTimeOffset.UtcNow;
        private MusicPlaybackState _lastState = MusicPlaybackState.Unknown;
        public TimeSpan UpdateAndGetPosition(
            string title,
            string artist,
            TimeSpan duration,
            MusicPlaybackState currentState)
        {
            lock (_lock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string signature = $"{title}\u001F{artist}";
                if (!string.Equals(_trackSignature, signature, StringComparison.Ordinal))
                {
                    _trackSignature = signature;
                    _accumulatedPosition = TimeSpan.Zero;
                    _lastPlayingTimestamp = now;
                    _lastState = currentState;
                    return TimeSpan.Zero;
                }
                if (_lastState == MusicPlaybackState.Playing)
                {
                    TimeSpan delta = now - _lastPlayingTimestamp;
                    if (delta > TimeSpan.Zero)
                    {
                        _accumulatedPosition += delta;
                    }
                }
                if (duration > TimeSpan.Zero && _accumulatedPosition > duration)
                {
                    _accumulatedPosition = duration;
                }
                _lastPlayingTimestamp = now;
                _lastState = currentState;
                return _accumulatedPosition;
            }
        }
        public void UpdateState(MusicPlaybackState newState)
        {
            lock (_lock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (_lastState == MusicPlaybackState.Playing)
                {
                    TimeSpan delta = now - _lastPlayingTimestamp;
                    if (delta > TimeSpan.Zero)
                    {
                        _accumulatedPosition += delta;
                    }
                }
                _lastPlayingTimestamp = now;
                _lastState = newState;
            }
        }
    }
    private sealed record WindowCandidate(
        nint Handle,
        uint ProcessId,
        string ClassName,
        string Title,
        int Score);
    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }
    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }
    private enum AudioSessionState
    {
        AudioSessionStateInactive = 0,
        AudioSessionStateActive = 1,
        AudioSessionStateExpired = 2
    }
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }
    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig]
        int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }
    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);
        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl? sessionControl);
    }
    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);
    }
    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);
        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);
        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig]
        int RegisterAudioSessionNotification(nint notifications);
        [PreserveSig]
        int UnregisterAudioSessionNotification(nint notifications);
        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);
        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);
        [PreserveSig]
        int GetProcessId(out uint processId);
        [PreserveSig]
        int IsSystemSoundsSession();
        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float pfPeak);
    }
}
