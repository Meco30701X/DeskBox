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
    bool HasRealTimeline,
    bool CanChangePlaybackMode,
    MusicPlaybackMode PlaybackMode,
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
    private const int MaxCachedCovers = 64;
    private static readonly TimeSpan NeteaseSnapshotStaleLimit = TimeSpan.FromSeconds(3);
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
    private readonly ConcurrentDictionary<string, byte[]?> _coverBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflightCoverDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, AudioActivityTracker> _audioActivityTrackers = new();
    private readonly NeteaseCloudMusicCdpClient _neteaseCdpClient = new();
    private readonly object _pollGate = new();
    private Timer? _pollTimer;
    private NeteaseCdpPlaybackSnapshot? _neteaseCdpSnapshot;
    private long _neteaseCdpSnapshotUpdatedAtTicks;
    private string _lastObservedSignature = string.Empty;
    private string _lastObservedTimelineSignature = string.Empty;
    private int _pollRunning;
    private bool _neteaseBridgeRestartAttempted;
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
                _ => _ = PollAsync(),
                null,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500));
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
        _neteaseCdpClient.Dispose();
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

        if (string.Equals(target.PlayerKey, "cloudmusic", StringComparison.Ordinal))
        {
            // The NetEase play button is a toggle while the tile issues idempotent play/pause commands:
            // clicking when the requested state is already active would reverse it.
            NeteaseCdpPlaybackSnapshot? live = _neteaseCdpSnapshot;
            if (live is not null &&
                ((operation == "play" && live.IsPlaying) || (operation == "pause" && !live.IsPlaying)))
            {
                return true;
            }

            if (_neteaseCdpClient.TryControlAsync(operation).GetAwaiter().GetResult())
            {
                RefreshNeteaseSnapshot();
                return true;
            }

            if (live is not null)
            {
                // The bridge is up, so the click was either rejected by the page or its reply was lost
                // in one of NetEase's periodic socket drops. Firing a global media-key toggle on top
                // could reverse a click that did land; the next poll reports the real state instead.
                return false;
            }
        }

        byte virtualKey = operation switch
        {
            "play" => VkMediaPlayPause,
            "pause" => VkMediaPlayPause,
            "previous" => VkMediaPrevTrack,
            "next" => VkMediaNextTrack,
            _ => (byte)0
        };
        if (virtualKey == 0)
        {
            return false;
        }

        // Direct WM_APPCOMMAND delivery can report success even when a skinned/CEF host
        // ignores the message. The standard global media key is the transport surface all
        // three supported desktop players enable by default.
        SendMediaKey(virtualKey);
        return true;
    }

    public bool TrySeek(string? sessionId, TimeSpan position)
    {
        IReadOnlyList<DesktopPlayerSnapshot> snapshots = GetActivePlayerSnapshots();
        DesktopPlayerSnapshot? target = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            target = snapshots.FirstOrDefault(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
        target ??= snapshots.FirstOrDefault(s =>
            string.Equals(s.PlayerKey, "cloudmusic", StringComparison.Ordinal));

        if (target is null ||
            !string.Equals(target.PlayerKey, "cloudmusic", StringComparison.Ordinal) ||
            !target.HasRealTimeline)
        {
            return false;
        }

        bool didSeek = _neteaseCdpClient.TrySeekAsync(position).GetAwaiter().GetResult();
        if (didSeek)
        {
            RefreshNeteaseSnapshot();
            _neteaseCdpSnapshot = _neteaseCdpSnapshot is not null
                ? _neteaseCdpSnapshot with { Position = position }
                : null;
        }

        return didSeek;
    }

    public bool TryChangePlaybackMode(string? sessionId, MusicPlaybackMode playbackMode)
    {
        IReadOnlyList<DesktopPlayerSnapshot> snapshots = GetActivePlayerSnapshots();
        DesktopPlayerSnapshot? target = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            target = snapshots.FirstOrDefault(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
        target ??= snapshots.FirstOrDefault(s =>
            string.Equals(s.PlayerKey, "cloudmusic", StringComparison.Ordinal));

        if (target is null ||
            !string.Equals(target.PlayerKey, "cloudmusic", StringComparison.Ordinal) ||
            !target.CanChangePlaybackMode)
        {
            return false;
        }

        bool didChange = _neteaseCdpClient.TryChangePlaybackModeAsync(playbackMode).GetAwaiter().GetResult();
        if (didChange)
        {
            RefreshNeteaseSnapshot();
            _neteaseCdpSnapshot = _neteaseCdpSnapshot is not null
                ? _neteaseCdpSnapshot with { PlaybackMode = playbackMode }
                : null;
        }

        return didChange;
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
    private async Task PollAsync()
    {
        if (_isDisposed || Interlocked.CompareExchange(ref _pollRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await EnsureNeteaseCdpBridgeAsync();
            ApplyNeteaseSnapshot(await _neteaseCdpClient.TryGetSnapshotAsync());

            IReadOnlyList<DesktopPlayerSnapshot> snapshots = GetActivePlayerSnapshots();
            string signature = BuildSnapshotsSignature(snapshots);
            string timelineSignature = BuildTimelineSignature(snapshots);
            bool metadataChanged = !string.Equals(signature, _lastObservedSignature, StringComparison.Ordinal);
            bool timelineChanged = !string.Equals(timelineSignature, _lastObservedTimelineSignature, StringComparison.Ordinal);

            if (metadataChanged)
            {
                _lastObservedSignature = signature;
                _lastObservedTimelineSignature = timelineSignature;
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
            else if (timelineChanged)
            {
                _lastObservedTimelineSignature = timelineSignature;
                TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopMusicFallback] Poll error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _pollRunning, 0);
        }
    }

    private void RefreshNeteaseSnapshot()
    {
        ApplyNeteaseSnapshot(_neteaseCdpClient.TryGetSnapshotAsync().GetAwaiter().GetResult());
    }

    /// <summary>
    /// A single read can fail transiently (the page is busy or re-rendering its bar). Dropping to "no
    /// timeline" for one poll makes the tile jump to position 0 and disables its buttons, so the last
    /// good read is kept for a short grace period and only abandoned when reads keep failing.
    /// </summary>
    private void ApplyNeteaseSnapshot(NeteaseCdpPlaybackSnapshot? fresh)
    {
        long now = DateTimeOffset.UtcNow.Ticks;
        if (fresh is not null)
        {
            _neteaseCdpSnapshot = fresh;
            Interlocked.Exchange(ref _neteaseCdpSnapshotUpdatedAtTicks, now);
            return;
        }

        long updatedAt = Interlocked.Read(ref _neteaseCdpSnapshotUpdatedAtTicks);
        if (_neteaseCdpSnapshot is not null &&
            TimeSpan.FromTicks(now - updatedAt) > NeteaseSnapshotStaleLimit)
        {
            _neteaseCdpSnapshot = null;
        }
    }

    private async Task EnsureNeteaseCdpBridgeAsync()
    {
        if (_isDisposed || _neteaseCdpClient.IsConnected)
        {
            return;
        }

        // Only restart NetEase when it is running without the bridge port. A failed snapshot read on an
        // already-bridged instance (transient timeout, busy page, track change) must never kill the player.
        if (await NeteaseCloudMusicCdpClient.IsBridgeEndpointOpenAsync())
        {
            return;
        }

        try
        {
            if (Process.GetProcessesByName("cloudmusic").Length == 0)
            {
                _neteaseBridgeRestartAttempted = false;
                return;
            }
        }
        catch
        {
        }

        if (_neteaseBridgeRestartAttempted)
        {
            return;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("cloudmusic");
        }
        catch
        {
            return;
        }

        if (processes.Length == 0)
        {
            _neteaseBridgeRestartAttempted = false;
            return;
        }

        string? executablePath = null;
        foreach (Process process in processes)
        {
            try
            {
                executablePath ??= process.MainModule?.FileName;
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
            return;
        }

        _neteaseBridgeRestartAttempted = true;
        try
        {
            foreach (Process process in processes)
            {
                process.Kill(entireProcessTree: true);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));

            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--remote-debugging-port=9223",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                UseShellExecute = true
            });

            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (_isDisposed)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
                if (await _neteaseCdpClient.TryGetSnapshotAsync() is not null)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopMusicFallback] Failed to enable NetEase CDP bridge: {ex.Message}");
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
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
                $"{s.SessionId}:{s.PlaybackState}:{s.PlaybackMode}:{s.Title}:{s.Artist}:{s.Duration.TotalSeconds:F0}:{(s.Thumbnail is not null ? 1 : 0)}"));
    }

    private static string BuildTimelineSignature(IReadOnlyList<DesktopPlayerSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            "|",
            snapshots.Select(s => $"{s.SessionId}:{Math.Round(s.Position.TotalSeconds):F0}"));
    }

    internal static (TimeSpan Position, TimeSpan Duration) ParseNeteaseProgressText(string progressText)
    {
        if (string.IsNullOrWhiteSpace(progressText))
        {
            return (TimeSpan.Zero, TimeSpan.Zero);
        }

        string[] parts = progressText.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return (TimeSpan.Zero, TimeSpan.Zero);
        }

        TimeSpan? position = ParseNeteaseTime(parts[0]);
        TimeSpan? duration = ParseNeteaseTime(parts[1]);
        if (position is null || duration is null)
        {
            return (TimeSpan.Zero, TimeSpan.Zero);
        }

        return (position.Value, duration.Value);
    }

    private static TimeSpan? ParseNeteaseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] segments = text.Split(':', StringSplitOptions.TrimEntries);
        if (segments.Length is < 2 or > 3)
        {
            return null;
        }

        int hours = 0;
        int index = 0;
        if (segments.Length == 3 && (!int.TryParse(segments[index], out hours) || hours < 0))
        {
            return null;
        }

        if (segments.Length == 3)
        {
            index++;
        }

        if (!int.TryParse(segments[index], out int minutes) || minutes < 0 || minutes > 59 ||
            !int.TryParse(segments[index + 1], out int seconds) || seconds < 0 || seconds > 59)
        {
            return null;
        }

        return new TimeSpan(hours, minutes, seconds);
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

        NeteaseCdpPlaybackSnapshot? cdpSnapshot =
            string.Equals(definition.PlayerKey, "cloudmusic", StringComparison.Ordinal)
                ? _neteaseCdpSnapshot
                : null;

        // When NetEase is paused or just restarted, the window title stays "网易云音乐".
        // Always prefer the authoritative title & artist read directly from the NetEase mini-bar DOM/React state.
        if (cdpSnapshot is not null)
        {
            if (!string.IsNullOrWhiteSpace(cdpSnapshot.Title))
            {
                parsedTitle = cdpSnapshot.Title;
            }
            if (!string.IsNullOrWhiteSpace(cdpSnapshot.Artist))
            {
                parsedArtist = cdpSnapshot.Artist;
            }
        }

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
        string album = !string.IsNullOrWhiteSpace(cdpSnapshot?.Album)
            ? cdpSnapshot.Album
            : (localMetadata?.Album ?? string.Empty);
        TimeSpan duration = localMetadata?.Duration ?? TimeSpan.Zero;
        TimeSpan position = TimeSpan.Zero;
        bool hasRealTimeline = false;
        bool canChangePlaybackMode = false;
        MusicPlaybackMode playbackMode = MusicPlaybackMode.Normal;
        byte[]? coverBytes = localMetadata?.CoverBytes;

        if (cdpSnapshot is not null)
        {
            playbackState = cdpSnapshot.IsPlaying
                ? MusicPlaybackState.Playing
                : MusicPlaybackState.Paused;
            position = cdpSnapshot.Position;
            if (cdpSnapshot.Duration > TimeSpan.Zero)
            {
                duration = cdpSnapshot.Duration;
            }
            hasRealTimeline = duration > TimeSpan.Zero;
            canChangePlaybackMode = true;
            playbackMode = cdpSnapshot.PlaybackMode;

            if (!string.IsNullOrWhiteSpace(cdpSnapshot.CoverUrl))
            {
                string sizedCoverUrl = cdpSnapshot.CoverUrl.Contains('?', StringComparison.Ordinal)
                    ? cdpSnapshot.CoverUrl
                    : $"{cdpSnapshot.CoverUrl}?param=256y256";
                if (_coverBytesCache.TryGetValue(sizedCoverUrl, out byte[]? directCover))
                {
                    // An empty entry records a failed download so it is not retried on every poll.
                    coverBytes ??= directCover is { Length: > 0 } ? directCover : null;
                }
                else
                {
                    _ = DownloadAndNotifyCoverAsync(sizedCoverUrl);
                }
            }
        }

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
            else if (coverBytes is null || duration <= TimeSpan.Zero)
            {
                _ = EnrichAndNotifyAsync(definition.PlayerKey, parsedTitle, parsedArtist);
            }
        }

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
            HasRealTimeline: hasRealTimeline,
            CanChangePlaybackMode: canChangePlaybackMode,
            PlaybackMode: playbackMode,
            Thumbnail: CreateStreamReference(coverBytes));
    }

    private async Task DownloadAndNotifyCoverAsync(string coverUrl)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }

        Task<byte[]?> downloadTask = _inflightCoverDownloads.GetOrAdd(
            coverUrl,
            url => TryDownloadCoverBytesAsync(url));

        try
        {
            byte[]? bytes = await downloadTask;
            _coverBytesCache[coverUrl] = bytes is { Length: > 0 } ? bytes : Array.Empty<byte>();
            TrimCoverCache();
            if (bytes is { Length: > 0 } && !_isDisposed)
            {
                MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _inflightCoverDownloads.TryRemove(coverUrl, out _);
        }
    }

    private void TrimCoverCache()
    {
        // One entry is added per played track (including failures), so bound the cache
        // for long-running sessions. Evicted tracks are simply downloaded again if they return.
        if (_coverBytesCache.Count <= MaxCachedCovers)
        {
            return;
        }

        int evict = Math.Max(1, _coverBytesCache.Count - (MaxCachedCovers / 2));
        foreach (string key in _coverBytesCache.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(evict))
        {
            _coverBytesCache.TryRemove(key, out _);
        }
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
            string historyPath = Path.Combine(localAppData, "NetEase", "CloudMusic", "webdata", "file", "playingList");
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
            if (!doc.RootElement.TryGetProperty("list", out JsonElement listElement) ||
                listElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            DesktopTrackMetadata? firstTrack = null;
            foreach (JsonElement item in listElement.EnumerateArray())
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
    private Dictionary<uint, MusicPlaybackState> GetProcessAudioStates()
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
                    float peak = 0;
                    bool hasMeter = control is IAudioMeterInformation meter &&
                        meter.GetPeakValue(out peak) >= 0;

                    // CoreAudio reports a paused CEF/Qt player as Active for a while.
                    // The endpoint meter is the immediate source of truth; a short grace
                    // period keeps quiet passages from flickering the transport state.
                    AudioActivityTracker tracker = _audioActivityTrackers.GetOrAdd(
                        pid,
                        _ => new AudioActivityTracker());
                    MusicPlaybackState mappedState = hasMeter
                        ? tracker.Update(peak)
                        : tracker.Update(sessionState == AudioSessionState.AudioSessionStateActive ? 1f : 0f);
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
    private sealed class AudioActivityTracker
    {
        private DateTimeOffset _lastActiveAt = DateTimeOffset.MinValue;

        public MusicPlaybackState Update(float peak)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (peak >= AudioPeakActiveThreshold)
            {
                _lastActiveAt = now;
                return MusicPlaybackState.Playing;
            }

            return now - _lastActiveAt <= TimeSpan.FromMilliseconds(1200)
                ? MusicPlaybackState.Playing
                : MusicPlaybackState.Paused;
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
