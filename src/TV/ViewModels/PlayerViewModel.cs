using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TV.Models;
using TV.Services;
using TV.Services.AI;
using TV.Services.Playback;
using TV.Services.Persistence;
using TV.Services.Vod.Native;

namespace TV.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private PlaybackRequest? _request;
    private DateTime _lastSaved = DateTime.MinValue;
    private bool _suppressSelection;
    private bool _isLive;
    private bool _isLocalFile;
    private bool _autoNext = true;
    private bool _autoSwitchLine = true;
    private bool _lineRecoveryAttempted;
    private bool _hasPlayedMedia;
    private TaskCompletionSource _viewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _loadCts = new();
    private CancellationTokenSource? _cloudSubtitleCts;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private long _loadGeneration;
    private int _tickRunning;
    private int _recoveryRunning;
    private long _lastProgressMs = -1;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _bufferResumeAt;
    private DateTimeOffset _bufferWindowAt;
    private DateTimeOffset _lastRecoveryAt;
    private bool _bufferingPaused;
    private bool _manualPaused;
    private bool _adaptiveQuality = true;
    private int _bufferEvents;
    private bool _refreshedSource;
    private bool _introSkipped;
    private bool _outroSkipped;
    private string _timingKey = string.Empty;
    private PlaybackRequest? _resolvedMedia;
    private Uri? _selectedCloudSubtitleAddress;
    private CancellationTokenSource? _aiSubtitleCts;
    private string? _activeExternalSubtitlePath;
    private List<SubtitleCue>? _activeExternalSubtitleCues;
    private string? _lastGeneratedAiSubtitlePath;
    private bool _externalSubtitleSpeedBaked;
    private long _lastAppliedDynamicSpuDelay = long.MinValue;
    private bool _pauseOverrideActive;
    private bool _pauseDesiredPlaying;
    private long _pauseOverrideUntilTs;
    private CancellationTokenSource? _subtitleSpeedDebounceCts;

    public PlayerViewModel(AppServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += OnTick;
        if (!services.PlayerOnly) _services.Cloud.AuthenticationRequired += OnCloudAuthenticationRequired;
    }

    public ObservableCollection<PlaybackLine> Lines { get; } = [];
    public ObservableCollection<Episode> Episodes { get; } = [];
    [ObservableProperty] private string _title = "尚未播放";
    [ObservableProperty] private string _subtitle = string.Empty;
    /// <summary>播放中视频的具体分辨率，如 1920*1080，显示在播放器标题行。</summary>
    [ObservableProperty] private string _resolutionText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string _bufferingText = string.Empty;
    [ObservableProperty] private string _playPauseGlyph = "\uE768";
    [ObservableProperty] private string _volumeGlyph = "\uE767";
    [ObservableProperty] private double _positionMs;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private string _positionText = "00:00";
    [ObservableProperty] private string _durationText = "00:00";
    [ObservableProperty] private int _volume = 85;
    [ObservableProperty] private double _rate = 1;
    [ObservableProperty] private string _rateText = "1.0x";
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private Episode? _selectedEpisode;
    [ObservableProperty] private bool _hasPlaylist;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private bool _canPrevious;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRequiredLogin))] private CloudProviderKind? _requiredLoginProvider;
    public bool HasRequiredLogin => RequiredLoginProvider.HasValue;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(QualityButtonText))] private QualityOption _selectedQuality = new("自动", 0);
    public string QualityButtonText => SelectedQuality.RequestsOriginal ? "原画" : SelectedQuality.ProviderId switch
    {
        100 => "修复", 120 => "4K", 125 => "HDR", 126 => "杜比", 127 => "8K",
        _ => SelectedQuality.Height > 0 ? $"{SelectedQuality.Height}p" : SelectedQuality.Name
    };
    private static readonly IReadOnlyList<QualityOption> DefaultQualityOptions = [new("自动", 0), new("原画", -1), new("1080p", 1080), new("720p", 720), new("420p", 420)];
    public IReadOnlyList<QualityOption> QualityOptions =>
        _resolvedMedia?.AvailableQualities is { } options ? [DefaultQualityOptions[0], .. options] :
        _resolvedMedia is { UsesProviderVideoHeights: true, AvailableVideoHeights: { Count: > 0 } heights }
            ? DefaultQualityOptions.Take(2).Concat(heights.OrderDescending().Select(height => new QualityOption($"{height}p", height))).ToArray()
            : _request?.Kind == PlaybackKind.Live ? [DefaultQualityOptions[0]] : [DefaultQualityOptions[0], DefaultQualityOptions[1]];
    internal bool IsBilibiliPlayback => !_services.PlayerOnly && _request is { Kind: PlaybackKind.Vod } request &&
        _services.Config.Current?.Sites.Any(site => site.Key == request.SiteKey && BilibiliVodProvider.Supports(site)) == true;
    [ObservableProperty] private string _qualityMessage = string.Empty;
    private CancellationTokenSource? _qualityCts;
    private bool _settingQuality;

    public event EventHandler? FullScreenRequested;
    public event EventHandler? CompactOverlayRequested;
    public event EventHandler<Uri?>? DanmakuChanged;
    public event EventHandler? DanmakuCleared;
    public event EventHandler<PlayerSnapshot>? SnapshotUpdated;
    public event EventHandler<long>? SeekPerformed;
    public event EventHandler<bool>? PlaybackStateChanged;
    public TV.Services.Playback.PlayerService Service => _services.Player;
    [ObservableProperty] private bool _loopCurrentEpisode;
    [ObservableProperty] private int _aspectModeIndex;
    [ObservableProperty] private long _subtitleDelayMs;
    [ObservableProperty] private double _subtitleSpeed = 1.0;
    [ObservableProperty] private long _audioDelayMs;
    [ObservableProperty] private string _mediaInfoText = string.Empty;
    [ObservableProperty] private bool _showMediaInfo;
    [ObservableProperty] private bool _isAiSubtitleBusy;
    [ObservableProperty] private string _aiSubtitleStatus = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveCaption))] private bool _isRealtimeSubtitleActive;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveCaption))] private string _liveCaptionOriginal = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveCaption))] private string _liveCaptionTranslated = string.Empty;
    public bool HasLiveCaption => IsRealtimeSubtitleActive &&
        (!string.IsNullOrWhiteSpace(LiveCaptionOriginal) || !string.IsNullOrWhiteSpace(LiveCaptionTranslated));
    internal string? ActiveExternalSubtitlePath => _activeExternalSubtitlePath;
    internal string? LastGeneratedAiSubtitlePath => _lastGeneratedAiSubtitlePath;
    internal string? CurrentLocalFilePath => _resolvedMedia is { Url: { Length: > 0 } u } && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    public static readonly string[] AspectModeNames = ["默认比例", "16:9", "4:3", "21:9", "填充裁剪"];
    public string AspectModeName => AspectModeNames[Math.Clamp(AspectModeIndex, 0, AspectModeNames.Length - 1)];
    internal IReadOnlyList<CloudSubtitleTrack> CloudSubtitleTracks => _resolvedMedia?.CloudSubtitles ?? [];
    internal CloudSubtitleTrack? SelectedCloudSubtitle { get; private set; }
    public IReadOnlyList<PlaybackTrack> AudioTracks => Service.LastSnapshot.AudioTracks;
    public IReadOnlyList<PlaybackTrack> SubtitleTracks => Service.LastSnapshot.SubtitleTracks;
    public int SelectedAudioTrack => Service.LastSnapshot.AudioTrack;
    public int SelectedSubtitleTrack => Service.LastSnapshot.SubtitleTrack;
    public event EventHandler<IReadOnlyList<VodItem>>? SourceRecoveryOffered;
    [ObservableProperty] private EpisodeTiming _timing = new();
    [ObservableProperty] private bool _skipEnabled = true;
    [ObservableProperty] private string _timingMessage = string.Empty;
    public bool HasTiming => Timing.IntroEndMs > 0 || Timing.OutroStartMs > 0;
    partial void OnTimingChanged(EpisodeTiming value)
    {
        OnPropertyChanged(nameof(HasTiming));
        OnPropertyChanged(nameof(IntroTimeText));
        OnPropertyChanged(nameof(OutroTimeText));
    }

    // ===== PCbox 影视仓式播放条显示文本 =====
    private static string FormatClock(long ms)
    {
        var total = Math.Max(0, ms / 1000);
        return $"{total / 60:00}:{total % 60:00}";
    }
    public string IntroTimeText => $"片头{FormatClock(Timing.IntroEndMs)}";
    public string OutroTimeText => $"片尾{FormatClock(Timing.OutroStartMs)}";
    public string HardwareDecodeText => _services.Player.HardwareDecoding ? "硬解" : "软解";
    public string RendererText => _services.Player.HardwareDecoding ? "D3D11" : "CPU";
    public string PlayerEngineText => "IJK";
    public void RefreshControlTexts()
    {
        OnPropertyChanged(nameof(HardwareDecodeText));
        OnPropertyChanged(nameof(RendererText));
    }

    /// <summary>是否已请求媒体：供播放器页中央空态使用。</summary>
    public bool HasMedia => _request is not null;
    /// <summary>中央提示/错误态是否可见：缓冲中不叠加提示面板。</summary>
    public bool ShowStatusPanel => !IsBuffering && !string.IsNullOrWhiteSpace(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(ShowStatusPanel));
    partial void OnIsBufferingChanged(bool value) => OnPropertyChanged(nameof(ShowStatusPanel));

    /// <summary>播放器页面把 VideoView 挂好后调用，避免“先播后挂”导致黑屏无声。</summary>
    public void NotifyVideoReady() => _viewReady.TrySetResult();

    internal PlaybackRequest? GetResumeRequest()
    {
        var resume = _request?.Copy();
        if (resume is not null && _resolvedMedia is not null && !_services.Player.IsStopping)
            resume.StartPositionMs = Math.Max(0, _services.Player.LastSnapshot.TimeMs);
        return resume;
    }


    private void ResetVideoReady()
    {
        if (_viewReady.Task.IsCompleted) _viewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task LoadAsync(PlaybackRequest request)
    {
        var (generation, cancellation) = BeginLoadOperation();
        await _loadGate.WaitAsync();
        try
        {
            if (generation != _loadGeneration) return;
            await LoadCoreAsync(request, cancellation.Token, generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { _loadGate.Release(); }
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginLoadOperation()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cancellation);
        previous.Cancel();
        previous.Dispose();
        _qualityCts?.Cancel();
        return (generation, cancellation);
    }

    private bool IsCurrentLoad(long generation, CancellationToken token) =>
        generation == _loadGeneration && !token.IsCancellationRequested;

    private async Task LoadCoreAsync(PlaybackRequest request, CancellationToken token, long generation)
    {
        ResetCloudSubtitles();
        CancelAiSubtitle();
        _ = StopRealtimeSubtitleAsync();
        _activeExternalSubtitlePath = null;
        _activeExternalSubtitleCues = null;
        _lastGeneratedAiSubtitlePath = null;
        _externalSubtitleSpeedBaked = false;
        _lastAppliedDynamicSpuDelay = long.MinValue;
        _pauseOverrideActive = false;
        _subtitleSpeedDebounceCts?.Cancel();
        SubtitleSpeed = 1.0;
        RequiredLoginProvider = null;
        QualityMessage = string.Empty;
        StopTimerAndSave();
        DanmakuCleared?.Invoke(this, EventArgs.Empty);
        try
        {
            if (_request is not null)
            {
                var previous = _resolvedMedia;
                await _services.StopPlayerAsync();
                ReleaseMedia(previous);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) StatusMessage = "无法停止上一条视频：" + ex.Message;
            return;
        }
        if (token.IsCancellationRequested) return;
        request = request.Copy();
        _request = request;
        OnPropertyChanged(nameof(HasMedia));
        _resolvedMedia = null;
        _isLive = request.Kind == PlaybackKind.Live;
        _hasPlayedMedia = false;
        _lineRecoveryAttempted = false;
        _refreshedSource = false;
        _manualPaused = false;
        _bufferingPaused = false;
        _bufferEvents = 0;
        _lastProgressMs = -1;
        _lastProgressAt = DateTimeOffset.UtcNow;
        _introSkipped = _outroSkipped = false;
        Timing = new();
        TimingMessage = string.Empty;
        _settingQuality = true;
        SelectedQuality = QualityOptions[0];
        _settingQuality = false;
        PositionMs = 0;
        DurationMs = 0;
        PlaybackRequest? pendingMedia = null;
        try
        {
            var settings = _services.Database;
            _autoNext = !string.Equals(await settings.GetSettingAsync("playback.autoNext", token), "false", StringComparison.OrdinalIgnoreCase);
            _autoSwitchLine = !string.Equals(await settings.GetSettingAsync("playback.autoSwitchLine", token), "false", StringComparison.OrdinalIgnoreCase);
            var defaultQuality = request.Kind == PlaybackKind.Vod
                ? DefaultQualityOptions[Math.Clamp(await ReadIntAsync("playback.defaultQuality", 0, 0, 4), 0, DefaultQualityOptions.Count - 1)]
                : DefaultQualityOptions[0];
            _settingQuality = true;
            SelectedQuality = defaultQuality;
            _settingQuality = false;
            if (string.Equals(await settings.GetSettingAsync("playback.resume", token), "false", StringComparison.OrdinalIgnoreCase)) request.StartPositionMs = 0;
            _services.Player.HardwareDecoding = !string.Equals(await settings.GetSettingAsync("playback.hardware", token), "false", StringComparison.OrdinalIgnoreCase);
            _services.Player.NetworkCachingMs = await ReadIntAsync("playback.networkCache", 3600, 1200, 12000);
            _services.Player.LiveCachingMs = await ReadIntAsync("playback.liveCache", 1800, 500, 8000);
            _adaptiveQuality = !string.Equals(await settings.GetSettingAsync("playback.adaptiveQuality", token), "false", StringComparison.OrdinalIgnoreCase);
            SkipEnabled = !string.Equals(await settings.GetSettingAsync("playback.skipEnabled", token), "false", StringComparison.OrdinalIgnoreCase);
            if (_lastSaved == DateTime.MinValue) Volume = await ReadIntAsync("playback.volume", 85, 0, 100);

            Title = request.Title;
            Subtitle = request.Subtitle;
            StatusMessage = "正在解析播放地址…";
            HasPlaylist = request.Kind == PlaybackKind.Vod && request.Lines.Count > 0;
            Lines.Clear();
            Episodes.Clear();
            _suppressSelection = true;
            foreach (var line in request.Lines) Lines.Add(line);
            SelectedLine = request.Lines.ElementAtOrDefault(request.LineIndex) ?? request.Lines.FirstOrDefault();
            if (SelectedLine is not null) foreach (var episode in SelectedLine.Episodes) Episodes.Add(episode);
            SelectedEpisode = SelectedLine?.Episodes.ElementAtOrDefault(request.EpisodeIndex) ?? SelectedLine?.Episodes.FirstOrDefault();
            _suppressSelection = false;
            UpdateNavigationState();
            await LoadTimingAsync(token);

            PlaybackRequest media;
            try { media = await _services.Playback.ResolveAsync(request, SelectedQuality, GetHdrOutputAsync, token); }
            catch (NotSupportedException) when (SelectedQuality.IsSpecific)
            {
                QualityMessage = $"默认 {SelectedQuality.Name} 不可用，已使用自动画质";
                _settingQuality = true;
                SelectedQuality = QualityOptions[0];
                _settingQuality = false;
                media = await _services.Playback.ResolveAsync(request, SelectedQuality, GetHdrOutputAsync, token);
            }
            pendingMedia = media;
            _isLocalFile = Uri.TryCreate(media.Url, UriKind.Absolute, out var mediaUri) && mediaUri.IsFile;
            if (!_isLocalFile && request.Url.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
                _services.Player.NetworkCachingMs = Math.Max(_services.Player.NetworkCachingMs, 4500);
            SubtitleDelayMs = 0;
            AudioDelayMs = 0;
            MediaInfoText = string.Empty;
            ResolutionText = string.Empty;
            StatusMessage = "正在打开媒体…";
            await _viewReady.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            await _services.Player.PlayAsync(media, token);
            if (!IsCurrentLoad(generation, token)) return;
            if (media.Subtitles.FirstOrDefault(u => u.IsFile && File.Exists(u.LocalPath)) is { } sidecarSub)
            {
                _activeExternalSubtitlePath = sidecarSub.LocalPath;
                _activeExternalSubtitleCues = null;
            }
            _resolvedMedia = media;
            SelectedQuality = DisplayQuality(media, SelectedQuality);
            DanmakuChanged?.Invoke(this, media.Danmaku);
            _timer.Start();
        }
        catch (OperationCanceledException) { }
        catch (CloudAuthenticationException ex) when (!token.IsCancellationRequested)
        {
            RequiredLoginProvider = ex.Provider;
            StatusMessage = $"{ex.Provider} 网盘需要登录后才能播放。登录成功将继续当前影片。";
            Diagnostics.Log($"Cloud authentication required: {ex.Provider}");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            Diagnostics.Log("Play failed: " + ex);
            if (ex is not NotSupportedException && request.Item is not null)
            {
                StatusMessage = "当前来源打开失败，正在尝试恢复…";
                _ = RecoverPlaybackAsync();
            }
            else StatusMessage = "播放失败：" + ex.Message;
        }
        finally
        {
            if (!ReferenceEquals(_resolvedMedia, pendingMedia)) ReleaseMedia(pendingMedia, _resolvedMedia);
        }
    }

    public async Task ResumeAfterCloudLoginAsync()
    {
        if (_request is null) return;
        var request = _request.Copy();
        if (_resolvedMedia is not null && !_services.Player.IsStopping)
            request.StartPositionMs = Math.Max(0, _services.Player.LastSnapshot.TimeMs);
        RequiredLoginProvider = null;
        await LoadAsync(request);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_request is null) return;
        var desiredPlaying = !IsPlaying;
        _manualPaused = !desiredPlaying;
        _bufferingPaused = false;
        _pauseDesiredPlaying = desiredPlaying;
        _pauseOverrideActive = true;
        _pauseOverrideUntilTs = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.65);

        // Optimistic 0ms UI + Danmaku sync before LibVLC native lock finishes
        IsPlaying = desiredPlaying;
        PlayPauseGlyph = desiredPlaying ? "\uE769" : "\uE768";
        PlaybackStateChanged?.Invoke(this, desiredPlaying);

        _services.Player.SetPause(!desiredPlaying);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        var (generation, cancellation) = BeginLoadOperation();
        ResetCloudSubtitles();
        CancelAiSubtitle();
        await StopRealtimeSubtitleAsync();
        StopTimerAndSave();
        DanmakuCleared?.Invoke(this, EventArgs.Empty);
        await _loadGate.WaitAsync();
        try
        {
            if (generation != _loadGeneration) return;
            await _services.StopPlayerAsync();
            ReleaseMedia(_resolvedMedia);
            _resolvedMedia = null;
            _pauseOverrideActive = false;
            IsPlaying = false;
            PlayPauseGlyph = "\uE768";
            IsBuffering = false;
            StatusMessage = "已停止播放";
        }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) StatusMessage = ex.Message; }
        finally { _loadGate.Release(); }
    }

    /// <summary>离开播放器页面前调用：停播并等解码线程退出，避免交换链被销毁后 VLC 写帧导致进程崩溃。</summary>
    public async Task PrepareLeaveAsync()
    {
        var (generation, _) = BeginLoadOperation();
        ResetVideoReady();
        ResetCloudSubtitles();
        CancelAiSubtitle();
        await StopRealtimeSubtitleAsync();
        _timer.Stop();
        DanmakuCleared?.Invoke(this, EventArgs.Empty);
        await _loadGate.WaitAsync();
        try
        {
            if (generation != _loadGeneration) return;
            if (_request is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var save = SaveHistoryAsync(timeout.Token);
                var saveVolume = _services.Database.SetSettingAsync("playback.volume", Volume.ToString(System.Globalization.CultureInfo.InvariantCulture), timeout.Token);
                _request = null;
                OnPropertyChanged(nameof(HasMedia));
                _hasPlayedMedia = false;
                await Task.WhenAll(save.WaitAsync(timeout.Token), saveVolume.WaitAsync(timeout.Token), _services.StopPlayerAsync().WaitAsync(timeout.Token))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                ReleaseMedia(_resolvedMedia);
                _resolvedMedia = null;
            }
            _pauseOverrideActive = false;
            _subtitleSpeedDebounceCts?.Cancel();
            IsPlaying = false;
            PlayPauseGlyph = "\uE768";
            IsBuffering = false;
            RequiredLoginProvider = null;
            StatusMessage = string.Empty;
        }
        finally { _loadGate.Release(); }
    }

    // ===== PCbox 影视仓式播放控制命令 =====
    [RelayCommand]
    private void Replay()
    {
        if (_request is null) return;
        _services.Player.Seek(0);
        PositionMs = 0;
        PositionText = Format(0);
        SeekPerformed?.Invoke(this, 0);
        if (!IsPlaying) TogglePause();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_request is null) return;
        var request = _request.Copy();
        request.StartPositionMs = Math.Max(0, _services.Player.LastSnapshot.TimeMs);
        await LoadAsync(request);
    }

    [RelayCommand]
    private void ToggleHardwareDecode()
    {
        var next = !_services.Player.HardwareDecoding;
        _services.Player.HardwareDecoding = next;
        try
        {
            if (_services.Player.MediaPlayer is { } mediaPlayer)
                mediaPlayer.EnableHardwareDecoding = next;
        }
        catch (Exception ex) { Diagnostics.Log($"切换硬解失败: {ex.GetType().Name}"); }
        RefreshControlTexts();
    }

    [RelayCommand]
    private void CycleRenderer()
    {
        // LibVLC 本机内核：在硬解(D3D11) / 软解之间轮换，视觉上对应影视仓的「渲染」切换。
        ToggleHardwareDecode();
    }

    [RelayCommand]
    private void CyclePlayerEngine()
    {
        // 影视仓的 IJK/EXO/系统为安卓播放器内核；Windows 端固定 LibVLC(IJK) 内核。
        // 这里轮换解码组合以对应「播放器内核」切换的交互形态。
        ToggleHardwareDecode();
    }

    [RelayCommand]
    private void FullScreen() => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CompactOverlay() => CompactOverlayRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NextEpisode() => StepEpisode(1);

    [RelayCommand]
    private void PreviousEpisode() => StepEpisode(-1);

    private void StepEpisode(int delta)
    {
        if (SelectedLine is null || _request is null) return;
        var next = _request.EpisodeIndex + delta;
        if (next < 0 || next >= SelectedLine.Episodes.Count) return;
        PlaySelection(_request.LineIndex, next, SelectedLine, SelectedLine.Episodes[next]);
    }

    public void SeekBy(double seconds)
    {
        var snapshot = _services.Player.LastSnapshot;
        if (snapshot.LengthMs <= 0) return;
        var target = Math.Clamp(snapshot.TimeMs + (long)(seconds * 1000), 0, snapshot.LengthMs);
        _services.Player.Seek(target);
        PositionMs = target;
        PositionText = Format(target);
        SeekPerformed?.Invoke(this, target);
    }

    public void SetAspectMode(int index)
    {
        AspectModeIndex = Math.Clamp(index, 0, AspectModeNames.Length - 1);
        OnPropertyChanged(nameof(AspectModeName));
        var (aspect, crop) = AspectModeIndex switch
        {
            1 => ("16:9", (string?)null),
            2 => ("4:3", (string?)null),
            3 => ("21:9", (string?)null),
            4 => ((string?)null, "16:9"),
            _ => ((string?)null, (string?)null)
        };
        _services.Player.SetVideoGeometry(aspect, crop);
    }

    public void AdjustSubtitleDelay(long deltaMs) => SetSubtitleDelay(SubtitleDelayMs + deltaMs);

    public void SetSubtitleDelay(long totalMs)
    {
        SubtitleDelayMs = Math.Clamp(totalMs, -60_000, 60_000);
        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
    }

    public void ResetSubtitleDelay()
    {
        SubtitleDelayMs = 0;
        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
    }

    /// <summary>
    /// Live slider preview: applies instant dynamic SPU delay and debounces external .srt retime reload.
    /// </summary>
    public void SetSubtitleSpeedLive(double speed)
    {
        var clamped = Math.Clamp(Math.Round(speed, 3), 0.50, 1.50);
        if (Math.Abs(clamped - SubtitleSpeed) < 0.0005) return;
        SubtitleSpeed = clamped;
        _externalSubtitleSpeedBaked = false;
        _lastAppliedDynamicSpuDelay = long.MinValue;
        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);

        if (string.IsNullOrWhiteSpace(_activeExternalSubtitlePath) || !File.Exists(_activeExternalSubtitlePath))
            return;

        var prev = Interlocked.Exchange(ref _subtitleSpeedDebounceCts, new CancellationTokenSource());
        prev?.Cancel();
        prev?.Dispose();
        var cts = _subtitleSpeedDebounceCts;
        _ = DebouncedBakeSubtitleSpeedAsync(clamped, cts.Token);
    }

    private async Task DebouncedBakeSubtitleSpeedAsync(double speed, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            if (!token.IsCancellationRequested && Math.Abs(SubtitleSpeed - speed) < 0.0005)
                await BakeExternalSubtitleSpeedAsync(speed);
        }
        catch (OperationCanceledException) { }
    }

    public async Task SetSubtitleSpeedAsync(double speed)
    {
        var clamped = Math.Clamp(Math.Round(speed, 3), 0.50, 1.50);
        _subtitleSpeedDebounceCts?.Cancel();
        if (Math.Abs(clamped - SubtitleSpeed) < 0.0005 && (_externalSubtitleSpeedBaked || string.IsNullOrWhiteSpace(_activeExternalSubtitlePath)))
            return;
        SubtitleSpeed = clamped;
        _lastAppliedDynamicSpuDelay = long.MinValue;
        await BakeExternalSubtitleSpeedAsync(clamped);
    }

    private async Task BakeExternalSubtitleSpeedAsync(double clamped)
    {
        if (!string.IsNullOrWhiteSpace(_activeExternalSubtitlePath) && File.Exists(_activeExternalSubtitlePath))
        {
            try
            {
                if (Math.Abs(clamped - 1.0) <= 0.0005)
                {
                    _externalSubtitleSpeedBaked = false;
                    await _services.Player.AddSubtitleAsync(new Uri(_activeExternalSubtitlePath));
                    ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
                    return;
                }

                _activeExternalSubtitleCues ??= SubtitleFileCodec.ParseFile(_activeExternalSubtitlePath);
                if (_activeExternalSubtitleCues.Count > 0)
                {
                    var retimed = SubtitleFileCodec.RetimeCues(_activeExternalSubtitleCues, clamped, 0);
                    var dir = Path.Combine(TV.Services.Persistence.AppPaths.Root, "cache", "subtitles");
                    Directory.CreateDirectory(dir);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes($"{_activeExternalSubtitlePath}|{clamped:0.000}")))[..16].ToLowerInvariant();
                    var dest = Path.Combine(dir, $"retimed-{hash}.srt");
                    await File.WriteAllTextAsync(dest, SubtitleFileCodec.WriteSrt(retimed), new System.Text.UTF8Encoding(false));
                    if (await _services.Player.AddSubtitleAsync(new Uri(dest)))
                    {
                        _externalSubtitleSpeedBaked = true;
                        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"External subtitle speed retime fallback: {ex.GetType().Name}");
            }
        }

        // Embedded subtitle track or fallback: drive smooth dynamic SPU delay compensation in OnTick
        _externalSubtitleSpeedBaked = false;
        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
    }

    public async Task ResetSubtitleTimingAsync()
    {
        SubtitleDelayMs = 0;
        await SetSubtitleSpeedAsync(1.0);
        ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
    }

    private void ApplyEffectiveSubtitleDelay(long mediaTimeMs)
    {
        var effectiveMs = SubtitleDelayMs;
        if (!_externalSubtitleSpeedBaked && Math.Abs(SubtitleSpeed - 1.0) > 0.0005 && mediaTimeMs > 0)
        {
            // speed > 1.0 makes subtitles appear earlier (negative delay); speed < 1.0 makes them later (positive delay)
            var driftMs = (long)Math.Round(mediaTimeMs * (1.0 - SubtitleSpeed));
            effectiveMs = Math.Clamp(SubtitleDelayMs + driftMs, -120_000, 120_000);
        }
        if (effectiveMs != _lastAppliedDynamicSpuDelay)
        {
            _lastAppliedDynamicSpuDelay = effectiveMs;
            _services.Player.SetSubtitleDelayMs(effectiveMs);
        }
    }

    public void AdjustAudioDelay(long deltaMs)
    {
        AudioDelayMs = Math.Clamp(AudioDelayMs + deltaMs, -10_000, 10_000);
        _services.Player.SetAudioDelayMs(AudioDelayMs);
    }

    public void ResetAudioDelay()
    {
        AudioDelayMs = 0;
        _services.Player.SetAudioDelayMs(0);
    }

    public void StepFrame()
    {
        if (IsPlaying) TogglePause();
        _services.Player.NextFrame();
    }

    public async Task<string?> TakeSnapshotAsync()
    {
        if (_request is null) return null;
        var folder = AppPaths.DefaultScreenshots;
        Directory.CreateDirectory(folder);
        var safeTitle = string.Concat(Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (safeTitle.Length > 40) safeTitle = safeTitle[..40];
        var file = Path.Combine(folder, $"{(safeTitle.Length > 0 ? safeTitle : "snapshot")}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        return await _services.Player.TakeSnapshotAsync(file) ? file : null;
    }

    partial void OnSelectedEpisodeChanged(Episode? value)
    {
        if (_suppressSelection || value is null || _request is null || SelectedLine is null) return;
        var index = SelectedLine.Episodes.IndexOf(value);
        if (index < 0 || index == _request.EpisodeIndex && _request.Url == value.Url) return;
        PlaySelection(_request.LineIndex, index, SelectedLine, value);
    }

    partial void OnSelectedLineChanged(PlaybackLine? value)
    {
        if (_suppressSelection || value is null || _request is null) return;
        _suppressSelection = true;
        Episodes.Clear();
        foreach (var episode in value.Episodes) Episodes.Add(episode);
        SelectedEpisode = value.Episodes.FirstOrDefault();
        _suppressSelection = false;
        if (SelectedEpisode is { } firstEpisode) PlaySelection(Lines.IndexOf(value), 0, value, firstEpisode);
    }

    private void PlaySelection(int lineIndex, int episodeIndex, PlaybackLine line, Episode episode, long startPositionMs = 0)
    {
        if (_request is not { } current) return;
        var next = current.Copy();
        next.LineIndex = lineIndex;
        next.EpisodeIndex = episodeIndex;
        next.Url = episode.Url;
        next.Subtitle = $"{next.Item?.SiteName} · {line.Name} · {episode.Name}";
        next.StartPositionMs = startPositionMs;
        _ = LoadAsync(next);
    }

    public Task ChangeQualityAsync(QualityOption quality) => ApplyQualityAsync(quality, refresh: false);

    internal Task RefreshBilibiliQualityAsync()
    {
        if (!IsBilibiliPlayback || _request is null) return Task.CompletedTask;
        return _resolvedMedia is null ? LoadAsync(_request) : ApplyQualityAsync(DefaultQualityOptions[0], refresh: true);
    }

    private static QualityOption DisplayQuality(PlaybackRequest media, QualityOption requested) =>
        (requested.IsSpecific || requested.RequestsOriginal) && media.ResolvedQuality is { } actual ? actual : requested;

    private async Task ApplyQualityAsync(QualityOption quality, bool refresh)
    {
        if (_settingQuality || _request is not { } current || !refresh &&
            (SelectedQuality == quality || !CanSelectQuality(quality))) return;
        if (_resolvedMedia?.HasFixedVideoTrack == true && _services.Player.CurrentVideoHeight > 0)
        {
            QualityMessage = string.Empty;
            SelectedQuality = quality;
            return;
        }
        _qualityCts?.Cancel();
        _qualityCts?.Dispose();
        _qualityCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var token = _qualityCts.Token;
        QualityMessage = string.Empty;
        PlaybackRequest? media = null;
        try
        {
            media = await _services.Playback.ResolveAsync(current, quality, GetHdrOutputAsync, token);
            token.ThrowIfCancellationRequested();
            media.StartPositionMs = current.Kind == PlaybackKind.Vod ? Math.Max(0, _services.Player.LastSnapshot.TimeMs) : 0;
            if (_resolvedMedia?.AvailableQualities?.FirstOrDefault(option => option.IsOriginal && option.Height > 0) is { } original &&
                media.AvailableQualities is { } available && available.Any(option => option.RequestsOriginal && option.Height < 0))
                media.AvailableQualities = available.Select(option => option.RequestsOriginal ? original : option).ToArray();
            var tracks = await _services.Player.CaptureTrackSelectionAsync(token);
            CancelCloudSubtitleLoad();
            if (SelectedCloudSubtitle is { } currentSubtitle && _selectedCloudSubtitleAddress is { } address &&
                media.CloudSubtitles.Any(track => track.Source == currentSubtitle.Source))
            {
                if (!media.Subtitles.Contains(address)) media.Subtitles.Add(address);
            }
            else ClearCloudSubtitleSelection();
            _hasPlayedMedia = false;
            var previous = _resolvedMedia;
            await _services.Player.PlayAsync(media, tracks, token);
            _resolvedMedia = media;
            ReleaseMedia(previous, media);
            DanmakuChanged?.Invoke(this, media.Danmaku);
            SelectedQuality = DisplayQuality(media, quality);
            _timer.Start();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) QualityMessage = ex.Message; }
        finally { if (!ReferenceEquals(_resolvedMedia, media)) ReleaseMedia(media, _resolvedMedia); }
    }

    internal bool CanSelectQuality(QualityOption quality)
    {
        if (_request is null || _resolvedMedia is null || _services.Player.IsStopping || !quality.IsAvailable ||
            _request.Kind == PlaybackKind.Live && _resolvedMedia?.AvailableQualities is null) return false;
        if (!quality.IsSpecific) return true;
        if (_resolvedMedia?.AvailableQualities is { } options) return options.Contains(quality);
        if (_resolvedMedia?.AvailableVideoHeights is { } heights) return heights.Contains(quality.Height);
        return _resolvedMedia?.HasFixedVideoTrack != true || _services.Player.CurrentVideoHeight == quality.Height;
    }

    private void ReleaseMedia(PlaybackRequest? media, PlaybackRequest? retained = null)
    {
        if (media is null || _services.PlayerOnly) return;
        if (media.Url != retained?.Url) _services.Cloud.ReleaseMedia(media.Url);
        foreach (var track in media.AudioTracks)
            if (retained?.AudioTracks.Contains(track) != true) _services.Cloud.ReleaseMedia(track.AbsoluteUri);
        foreach (var subtitle in media.Subtitles)
            if (retained?.Subtitles.Contains(subtitle) != true) _services.Cloud.ReleaseMedia(subtitle.AbsoluteUri);
    }

    private void ReadFixedVideoQuality(VideoFormat? format)
    {
        if (_resolvedMedia is not { HasFixedVideoTrack: true } media || media.ResolvedQuality?.Height > 0 || format is not { } actual) return;
        var original = VideoFormat.ToOptions([actual])[0];
        media.AvailableQualities = media.AvailableQualities is { } options
            ? options.Select(option => option.RequestsOriginal ? original : option).ToArray() : [original];
        media.ResolvedQuality = original;
        SelectedQuality = DisplayQuality(media, SelectedQuality);
    }

    private async Task<bool> GetHdrOutputAsync(CancellationToken token)
    {
        await _viewReady.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
        return _services.Player.HasHdrSurface && VideoDisplay.IsHdrActive(App.Window.AppWindow.Id);
    }

    internal async Task<DanDanPlayEpisode> MatchDanDanPlayAsync(CancellationToken token)
    {
        if (_request is not { } request) throw new InvalidOperationException("当前没有可匹配的视频。");
        var title = request.Item?.Name ?? request.Title;
        var episode = SelectedEpisode?.Name ?? request.Subtitle;
        var durationSec = (int)Math.Round(Math.Max(0, _services.Player.LastSnapshot.LengthMs) / 1000.0);
        var match = await _services.DanDanPlay.MatchAsync(title, episode, request.EpisodeIndex, CurrentLocalFilePath, durationSec, token);
        QualityMessage = $"弹弹play 已匹配：{match.DisplayName}";
        return match;
    }

    internal async Task<OnlineMediaMatch> MatchOnlineMediaAsync(CancellationToken token)
    {
        if (_request is not { } request) throw new InvalidOperationException("当前没有可匹配的视频。");
        var title = request.Item?.Name ?? request.Title;
        var episode = SelectedEpisode?.Name ?? request.Subtitle;
        if (IsBilibiliPlayback && request.Url.Split('+', 2) is { Length: 2 } ids &&
            long.TryParse(ids[0], out var aid) && long.TryParse(ids[1], out var cid))
            return new OnlineMediaMatch(title, episode, aid, cid);
        var match = await _services.OnlineMedia.MatchAsync(title, episode, request.EpisodeIndex, token);
        QualityMessage = $"已匹配：{match.Title} · {match.EpisodeTitle}";
        return match;
    }

    internal async Task<IReadOnlyList<OnlineSubtitleTrack>> GetOnlineSubtitlesAsync(CancellationToken token)
    {
        var match = await MatchOnlineMediaAsync(token);
        return await _services.OnlineMedia.GetSubtitlesAsync(match, token);
    }

    internal async Task SelectOnlineSubtitleAsync(OnlineSubtitleTrack track)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        QualityMessage = "正在下载并转换在线字幕…";
        var file = await _services.OnlineMedia.SaveSubtitleAsync(track, timeout.Token);
        if (!await AddSubtitleAsync(file)) throw new InvalidOperationException("播放器无法加载在线字幕。");
        QualityMessage = $"已加载在线字幕：{track.Name}";
    }

    internal async Task<bool> AddSubtitleAsync(Uri file)
    {
        if (_request is not { } request || !await _services.Player.AddSubtitleAsync(file)) return false;
        if (!request.Subtitles.Contains(file)) request.Subtitles.Add(file);
        if (file.IsFile && File.Exists(file.LocalPath))
        {
            _activeExternalSubtitlePath = file.LocalPath;
            _activeExternalSubtitleCues = null;
            _externalSubtitleSpeedBaked = false;
            SubtitleSpeed = 1.0;
            ApplyEffectiveSubtitleDelay(_services.Player.LastSnapshot.TimeMs);
        }
        CancelCloudSubtitleLoad();
        ClearCloudSubtitleSelection();
        return true;
    }

    [RelayCommand]
    public void CancelAiSubtitle()
    {
        var cts = Interlocked.Exchange(ref _aiSubtitleCts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        IsAiSubtitleBusy = false;
        AiSubtitleStatus = string.Empty;
    }

    public async Task<bool> GenerateAiSubtitleAsync(bool translateAfter)
    {
        if (_resolvedMedia is not { Url: { Length: > 0 } mediaUrl } || _request is null)
        {
            QualityMessage = "当前没有正在播放的视频，无法生成 AI 字幕。";
            return false;
        }
        if (IsAiSubtitleBusy) return false;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var prev = Interlocked.Exchange(ref _aiSubtitleCts, cts);
        prev?.Cancel();
        prev?.Dispose();

        IsAiSubtitleBusy = true;
        AiSubtitleStatus = "正在准备 AI 字幕生成任务…";
        try
        {
            var progress = new Progress<AiSubtitleProgress>(p => _dispatcher.TryEnqueue(() => AiSubtitleStatus = p.Stage));
            TimeSpan? videoDuration = DurationMs > 1000 ? TimeSpan.FromMilliseconds(DurationMs) : null;
            var result = await _services.BailianSubtitle.GenerateSubtitleAsync(
                mediaUrl, _resolvedMedia.Headers, Title, translateAfter, progress, cts.Token, videoDuration);
            if (cts.IsCancellationRequested) return false;

            _lastGeneratedAiSubtitlePath = result.FilePath;
            if (!await AddSubtitleAsync(new Uri(result.FilePath)))
                throw new InvalidOperationException("AI 字幕已生成，但播放器加载字幕轨失败。");

            QualityMessage = result.IsTranslated
                ? $"AI 双语/翻译字幕已生成并对齐时间轴（{result.CueCount} 条）"
                : $"AI 字幕已生成并对齐原视频时间轴（{result.CueCount} 条 · {result.ModelUsed}）";
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            QualityMessage = "已取消 AI 字幕任务";
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"AI subtitle generation failed: {ex.GetType().Name} {ex.Message}");
            QualityMessage = "AI 字幕生成失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_aiSubtitleCts, cts))
                Interlocked.CompareExchange(ref _aiSubtitleCts, null, cts);
            cts.Dispose();
            IsAiSubtitleBusy = false;
            AiSubtitleStatus = string.Empty;
        }
    }

    public async Task<bool> TranslateSubtitleAsync(string? sourceFilePath = null)
    {
        var targetPath = sourceFilePath ?? _activeExternalSubtitlePath ?? _lastGeneratedAiSubtitlePath;
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            QualityMessage = "当前没有可翻译的外部字幕文件，请先加载本地字幕或使用 AI 生成字幕。";
            return false;
        }
        if (IsAiSubtitleBusy) return false;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var prev = Interlocked.Exchange(ref _aiSubtitleCts, cts);
        prev?.Cancel();
        prev?.Dispose();

        IsAiSubtitleBusy = true;
        AiSubtitleStatus = "正在准备 AI 字幕翻译任务…";
        try
        {
            var progress = new Progress<AiSubtitleProgress>(p => _dispatcher.TryEnqueue(() => AiSubtitleStatus = p.Stage));
            var result = await _services.BailianSubtitle.TranslateSubtitleFileAsync(targetPath, progress, cts.Token);
            if (cts.IsCancellationRequested) return false;

            _lastGeneratedAiSubtitlePath = result.FilePath;
            if (!await AddSubtitleAsync(new Uri(result.FilePath)))
                throw new InvalidOperationException("翻译字幕已生成，但播放器加载失败。");

            QualityMessage = $"AI 字幕翻译完成并已加载（{result.CueCount} 条 · {result.ModelUsed}）";
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            QualityMessage = "已取消字幕翻译";
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"AI subtitle translation failed: {ex.GetType().Name} {ex.Message}");
            QualityMessage = "AI 字幕翻译失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_aiSubtitleCts, cts))
                Interlocked.CompareExchange(ref _aiSubtitleCts, null, cts);
            cts.Dispose();
            IsAiSubtitleBusy = false;
            AiSubtitleStatus = string.Empty;
        }
    }

    public async Task ToggleRealtimeSubtitleAsync()
    {
        if (IsRealtimeSubtitleActive)
        {
            await StopRealtimeSubtitleAsync();
            QualityMessage = "实时字幕已关闭";
            return;
        }
        if (_request is null)
        {
            QualityMessage = "当前没有正在播放的视频。";
            return;
        }
        try
        {
            _services.RealtimeSubtitle.CaptionUpdated -= OnLiveCaptionUpdated;
            _services.RealtimeSubtitle.CaptionUpdated += OnLiveCaptionUpdated;
            _services.RealtimeSubtitle.StatusChanged -= OnRealtimeStatusChanged;
            _services.RealtimeSubtitle.StatusChanged += OnRealtimeStatusChanged;
            LiveCaptionOriginal = string.Empty;
            LiveCaptionTranslated = string.Empty;
            await _services.RealtimeSubtitle.StartAsync(() => _services.Player.LastSnapshot.TimeMs, _loadCts.Token);
            IsRealtimeSubtitleActive = true;
        }
        catch (Exception ex)
        {
            _services.RealtimeSubtitle.CaptionUpdated -= OnLiveCaptionUpdated;
            _services.RealtimeSubtitle.StatusChanged -= OnRealtimeStatusChanged;
            IsRealtimeSubtitleActive = false;
            QualityMessage = "实时字幕启动失败：" + ex.Message;
        }
    }

    public async Task StopRealtimeSubtitleAsync()
    {
        _services.RealtimeSubtitle.CaptionUpdated -= OnLiveCaptionUpdated;
        _services.RealtimeSubtitle.StatusChanged -= OnRealtimeStatusChanged;
        if (IsRealtimeSubtitleActive || _services.RealtimeSubtitle.IsRunning)
            await _services.RealtimeSubtitle.StopAsync();
        IsRealtimeSubtitleActive = false;
        LiveCaptionOriginal = string.Empty;
        LiveCaptionTranslated = string.Empty;
    }

    public async Task<bool> ExportSubtitleToAsync(string destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(_lastGeneratedAiSubtitlePath) && File.Exists(_lastGeneratedAiSubtitlePath))
        {
            File.Copy(_lastGeneratedAiSubtitlePath, destinationPath, overwrite: true);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(_activeExternalSubtitlePath) && File.Exists(_activeExternalSubtitlePath))
        {
            File.Copy(_activeExternalSubtitlePath, destinationPath, overwrite: true);
            return true;
        }
        var recorded = _services.RealtimeSubtitle.GetRecordedCues();
        if (recorded.Count > 0)
        {
            var config = await _services.BailianSubtitle.GetConfigAsync();
            var srt = SubtitleFileCodec.WriteSrt(recorded, bilingual: config.Bilingual);
            await File.WriteAllTextAsync(destinationPath, srt, new System.Text.UTF8Encoding(false));
            return true;
        }
        return false;
    }

    private void OnLiveCaptionUpdated(object? sender, LiveCaptionUpdate update)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!IsRealtimeSubtitleActive) return;
            LiveCaptionOriginal = update.OriginalText;
            LiveCaptionTranslated = update.TranslatedText ?? string.Empty;
        });
    }

    private void OnRealtimeStatusChanged(object? sender, string message)
    {
        _dispatcher.TryEnqueue(() => QualityMessage = message);
    }

    internal async Task SelectTrackAsync(bool subtitle, int id)
    {
        if (subtitle) CancelCloudSubtitleLoad();
        var success = await _services.Player.SelectTrackAsync(subtitle, id) || subtitle && id < 0;
        QualityMessage = success ? string.Empty : "无法切换该轨道。";
        if (subtitle && success) ClearCloudSubtitleSelection();
    }

    internal async Task SelectCloudSubtitleAsync(CloudSubtitleTrack track)
    {
        if (_resolvedMedia is not { } media || !media.CloudSubtitles.Contains(track) || SelectedCloudSubtitle == track) return;
        CancelCloudSubtitleLoad();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        _cloudSubtitleCts = request;
        var token = request.Token;
        QualityMessage = "正在加载字幕…";
        try
        {
            var address = await _services.Cloud.ResolveSubtitleAsync(track, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_resolvedMedia, media)) return;
            if (!await _services.Player.AddSubtitleAsync(address))
                throw new InvalidOperationException("无法加载字幕。");
            if (!media.Subtitles.Contains(address)) media.Subtitles.Add(address);
            _selectedCloudSubtitleAddress = address;
            SelectedCloudSubtitle = track;
            QualityMessage = string.Empty;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) QualityMessage = $"字幕加载失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_cloudSubtitleCts, request))
            {
                _cloudSubtitleCts = null;
                request.Dispose();
            }
        }
    }

    private void CancelCloudSubtitleLoad()
    {
        if (_cloudSubtitleCts is not { } pending) return;
        _cloudSubtitleCts = null;
        pending.Cancel();
        pending.Dispose();
        QualityMessage = string.Empty;
    }

    private void ClearCloudSubtitleSelection()
    {
        SelectedCloudSubtitle = null;
        _selectedCloudSubtitleAddress = null;
    }

    private void ResetCloudSubtitles()
    {
        CancelCloudSubtitleLoad();
        ClearCloudSubtitleSelection();
    }

    private async Task LoadTimingAsync(CancellationToken token)
    {
        if (_request is not { } request) return;
        var episode = request.Lines.ElementAtOrDefault(request.LineIndex)?.Episodes.ElementAtOrDefault(request.EpisodeIndex)?.Name ?? request.EpisodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var identity = $"{request.SiteKey}|{request.VodId}|{request.LineIndex}|{request.EpisodeIndex}|{episode}";
        _timingKey = "playback.timing." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..24];
        try
        {
            var json = await _services.Database.GetSettingAsync(_timingKey, token);
            Timing = string.IsNullOrWhiteSpace(json) ? new() : EpisodeTiming.Normalize(System.Text.Json.JsonSerializer.Deserialize<EpisodeTiming>(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"片头片尾设置读取失败: {ex.GetType().Name}");
            Timing = new();
        }
    }

    internal async Task SaveTimingAsync(double introSeconds, double outroSeconds, bool enabled)
    {
        if (!double.IsFinite(introSeconds) || !double.IsFinite(outroSeconds) || introSeconds < 0 || outroSeconds < 0)
            throw new InvalidDataException("片头片尾时间必须是有效的非负秒数。");
        var duration = _services.Player.LastSnapshot.LengthMs;
        var intro = (long)Math.Round(introSeconds * 1000);
        var outro = (long)Math.Round(outroSeconds * 1000);
        if (duration > 0 && (intro >= duration || outro >= duration)) throw new InvalidDataException("跳过时间必须小于当前视频总时长。");
        if (outro > 0 && outro <= intro) throw new InvalidDataException("片尾开始时间必须晚于片头结束时间。");
        Timing = EpisodeTiming.Normalize(new EpisodeTiming(intro, outro));
        SkipEnabled = enabled;
        _introSkipped = _outroSkipped = false;
        if (!string.IsNullOrEmpty(_timingKey))
            await _services.Database.SetSettingAsync(_timingKey, System.Text.Json.JsonSerializer.Serialize(Timing));
        await _services.Database.SetSettingAsync("playback.skipEnabled", enabled ? "true" : "false");
        TimingMessage = "已保存当前线路、当前集的片头片尾设置。";
    }

    internal Task SetTimingFromPositionAsync(bool intro)
    {
        var seconds = _services.Player.LastSnapshot.TimeMs / 1000d;
        return SaveTimingAsync(intro ? seconds : Timing.IntroEndMs / 1000d, intro ? Timing.OutroStartMs / 1000d : seconds, SkipEnabled);
    }

    [RelayCommand]
    private void SkipIntro()
    {
        if (Timing.IntroEndMs <= 0) return;
        _introSkipped = true;
        _services.Player.Seek(Timing.IntroEndMs);
        PositionMs = Timing.IntroEndMs;
        PositionText = Format(Timing.IntroEndMs);
        SeekPerformed?.Invoke(this, Timing.IntroEndMs);
    }

    [RelayCommand]
    private void SkipOutro() => FinishOutro();

    private void FinishOutro()
    {
        if (_outroSkipped) return;
        _outroSkipped = true;
        if (CanNext) NextEpisode();
        else _services.Player.Seek(Math.Max(0, _services.Player.LastSnapshot.LengthMs));
    }

    private void ApplyEpisodeTiming(long position, long duration)
    {
        if (!SkipEnabled || _isSeeking) return;
        if (Timing.IntroEndMs > 1000 && !_introSkipped && position >= 0 && position < Timing.IntroEndMs - 750)
        {
            _introSkipped = true;
            _services.Player.Seek(Timing.IntroEndMs);
            SeekPerformed?.Invoke(this, Timing.IntroEndMs);
            TimingMessage = $"已跳过片头至 {Format(Timing.IntroEndMs)}";
        }
        if (Timing.OutroStartMs > 0 && !_outroSkipped && duration >= Timing.OutroStartMs && position >= Timing.OutroStartMs)
            FinishOutro();
    }

    private async Task<int> ReadIntAsync(string key, int fallback, int minimum, int maximum)
    {
        var value = await _services.Database.GetSettingAsync(key);
        return int.TryParse(value, out var result) ? Math.Clamp(result, minimum, maximum) : fallback;
    }

    partial void OnVolumeChanged(int value)
    {
        _services.Player.SetVolume(value);
        VolumeGlyph = value == 0 ? "\uE74F" : value < 50 ? "\uE993" : "\uE767";
    }

    partial void OnRateChanged(double value)
    {
        if (value > 0)
        {
            _services.Player.SetRate((float)value);
            RateText = $"{value:0.##}x";
        }
    }

    private async void OnTick(object? sender, object e)
    {
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0 || _services.Player.IsStopping) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var snapshot = await _services.Player.ReadSnapshotAsync(deadline.Token);
            if (_request is null || _loadCts.IsCancellationRequested) return;
            var effectivePlaying = snapshot.IsPlaying;
            if (_pauseOverrideActive)
            {
                if (snapshot.IsPlaying == _pauseDesiredPlaying || Stopwatch.GetTimestamp() >= _pauseOverrideUntilTs)
                {
                    _pauseOverrideActive = false;
                }
                else
                {
                    effectivePlaying = _pauseDesiredPlaying;
                    snapshot = snapshot with
                    {
                        IsPlaying = effectivePlaying,
                        State = effectivePlaying ? VLCState.Playing : VLCState.Paused
                    };
                }
            }
            IsPlaying = effectivePlaying;
            PlayPauseGlyph = IsPlaying ? "\uE769" : "\uE768";
            if (snapshot.State == VLCState.Playing)
            {
                _hasPlayedMedia = true;
                ReadFixedVideoQuality(snapshot.VideoFormat);
                if (!string.IsNullOrEmpty(snapshot.MediaInfo) && MediaInfoText != snapshot.MediaInfo)
                {
                    MediaInfoText = snapshot.MediaInfo;
                    // 从 "1920×1080 · 30 fps · …" 中解析出真实宽高，直接显示具体分辨率，如 1920*1080
                    var sizeMatch = Regex.Match(snapshot.MediaInfo, @"(\d+)\s*[×xX]\s*(\d+)");
                    if (sizeMatch.Success
                        && int.TryParse(sizeMatch.Groups[1].Value, out var w) && w > 0
                        && int.TryParse(sizeMatch.Groups[2].Value, out var h) && h > 0)
                    {
                        ResolutionText = $"{w}*{h}";
                    }
                }
            }
            IsBuffering = snapshot.State is VLCState.Opening or VLCState.Buffering;
            BufferingText = snapshot.State == VLCState.Buffering
                ? $"网络缓冲 {Math.Clamp(snapshot.BufferingPercent, 0, 100):0}% · 缓冲后自动续播" : "正在打开媒体…";
            SnapshotUpdated?.Invoke(this, snapshot);
            if (_request.Kind == PlaybackKind.Vod)
            {
                PositionMs = _isSeeking ? PositionMs : snapshot.TimeMs;
                DurationMs = snapshot.LengthMs;
                PositionText = Format(snapshot.TimeMs);
                DurationText = Format(snapshot.LengthMs);
                ApplyEpisodeTiming(snapshot.TimeMs, snapshot.LengthMs);
                if (!_externalSubtitleSpeedBaked && Math.Abs(SubtitleSpeed - 1.0) > 0.0005)
                    ApplyEffectiveSubtitleDelay(snapshot.TimeMs);
                if (_hasPlayedMedia && !_manualPaused && !_isLocalFile) UpdateBuffering(snapshot);
            }
            else
            {
                PositionText = "直播";
                DurationText = string.Empty;
            }
            if (!IsBuffering && snapshot.State is VLCState.Playing or VLCState.Paused) StatusMessage = string.Empty;
            if (snapshot.State == VLCState.Error)
            {
                StatusMessage = "正在尝试恢复播放…";
                _ = RecoverPlaybackAsync();
            }
            else if (snapshot.State == VLCState.Ended && _request.Kind == PlaybackKind.Vod && _recoveryRunning == 0)
            {
                if (_hasPlayedMedia && LoopCurrentEpisode)
                {
                    var replay = _request.Copy();
                    replay.StartPositionMs = 0;
                    _ = LoadAsync(replay);
                    return;
                }
                if (_hasPlayedMedia && CanNext && _autoNext) NextEpisode();
                else if (!_hasPlayedMedia && _autoSwitchLine && TryNextLine()) return;
                else if (_hasPlayedMedia) StatusMessage = "播放完毕";
                else
                {
                    StatusMessage = "媒体地址可能已失效，正在查找可用来源…";
                    _ = RecoverPlaybackAsync();
                }
            }
            if (DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(5)) _ = SaveHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            BufferingText = "播放器响应较慢，正在等待解码器恢复；界面仍可操作。";
        }
        catch (Exception ex) { Diagnostics.Log($"Player state read failed: {ex.GetType().Name}"); }
        finally { Interlocked.Exchange(ref _tickRunning, 0); }
    }

    private void UpdateBuffering(PlayerSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot.TimeMs > _lastProgressMs + 40)
        {
            _lastProgressMs = snapshot.TimeMs;
            _lastProgressAt = now;
            if (_bufferingPaused)
            {
                _bufferingPaused = false;
                _services.Player.SetBufferingPause(false);
            }
            return;
        }
        if (!_bufferingPaused && snapshot.State == VLCState.Playing && now - _lastProgressAt < TimeSpan.FromSeconds(6)) return;
        if (!_bufferingPaused && snapshot.State == VLCState.Buffering && now - _lastProgressAt < TimeSpan.FromSeconds(2)) return;
        if (!_bufferingPaused)
        {
            _bufferingPaused = true;
            _bufferResumeAt = now + TimeSpan.FromMilliseconds(Math.Clamp(_services.Player.NetworkCachingMs, 2500, 6000));
            _bufferEvents = now - _bufferWindowAt > TimeSpan.FromSeconds(90) ? 1 : _bufferEvents + 1;
            _bufferWindowAt = now;
            _services.Player.SetBufferingPause(true);
            IsBuffering = true;
            BufferingText = "网络波动，正在预缓存，稍后自动续播";
            _services.Player.NetworkCachingMs = Math.Min(8000, _services.Player.NetworkCachingMs + 1000);
            if (_adaptiveQuality && !SelectedQuality.RequestsOriginal && _bufferEvents >= 3) _ = LowerQualityAfterBufferingAsync();
            return;
        }
        if (now < _bufferResumeAt || snapshot.BufferingPercent < 99 && now - _bufferResumeAt < TimeSpan.FromSeconds(5)) return;
        _bufferingPaused = false;
        _lastProgressAt = now;
        _services.Player.SetBufferingPause(false);
        IsBuffering = false;
    }

    private async Task LowerQualityAfterBufferingAsync()
    {
        if (_resolvedMedia?.HasFixedVideoTrack == true || _settingQuality) return;
        var ceiling = SelectedQuality.Height > 0 ? SelectedQuality.Height - 1 : 1080;
        var quality = QualityOptions.Where(option => option.IsSpecific && !option.RequestsOriginal && option.Height > 0 && option.Height <= ceiling && CanSelectQuality(option))
            .OrderByDescending(option => option.Height).FirstOrDefault();
        if (quality is null) return;
        _bufferEvents = 0;
        QualityMessage = $"网络多次缓冲，自动切换至 {quality.Name}；可随时手动切回原画";
        await ApplyQualityAsync(quality, false);
    }

    private async Task RecoverPlaybackAsync()
    {
        if (Interlocked.CompareExchange(ref _recoveryRunning, 1, 0) != 0 || _request is not { } request) return;
        var generation = _loadGeneration;
        var token = _loadCts.Token;
        _qualityCts?.Cancel();
        try
        {
            if (!IsCurrentLoad(generation, token) || DateTimeOffset.UtcNow - _lastRecoveryAt < TimeSpan.FromSeconds(8)) return;
            _lastRecoveryAt = DateTimeOffset.UtcNow;
            var resume = _services.Player.LastSnapshot.TimeMs;
            if (_services.Player.HardwareDecoding && _resolvedMedia is { } media)
            {
                _services.Player.HardwareDecoding = false;
                var retry = media.Copy();
                retry.StartPositionMs = resume;
                await _services.Player.PlayAsync(retry, token);
                if (!IsCurrentLoad(generation, token) || !ReferenceEquals(_request, request)) return;
                _resolvedMedia = retry;
                QualityMessage = "已切换兼容解码模式，正在恢复播放。";
                _timer.Start();
                return;
            }
            if (_autoSwitchLine && !_hasPlayedMedia && TryNextLine()) return;
            if (request.Item is null)
            {
                StatusMessage = "播放失败，请尝试其他线路。";
                return;
            }
            if (!_refreshedSource)
            {
                _refreshedSource = true;
                var copy = request.Copy();
                copy.StartPositionMs = resume;
                PlaybackRequest? refreshed = null;
                try
                {
                    refreshed = await _services.Playback.ResolveAsync(copy, QualityOptions[0], GetHdrOutputAsync, token);
                    refreshed.StartPositionMs = resume;
                    var previous = _resolvedMedia;
                    await _services.Player.PlayAsync(refreshed, token);
                    if (!IsCurrentLoad(generation, token) || !ReferenceEquals(_request, request)) return;
                    _resolvedMedia = refreshed;
                    ReleaseMedia(previous, refreshed);
                    DanmakuChanged?.Invoke(this, refreshed.Danmaku);
                    _timer.Start();
                    StatusMessage = "正在使用刷新后的媒体地址恢复播放…";
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostics.Log($"原来源刷新失败: {ex.GetType().Name}");
                }
                finally
                {
                    if (!ReferenceEquals(_resolvedMedia, refreshed)) ReleaseMedia(refreshed, _resolvedMedia);
                }
            }
            if (!IsCurrentLoad(generation, token)) return;
            StatusMessage = request.RecoverMissingSource ? "历史来源失效，正在自动获取最新资源…" : "当前来源可能失效，正在查找可用来源…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(24));
            var candidates = await _services.SourceRecovery.FindAsync(request.Item, timeout.Token);
            if (!IsCurrentLoad(generation, token) || !ReferenceEquals(_request, request)) return;
            if (candidates.Count == 0)
            {
                StatusMessage = "暂未找到有效的新来源，原播放记录已保留。可稍后重试。";
                return;
            }
            if (request.RecoverMissingSource)
            {
                var selected = candidates[0];
                var next = _services.SourceRecovery.Switch(request, selected);
                next.RecoverMissingSource = false;
                next.StartPositionMs = resume;
                QualityMessage = $"已自动切换到 {selected.SiteName}";
                await LoadAsync(next);
                return;
            }
            SourceRecoveryOffered?.Invoke(this, candidates);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentLoad(generation, token)) StatusMessage = "恢复播放失败：" + ex.Message + "。可返回详情页更换来源。";
        }
        finally { Interlocked.Exchange(ref _recoveryRunning, 0); }
    }

    internal async Task SwitchSourceAsync(VodItem source)
    {
        if (_request is null) return;
        var next = _services.SourceRecovery.Switch(_request, source);
        next.StartPositionMs = _services.Player.LastSnapshot.TimeMs;
        next.RecoverMissingSource = false;
        await LoadAsync(next);
    }

    private bool _isSeeking;
    public void BeginSeek() => _isSeeking = true;
    public void EndSeek(double value)
    {
        _isSeeking = false;
        var target = Math.Max(0, (long)value);
        _services.Player.Seek(target);
        PositionMs = target;
        PositionText = Format(target);
        SeekPerformed?.Invoke(this, target);
    }

    private bool TryNextLine()
    {
        if (_lineRecoveryAttempted || SelectedLine is null || _request is null) return false;
        _lineRecoveryAttempted = true;
        var index = Lines.IndexOf(SelectedLine);
        if (index < 0 || index + 1 >= Lines.Count) return false;
        var line = Lines[index + 1];
        if (line.Episodes.ElementAtOrDefault(Math.Clamp(_request.EpisodeIndex, 0, line.Episodes.Count - 1)) is not { } episode) return false;
        QualityMessage = "当前线路未能起播，正在尝试下一线路";
        PlaySelection(index + 1, Math.Clamp(_request.EpisodeIndex, 0, line.Episodes.Count - 1), line, episode, _services.Player.LastSnapshot.TimeMs);
        return true;
    }

    private void UpdateNavigationState()
    {
        CanNext = SelectedLine is not null && _request is not null && _request.EpisodeIndex < SelectedLine.Episodes.Count - 1;
        CanPrevious = SelectedLine is not null && _request is not null && _request.EpisodeIndex > 0;
    }

    private void StopTimerAndSave()
    {
        _timer.Stop();
        _ = SaveHistoryAsync();
    }

    private async Task SaveHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_request is not { } request || !_hasPlayedMedia) return;
        _lastSaved = DateTime.UtcNow;
        try
        {
            var service = _services.Player;
            var snapshot = request.Copy();
            var state = service.LastSnapshot;
            var position = Math.Max(0, state.TimeMs);
            var duration = Math.Max(0, state.LengthMs);
            await _services.Database.SaveHistoryAsync(snapshot, position, duration, cancellationToken);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"保存播放记录失败: {ex.Message}"); }
    }

    private void OnCloudAuthenticationRequired(object? sender, CloudProviderKind provider)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_request is null) return;
            RequiredLoginProvider = provider;
            StatusMessage = $"{provider} 网盘登录已过期，重新登录后将继续播放。";
        });
    }

    private static string Format(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
