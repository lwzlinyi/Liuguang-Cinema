using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;
using TV.ViewModels;
using TV.Models;
using TV.Controls;
using TV.Services;
using TV.Services.AI;
using TV.Services.Playback;
using LibVLCSharp.Shared;

namespace TV.Views;

public sealed partial class PlayerPage : Page
{
    private static readonly double[] Rates = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    private static readonly double[] DanmakuSpeeds = [0.6, 0.8, 1.0, 1.25, 1.5];
    private static readonly double[] DanmakuAreas = [0.25, 0.50, 0.75, 1.0];
    private static readonly string[] BasicDanmakuBlockedWords = ["加群", "代充", "博彩", "刷单", "加微信"];

    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _osdTimer;
    private readonly DispatcherQueueTimer _danmakuSaveTimer;
    private bool _controlsVisible = true;
    private PlayerViewModel? _vm;
    private CancellationTokenSource? _danmakuLoad;
    private DanmakuEntry[] _rawComments = [];
    private DanmakuPreferences _danmaku = new();
    private Uri? _danmakuAddress;
    private Uri? _localDanmakuOverride;
    private Flyout? _danmakuFlyout;
    private Flyout? _subtitleFlyout;
    private ContentDialog? _cloudLoginDialog;
    private BilibiliLoginDialog? _bilibiliLoginDialog;
    private ContentDialog? _sourceDialog;
    private ContentDialog? _onlineSubtitleDialog;
    private ContentDialog? _danmakuSearchDialog;
    private Flyout? _timingFlyout;
    private long? _manualDanDanPlayEpisodeId;
    private int _volumeBeforeMute = 85;
    private bool _danmakuPrefsLoaded;
    private bool _seekDragging;
    private bool _seekHandlersWired;
    private readonly PointerEventHandler _seekPressedHandler;
    private readonly PointerEventHandler _seekReleasedHandler;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint cursorName);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);
    private static void ShowArrowCursor() => SetCursor(LoadCursor(0, 32512));
    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch { }
    }

    private PlayerViewModel ViewModel => _vm ??= App.Window.ViewModel.Player;

    public PlayerPage()
    {
        InitializeComponent();
        _seekPressedHandler = OnSeekPressed;
        _seekReleasedHandler = OnSeekReleased;
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTimer;

        _osdTimer = DispatcherQueue.CreateTimer();
        _osdTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _osdTimer.IsRepeating = false;
        _osdTimer.Tick += (_, _) => HideOsd();

        _danmakuSaveTimer = DispatcherQueue.CreateTimer();
        _danmakuSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _danmakuSaveTimer.IsRepeating = false;
        _danmakuSaveTimer.Tick += async (_, _) => await PersistDanmakuPreferencesAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.DanmakuChanged -= OnDanmakuChanged;
        ViewModel.DanmakuChanged += OnDanmakuChanged;
        ViewModel.DanmakuCleared -= OnDanmakuCleared;
        ViewModel.DanmakuCleared += OnDanmakuCleared;
        ViewModel.SnapshotUpdated -= OnSnapshotUpdated;
        ViewModel.SnapshotUpdated += OnSnapshotUpdated;
        ViewModel.SeekPerformed -= OnSeekPerformed;
        ViewModel.SeekPerformed += OnSeekPerformed;
        ViewModel.PlaybackStateChanged -= OnPlaybackStateChanged;
        ViewModel.PlaybackStateChanged += OnPlaybackStateChanged;
        ViewModel.SourceRecoveryOffered -= OnSourceRecoveryOffered;
        ViewModel.SourceRecoveryOffered += OnSourceRecoveryOffered;

        if (!_seekHandlersWired)
        {
            SeekSlider.AddHandler(UIElement.PointerPressedEvent, _seekPressedHandler, handledEventsToo: true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, _seekReleasedHandler, handledEventsToo: true);
            SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, _seekReleasedHandler, handledEventsToo: true);
            _seekHandlersWired = true;
        }

        Diagnostics.Log($"Player page loaded, output ready={ViewModel.Service.IsVideoOutputReady}");
        if (RateFlyout.Items.Count == 0) BuildFlyouts();
        if (ViewModel.Service.IsVideoOutputReady)
        {
            VideoView.MediaPlayer = ViewModel.Service.MediaPlayer;
            ViewModel.NotifyVideoReady();
        }
        VideoView.Initialized -= OnVideoViewInitialized;
        VideoView.Initialized += OnVideoViewInitialized;
        Focus(FocusState.Programmatic);
        _ = RefreshDanmakuToggleAsync();
        ShowControls();
    }

    private async void OnVideoViewInitialized(object? sender, LibVLCSharp.Platforms.Windows.InitializedEventArgs e)
    {
        Diagnostics.Log("VideoView initialization callback");
        try
        {
            VideoView.MediaPlayer = null;
            await ViewModel.Service.EnsureInitializedAsync(e.SwapChainOptions);
            if (!IsLoaded) return;
            VideoView.MediaPlayer = ViewModel.Service.MediaPlayer;
            ViewModel.Service.SetVolume(ViewModel.Volume);
            ViewModel.NotifyVideoReady();
            Diagnostics.Log("VideoView output attached");
            if (App.PlayerOnly)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1800).ConfigureAwait(false);
                    TrimWorkingSet();
                });
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VideoView initialization failed: " + ex);
            ViewModel.StatusMessage = "播放器初始化失败：" + ex.Message;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _bilibiliLoginDialog?.Hide();
        if (_cloudLoginDialog is not null)
        {
            App.Services.CloudLogin.Succeeded -= OnCloudLoginSucceeded;
            App.Services.CloudLogin.Close();
            _cloudLoginDialog.Hide();
            _cloudLoginDialog = null;
        }
        PlaylistFlyout.Hide();
        _danmakuFlyout?.Hide();
        _subtitleFlyout?.Hide();
        _timingFlyout?.Hide();
        _hideTimer.Stop();
        _osdTimer.Stop();
        if (_danmakuSaveTimer.IsRunning)
        {
            _danmakuSaveTimer.Stop();
            _ = PersistDanmakuPreferencesAsync();
        }
        CancelDanmakuLoad();
        _danmakuAddress = null;
        _localDanmakuOverride = null;
        _manualDanDanPlayEpisodeId = null;
        _rawComments = [];
        DanmakuLayer.ReleasePool();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.DanmakuChanged -= OnDanmakuChanged;
        ViewModel.DanmakuCleared -= OnDanmakuCleared;
        ViewModel.SnapshotUpdated -= OnSnapshotUpdated;
        ViewModel.SeekPerformed -= OnSeekPerformed;
        ViewModel.PlaybackStateChanged -= OnPlaybackStateChanged;
        ViewModel.SourceRecoveryOffered -= OnSourceRecoveryOffered;
        if (_seekHandlersWired)
        {
            SeekSlider.RemoveHandler(UIElement.PointerPressedEvent, _seekPressedHandler);
            SeekSlider.RemoveHandler(UIElement.PointerReleasedEvent, _seekReleasedHandler);
            SeekSlider.RemoveHandler(UIElement.PointerCaptureLostEvent, _seekReleasedHandler);
            _seekHandlersWired = false;
        }
        _seekDragging = false;
        _sourceDialog?.Hide();
        _sourceDialog = null;
        _onlineSubtitleDialog?.Hide();
        _onlineSubtitleDialog = null;
        _danmakuSearchDialog?.Hide();
        _danmakuSearchDialog = null;
        ShowArrowCursor();
        VideoView.Initialized -= OnVideoViewInitialized;
        VideoView.MediaPlayer = null;
    }

    private void BuildFlyouts()
    {
        foreach (var rate in Rates)
        {
            var item = new MenuFlyoutItem { Text = $"{rate:0.##}x", Tag = rate };
            item.Click += (_, _) =>
            {
                ViewModel.Rate = rate;
                ShowOsd($"倍速 {rate:0.##}x");
            };
            RateFlyout.Items.Add(item);
        }
    }

    private void OnSnapshotUpdated(object? sender, PlayerSnapshot snapshot)
    {
        var ts = snapshot.CapturedAtTimestamp != 0 ? snapshot.CapturedAtTimestamp : Stopwatch.GetTimestamp();
        DanmakuLayer.SyncPlayback(snapshot.TimeMs / 1000d, ts, snapshot.Rate, snapshot.IsPlaying, ViewModel.IsBuffering);
        if (ViewModel.ShowMediaInfo)
        {
            var danmakuText = _danmaku.Enabled && DanmakuLayer.LoadedCount > 0
                ? $"弹幕 {DanmakuLayer.ActiveCount}/{DanmakuLayer.LoadedCount}"
                : "弹幕关闭";
            MediaInfoExtra.Text = $"倍速 {snapshot.Rate:0.##}x · 比例 {ViewModel.AspectModeName} · {danmakuText}";
        }
    }

    private void OnSeekPerformed(object? sender, long targetMs)
    {
        DanmakuLayer.NotifySeek(targetMs / 1000d);
    }

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        DanmakuLayer.SetPlayingImmediate(isPlaying);
        UpdateCenterPlayVisibility();
        if (!isPlaying)
        {
            _hideTimer.Stop();
            if (!_controlsVisible)
            {
                _controlsVisible = true;
                SetChromeVisible(true);
            }
        }
        else if (_controlsVisible)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePauseCommand.Execute(null);
        ShowControls();
    }

    private void ShowOsd(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        OsdText.Text = message;
        OsdBadge.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Start();
        MotionService.SetFloatingVisible(OsdBadge, true, offsetY: -8, hiddenScale: 0.92f);
    }

    private void HideOsd()
    {
        MotionService.SetFloatingVisible(OsdBadge, false, offsetY: -8, hiddenScale: 0.92f);
    }

    private void OnQualityMenuOpening(object? sender, object args)
    {
        _hideTimer.Stop();
        QualityFlyout.Items.Clear();
        foreach (var quality in ViewModel.QualityOptions)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = quality.Name, Tag = quality, IsChecked = quality == ViewModel.SelectedQuality,
                IsEnabled = ViewModel.CanSelectQuality(quality)
            };
            item.Click += async (_, _) => await ViewModel.ChangeQualityAsync(quality);
            QualityFlyout.Items.Add(item);
        }
        if (ViewModel.IsBilibiliPlayback)
        {
            QualityFlyout.Items.Add(new MenuFlyoutSeparator());
            var account = new MenuFlyoutItem { Text = "B 站账号" };
            account.Click += async (_, _) => await ShowBilibiliLoginAsync();
            QualityFlyout.Items.Add(account);
        }
    }

    private async Task ShowBilibiliLoginAsync()
    {
        if (XamlRoot is null || _bilibiliLoginDialog is not null) return;
        _hideTimer.Stop();
        using var dialog = new BilibiliLoginDialog(App.Services.BilibiliAccount, XamlRoot);
        _bilibiliLoginDialog = dialog;
        try
        {
            await dialog.ShowAsync();
            if (dialog.AccountChanged && IsLoaded) await ViewModel.RefreshBilibiliQualityAsync();
        }
        catch (Exception) { ViewModel.QualityMessage = "B 站账号面板无法打开，请重试。"; }
        finally
        {
            _bilibiliLoginDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.RequiredLoginProvider) && ViewModel.RequiredLoginProvider is not null && IsLoaded)
            _ = ShowCloudLoginAsync();
        if (e.PropertyName is nameof(PlayerViewModel.IsRealtimeSubtitleActive) or nameof(PlayerViewModel.SubtitleDelayMs) or nameof(PlayerViewModel.SubtitleSpeed))
            UpdateSubtitleIndicator();
        if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.IsBuffering)
            or nameof(PlayerViewModel.HasMedia) or nameof(PlayerViewModel.ShowStatusPanel))
        {
            UpdateCenterPlayVisibility();
            if (e.PropertyName == nameof(PlayerViewModel.ShowStatusPanel) && ViewModel.ShowStatusPanel && IsLoaded)
                ShowControls();
        }
    }

    private void OnPlaylistOpening(object? sender, object args)
    {
        _hideTimer.Stop();
        PlaylistEpisodes.MaxHeight = Math.Clamp(ActualHeight - 175, 100, 400);
        if (ViewModel.SelectedEpisode is { } episode) PlaylistEpisodes.ScrollIntoView(episode);
    }

    private void OnPlaylistClosed(object? sender, object args)
    {
        if (IsLoaded) ShowControls();
    }

    private void OnPlaybackOptionsOpening(object? sender, object e)
    {
        _hideTimer.Stop();
        var player = ViewModel.Service.LastSnapshot;
        BuildTracks(AudioTracks, ViewModel.AudioTracks, player.AudioTrack, id => _ = ViewModel.SelectTrackAsync(false, id));
        var selectedCloud = ViewModel.SelectedCloudSubtitle;
        var cloudTracks = ViewModel.CloudSubtitleTracks;
        BuildTracks(SubtitleTracks, ViewModel.SubtitleTracks, player.SubtitleTrack, id => _ = ViewModel.SelectTrackAsync(true, id),
            selectedCloud is not null, cloudTracks.Count > 0);
        if (cloudTracks.Count > 0)
        {
            SubtitleTracks.Items.Add(new MenuFlyoutSeparator());
            foreach (var track in cloudTracks)
            {
                var item = new ToggleMenuFlyoutItem { Text = track.Name, IsChecked = track == selectedCloud };
                item.Click += async (_, _) => await ViewModel.SelectCloudSubtitleAsync(track);
                SubtitleTracks.Items.Add(item);
            }
        }

        // Aspect ratio menu
        AspectRatioMenu.Items.Clear();
        for (var i = 0; i < PlayerViewModel.AspectModeNames.Length; i++)
        {
            var index = i;
            var modeItem = new ToggleMenuFlyoutItem
            {
                Text = PlayerViewModel.AspectModeNames[i],
                IsChecked = ViewModel.AspectModeIndex == i
            };
            modeItem.Click += (_, _) =>
            {
                ViewModel.SetAspectMode(index);
                ShowOsd($"画面比例：{ViewModel.AspectModeName}");
            };
            AspectRatioMenu.Items.Add(modeItem);
        }

        // Subtitle & Audio sync delay menu
        SyncDelayMenu.Items.Clear();
        var subEarlier = new MenuFlyoutItem { Text = $"字幕提前 0.5 秒（当前 {ViewModel.SubtitleDelayMs / 1000.0:+0.#;-0.#;0}s）" };
        subEarlier.Click += (_, _) => { ViewModel.AdjustSubtitleDelay(-500); ShowOsd($"字幕延迟 {ViewModel.SubtitleDelayMs / 1000.0:+0.#;-0.#;0}s"); };
        var subLater = new MenuFlyoutItem { Text = "字幕延后 0.5 秒" };
        subLater.Click += (_, _) => { ViewModel.AdjustSubtitleDelay(500); ShowOsd($"字幕延迟 {ViewModel.SubtitleDelayMs / 1000.0:+0.#;-0.#;0}s"); };
        var subReset = new MenuFlyoutItem { Text = "重置字幕延迟", IsEnabled = ViewModel.SubtitleDelayMs != 0 };
        subReset.Click += (_, _) => { ViewModel.ResetSubtitleDelay(); ShowOsd("字幕延迟已重置"); };
        var audEarlier = new MenuFlyoutItem { Text = $"音频提前 0.1 秒（当前 {ViewModel.AudioDelayMs / 1000.0:+0.#;-0.#;0}s）" };
        audEarlier.Click += (_, _) => { ViewModel.AdjustAudioDelay(-100); ShowOsd($"音频延迟 {ViewModel.AudioDelayMs / 1000.0:+0.#;-0.#;0}s"); };
        var audLater = new MenuFlyoutItem { Text = "音频延后 0.1 秒" };
        audLater.Click += (_, _) => { ViewModel.AdjustAudioDelay(100); ShowOsd($"音频延迟 {ViewModel.AudioDelayMs / 1000.0:+0.#;-0.#;0}s"); };
        var audReset = new MenuFlyoutItem { Text = "重置音频延迟", IsEnabled = ViewModel.AudioDelayMs != 0 };
        audReset.Click += (_, _) => { ViewModel.ResetAudioDelay(); ShowOsd("音频延迟已重置"); };
        SyncDelayMenu.Items.Add(subEarlier);
        SyncDelayMenu.Items.Add(subLater);
        SyncDelayMenu.Items.Add(subReset);
        SyncDelayMenu.Items.Add(new MenuFlyoutSeparator());
        SyncDelayMenu.Items.Add(audEarlier);
        SyncDelayMenu.Items.Add(audLater);
        SyncDelayMenu.Items.Add(audReset);

        LoopEpisodeItem.IsChecked = ViewModel.LoopCurrentEpisode;
        MediaInfoToggleItem.IsChecked = ViewModel.ShowMediaInfo;
        RealtimeSubtitleToggleItem.IsChecked = ViewModel.IsRealtimeSubtitleActive;
    }

    private void UpdateSubtitleIndicator()
    {
        if (SubtitleActiveIndicator is null) return;
        var active = ViewModel.IsRealtimeSubtitleActive || ViewModel.SubtitleDelayMs != 0 || Math.Abs(ViewModel.SubtitleSpeed - 1.0) > 0.001;
        SubtitleActiveIndicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSubtitleSettingsClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        _ = OpenSubtitleSettingsAsync(sender as FrameworkElement ?? SubtitleSettingsButton);
    }

    private async void OnAiGenerateSubtitleClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        if (!await EnsureBailianConfiguredAsync()) return;
        ShowOsd("开始 AI 字幕生成…");
        await ViewModel.GenerateAiSubtitleAsync(translateAfter: false);
    }

    private async void OnAiGenerateAndTranslateClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        if (!await EnsureBailianConfiguredAsync()) return;
        ShowOsd("开始 AI 生成并翻译字幕…");
        await ViewModel.GenerateAiSubtitleAsync(translateAfter: true);
    }

    private async void OnAiTranslateSubtitleClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        if (!await EnsureBailianConfiguredAsync()) return;
        string? filePath = ViewModel.ActiveExternalSubtitlePath ?? ViewModel.LastGeneratedAiSubtitlePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
            foreach (var ext in new[] { ".srt", ".vtt", ".ass", ".ssa" }) picker.FileTypeFilter.Add(ext);
            var picked = await picker.PickSingleFileAsync();
            if (picked is null) return;
            filePath = picked.Path;
        }
        ShowOsd("开始 AI 字幕翻译…");
        await ViewModel.TranslateSubtitleAsync(filePath);
    }

    private async void OnRealtimeSubtitleToggleClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        if (!ViewModel.IsRealtimeSubtitleActive && !await EnsureBailianConfiguredAsync()) return;
        await ViewModel.ToggleRealtimeSubtitleAsync();
        ShowOsd(ViewModel.IsRealtimeSubtitleActive ? "实时字幕已开启" : "实时字幕已关闭");
    }

    private async Task<bool> EnsureBailianConfiguredAsync()
    {
        var config = await App.Services.BailianSubtitle.GetConfigAsync();
        if (config.HasApiKey) return true;
        if (XamlRoot is null) return false;
        _hideTimer.Stop();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "需要配置阿里云百炼 API Key",
            Content = "AI 自动生成字幕、字幕翻译和实时字幕依赖阿里云百炼平台密钥。\n点击下方按钮可前往设置页面一键跳转百炼平台生成并填入密钥。",
            PrimaryButtonText = "前往设置配置",
            SecondaryButtonText = "直接打开百炼平台",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (!App.PlayerOnly) App.Window.OpenAiSettings();
                else ViewModel.QualityMessage = "直达播放模式请打开主应用“设置 → 弹幕与 AI”配置阿里云百炼 API Key。";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(AiSubtitleModels.BailianApiKeyConsoleUrl));
            }
        }
        finally { if (IsLoaded) ShowControls(); }
        return false;
    }

    private async Task OpenSubtitleSettingsAsync(FrameworkElement target)
    {
        if (_subtitleFlyout is not null)
        {
            _subtitleFlyout.Hide();
            return;
        }
        _hideTimer.Stop();
        try
        {
            var aiConfig = await App.Services.BailianSubtitle.GetConfigAsync();
            if (!IsLoaded) return;

            var root = new StackPanel { Width = 356, Spacing = 12 };

            // --- 1. 字幕时间轴微调（前移 / 后移 / 字幕速度）---
            root.Children.Add(new TextBlock { Text = "字幕时间轴调节", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });

            var delayLabel = new TextBlock { Text = FormatDelayLabel(ViewModel.SubtitleDelayMs) };
            var delayBox = new NumberBox
            {
                Header = "字幕偏移（秒，负数前移 / 正数后移）",
                Minimum = -60, Maximum = 60, SmallChange = 0.1, LargeChange = 1.0,
                Value = Math.Round(ViewModel.SubtitleDelayMs / 1000.0, 2),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };

            Button MakeShiftBtn(string text, long deltaMs)
            {
                var btn = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, MinHeight = 32 };
                btn.Click += (_, _) =>
                {
                    ViewModel.AdjustSubtitleDelay(deltaMs);
                    delayBox.Value = Math.Round(ViewModel.SubtitleDelayMs / 1000.0, 2);
                    delayLabel.Text = FormatDelayLabel(ViewModel.SubtitleDelayMs);
                    ShowOsd($"字幕{(ViewModel.SubtitleDelayMs < 0 ? "前移" : "后移")} {Math.Abs(ViewModel.SubtitleDelayMs) / 1000.0:0.#}s");
                };
                return btn;
            }

            var shiftRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    MakeShiftBtn("-2s", -2000),
                    MakeShiftBtn("-0.5s", -500),
                    MakeShiftBtn("-0.1s", -100),
                    MakeShiftBtn("+0.1s", 100),
                    MakeShiftBtn("+0.5s", 500),
                    MakeShiftBtn("+2s", 2000)
                }
            };

            delayBox.ValueChanged += (_, args) =>
            {
                if (!double.IsFinite(args.NewValue)) return;
                var ms = (long)Math.Round(args.NewValue * 1000.0);
                if (ms == ViewModel.SubtitleDelayMs) return;
                ViewModel.SetSubtitleDelay(ms);
                delayLabel.Text = FormatDelayLabel(ViewModel.SubtitleDelayMs);
            };

            // 字幕速度 (0.80x ~ 1.25x)
            var speedLabel = new TextBlock { Text = $"字幕速度：{ViewModel.SubtitleSpeed:0.00}x（>1 加快前移，<1 放慢后移）" };
            var speedSlider = new Slider
            {
                Minimum = 80, Maximum = 125, StepFrequency = 1,
                Value = Math.Round(ViewModel.SubtitleSpeed * 100)
            };

            Button MakeSpeedPreset(string text, double spd)
            {
                var btn = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, MinHeight = 32 };
                btn.Click += async (_, _) =>
                {
                    speedSlider.Value = Math.Round(spd * 100);
                    await ViewModel.SetSubtitleSpeedAsync(spd);
                    speedLabel.Text = $"字幕速度：{ViewModel.SubtitleSpeed:0.00}x";
                    ShowOsd($"字幕速度 {ViewModel.SubtitleSpeed:0.00}x");
                };
                return btn;
            }

            var speedPresets = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    MakeSpeedPreset("0.95x", 0.95),
                    MakeSpeedPreset("0.96x", 0.96),
                    MakeSpeedPreset("1.00x", 1.00),
                    MakeSpeedPreset("1.04x", 1.04),
                    MakeSpeedPreset("1.05x", 1.05)
                }
            };

            speedSlider.ValueChanged += (_, args) =>
            {
                var spd = Math.Round(args.NewValue) / 100.0;
                speedLabel.Text = $"字幕速度：{spd:0.00}x";
                ViewModel.SetSubtitleSpeedLive(spd);
            };

            var resetTimingBtn = new Button { Content = "重置偏移与速度", HorizontalAlignment = HorizontalAlignment.Left };
            resetTimingBtn.Click += async (_, _) =>
            {
                await ViewModel.ResetSubtitleTimingAsync();
                delayBox.Value = 0;
                speedSlider.Value = 100;
                delayLabel.Text = FormatDelayLabel(0);
                speedLabel.Text = "字幕速度：1.00x";
                ShowOsd("字幕时间轴已重置");
            };

            root.Children.Add(delayLabel);
            root.Children.Add(shiftRow);
            root.Children.Add(delayBox);
            root.Children.Add(speedLabel);
            root.Children.Add(speedSlider);
            root.Children.Add(speedPresets);
            root.Children.Add(resetTimingBtn);
            root.Children.Add(new Border { Height = 1, Background = Application.Current.Resources["DividerBrush"] as Brush });

            // --- 2. AI 字幕与翻译（阿里云百炼）---
            root.Children.Add(new TextBlock { Text = "AI 智能字幕与翻译（阿里云百炼）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });
            var langName = AiSubtitleModels.TargetLanguages.FirstOrDefault(l => l.ApiName == aiConfig.TargetLanguage)?.DisplayName ?? aiConfig.TargetLanguage;
            var aiSummary = new TextBlock
            {
                Text = aiConfig.HasApiKey
                    ? $"生成模型：{aiConfig.AsrModel} · 翻译模型：{aiConfig.MtModel}（目标：{langName}）"
                    : "尚未配置阿里云百炼 API Key，点击下方按钮前往配置。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                FontSize = 13
            };
            root.Children.Add(aiSummary);

            var genBtn = new Button
            {
                Content = "AI 生成字幕（自动对齐时间轴）",
                Style = Application.Current.Resources["PrimaryButtonStyle"] as Style,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = !ViewModel.IsAiSubtitleBusy
            };
            var genTransBtn = new Button
            {
                Content = $"AI 生成并翻译为{langName}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = !ViewModel.IsAiSubtitleBusy
            };
            var translateBtn = new Button
            {
                Content = ViewModel.ActiveExternalSubtitlePath is not null ? $"AI 翻译当前字幕 → {langName}" : $"选择本地字幕 AI 翻译 → {langName}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = !ViewModel.IsAiSubtitleBusy
            };
            var realtimeBtn = new Button
            {
                Content = ViewModel.IsRealtimeSubtitleActive ? "关闭实时字幕 (Qwen3-ASR)" : "开启实时字幕 (Qwen3-ASR)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var fileRow = new Grid { ColumnSpacing = 8 };
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var loadLocalBtn = new Button { Content = "加载本地字幕", HorizontalAlignment = HorizontalAlignment.Stretch };
            var matchOnlineBtn = new Button { Content = "匹配在线字幕", HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(loadLocalBtn, 0);
            Grid.SetColumn(matchOnlineBtn, 1);
            fileRow.Children.Add(loadLocalBtn);
            fileRow.Children.Add(matchOnlineBtn);

            var utilRow = new Grid { ColumnSpacing = 8 };
            utilRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            utilRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var exportBtn = new Button { Content = "导出字幕 (.srt)", HorizontalAlignment = HorizontalAlignment.Stretch };
            var openSettingsBtn = new Button { Content = aiConfig.HasApiKey ? "AI 模型设置" : "配置百炼密钥", HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(exportBtn, 0);
            Grid.SetColumn(openSettingsBtn, 1);
            utilRow.Children.Add(exportBtn);
            utilRow.Children.Add(openSettingsBtn);

            root.Children.Add(genBtn);
            root.Children.Add(genTransBtn);
            root.Children.Add(translateBtn);
            root.Children.Add(realtimeBtn);
            root.Children.Add(fileRow);
            root.Children.Add(utilRow);

            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
                Content = new ScrollViewer { MaxHeight = Math.Max(220, ActualHeight - 116), Padding = new Thickness(0, 0, 8, 0), Content = root }
            };
            flyout.Closed += (_, _) => { _subtitleFlyout = null; if (IsLoaded) ShowControls(); };

            genBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                if (!await EnsureBailianConfiguredAsync()) return;
                ShowOsd("开始 AI 字幕生成…");
                await ViewModel.GenerateAiSubtitleAsync(translateAfter: false);
            };
            genTransBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                if (!await EnsureBailianConfiguredAsync()) return;
                ShowOsd("开始 AI 生成并翻译字幕…");
                await ViewModel.GenerateAiSubtitleAsync(translateAfter: true);
            };
            translateBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                OnAiTranslateSubtitleClick(translateBtn, new RoutedEventArgs());
            };
            realtimeBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                if (!ViewModel.IsRealtimeSubtitleActive && !await EnsureBailianConfiguredAsync()) return;
                await ViewModel.ToggleRealtimeSubtitleAsync();
                ShowOsd(ViewModel.IsRealtimeSubtitleActive ? "实时字幕已开启" : "实时字幕已关闭");
            };
            loadLocalBtn.Click += (_, _) =>
            {
                flyout.Hide();
                OnLoadSubtitleClick(loadLocalBtn, new RoutedEventArgs());
            };
            matchOnlineBtn.Click += (_, _) =>
            {
                flyout.Hide();
                OnMatchSubtitleClick(matchOnlineBtn, new RoutedEventArgs());
            };
            exportBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                await ExportSubtitleInteractiveAsync();
            };
            openSettingsBtn.Click += (_, _) =>
            {
                flyout.Hide();
                if (!App.PlayerOnly) App.Window.OpenAiSettings();
                else _ = Windows.System.Launcher.LaunchUriAsync(new Uri(AiSubtitleModels.BailianApiKeyConsoleUrl));
            };

            _subtitleFlyout = flyout;
            if (IsLoaded) flyout.ShowAt(target);
        }
        catch (Exception ex) { ViewModel.QualityMessage = "字幕面板打开失败：" + ex.Message; }
    }

    private async Task ExportSubtitleInteractiveAsync()
    {
        try
        {
            var safeTitle = string.Concat(ViewModel.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (safeTitle.Length > 60) safeTitle = safeTitle[..60];
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.Window.AppWindow.Id)
            {
                SuggestedFileName = string.IsNullOrWhiteSpace(safeTitle) ? "ai-subtitle" : safeTitle,
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary
            };
            picker.FileTypeChoices.Add("SRT 字幕文件", [".srt"]);
            var dest = await picker.PickSaveFileAsync();
            if (dest is null) return;
            if (await ViewModel.ExportSubtitleToAsync(dest.Path))
            {
                ShowOsd("字幕已导出");
                ViewModel.QualityMessage = $"字幕已导出至：{dest.Path}";
            }
            else
            {
                ViewModel.QualityMessage = "当前没有可导出的 AI 或外部字幕文件。";
            }
        }
        catch (Exception ex) { ViewModel.QualityMessage = "字幕导出失败：" + ex.Message; }
    }

    private static string FormatDelayLabel(long delayMs)
    {
        if (delayMs == 0) return "当前偏移：0.0s（与原视频同步）";
        var dir = delayMs < 0 ? "前移" : "后移";
        return $"当前偏移：{dir} {Math.Abs(delayMs) / 1000.0:0.0}s（{delayMs / 1000.0:+0.0;-0.0;0.0}s）";
    }

    private void OnLoopEpisodeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.LoopCurrentEpisode = LoopEpisodeItem.IsChecked;
        ShowOsd(ViewModel.LoopCurrentEpisode ? "已开启单集循环" : "已关闭单集循环");
    }

    private async void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ViewModel.TakeSnapshotAsync();
            if (path is not null)
            {
                ShowOsd("截图已保存");
                ViewModel.QualityMessage = $"截图已保存至：{path}";
            }
            else ShowOsd("当前无法截图");
        }
        catch (Exception ex) { ViewModel.QualityMessage = "截图失败：" + ex.Message; }
    }

    private void OnMediaInfoToggleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowMediaInfo = MediaInfoToggleItem.IsChecked;
    }

    private static void BuildTracks(MenuFlyoutSubItem menu, IEnumerable<PlaybackTrack> tracks, int selected, Action<int> select,
        bool externalSelected = false, bool hasExternalTracks = false)
    {
        menu.Items.Clear();
        var available = tracks.Where(track => track.Id >= 0).ToArray();
        var off = new ToggleMenuFlyoutItem { Text = "关闭", IsChecked = selected < 0 && !externalSelected, IsEnabled = available.Length > 0 || hasExternalTracks };
        off.Click += (_, _) => select(-1);
        menu.Items.Add(off);
        for (var index = 0; index < available.Length; index++)
        {
            var track = available[index];
            var item = new ToggleMenuFlyoutItem { Text = string.IsNullOrWhiteSpace(track.Name) ? $"轨道 {index + 1}" : track.Name, IsChecked = track.Id == selected && !externalSelected };
            item.Click += (_, _) => select(track.Id);
            menu.Items.Add(item);
        }
    }

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
            foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !await ViewModel.AddSubtitleAsync(new Uri(file.Path)))
                ViewModel.QualityMessage = "无法加载字幕。";
            else if (file is not null) ShowOsd("已加载本地字幕");
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private async void OnMatchSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_onlineSubtitleDialog is not null || XamlRoot is null) return;
        _hideTimer.Stop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var tracks = await ViewModel.GetOnlineSubtitlesAsync(timeout.Token);
            if (!IsLoaded || XamlRoot is null) return;
            if (tracks.Count == 0) { ViewModel.QualityMessage = "匹配节目没有提供在线字幕，可使用本地字幕。"; return; }
            var choices = new ComboBox
            {
                ItemsSource = tracks, DisplayMemberPath = "Name", SelectedIndex = 0,
                Header = "字幕语言", HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "在线字幕", Content = choices,
                PrimaryButtonText = "加载字幕", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
            };
            _onlineSubtitleDialog = dialog;
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is OnlineSubtitleTrack track)
                    await ViewModel.SelectOnlineSubtitleAsync(track);
            }
            finally { _onlineSubtitleDialog = null; }
        }
        catch (OperationCanceledException) { ViewModel.QualityMessage = "在线字幕匹配超时，请重试。"; }
        catch (Exception ex) { ViewModel.QualityMessage = "在线字幕匹配失败：" + ex.Message; }
        finally { if (IsLoaded) ShowControls(); }
    }

    private void OnDanmakuRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _ = OpenDanmakuSettingsAsync(DanmakuToggleButton);
    }

    private void OnDanmakuSettingsClick(object sender, RoutedEventArgs e)
    {
        PlaybackOptionsFlyout.Hide();
        _ = OpenDanmakuSettingsAsync(PlaybackOptionsButton);
    }

    private async Task OpenDanmakuSettingsAsync(FrameworkElement target)
    {
        if (_danmakuFlyout is not null)
        {
            _danmakuFlyout.Hide();
            return;
        }
        _hideTimer.Stop();
        try
        {
            if (!_danmakuPrefsLoaded) await ReadDanmakuPreferencesAsync();
            if (!IsLoaded) return;

            var enabled = new ToggleSwitch { Header = "显示弹幕", IsOn = _danmaku.Enabled };

            // Opacity slider
            var opacityHeader = new TextBlock { Text = $"不透明度：{(int)Math.Round(_danmaku.Opacity * 100)}%" };
            var opacitySlider = new Slider { Minimum = 20, Maximum = 100, StepFrequency = 5, Value = Math.Round(_danmaku.Opacity * 100) };

            // Font size slider
            var sizeHeader = new TextBlock { Text = $"字号：{_danmaku.Size} px" };
            var sizeSlider = new Slider { Minimum = 14, Maximum = 36, StepFrequency = 2, Value = _danmaku.Size };

            // Speed
            var speedIndex = Array.IndexOf(DanmakuSpeeds, DanmakuSpeeds.MinBy(v => Math.Abs(v - _danmaku.Speed)));
            var speed = new AnchoredChoice
            {
                Header = "滚动速度", Options = "很慢 (0.6x)|较慢 (0.8x)|标准 (1.0x)|较快 (1.25x)|很快 (1.5x)",
                SelectedIndex = Math.Clamp(speedIndex, 0, DanmakuSpeeds.Length - 1),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Display area
            var areaIndex = Array.IndexOf(DanmakuAreas, DanmakuAreas.MinBy(v => Math.Abs(v - _danmaku.DisplayArea)));
            var area = new AnchoredChoice
            {
                Header = "显示区域", Options = "1/4 屏|半屏|3/4 屏|全屏",
                SelectedIndex = Math.Clamp(areaIndex, 0, DanmakuAreas.Length - 1),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Density
            var density = new AnchoredChoice
            {
                Header = "同屏密度", Options = "稀疏|标准|密集|不限",
                SelectedIndex = _danmaku.Density,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Color & style
            var color = new AnchoredChoice
            {
                Header = "颜色模式", Options = "跟随来源|统一白色|暖金色|冰蓝色",
                SelectedIndex = _danmaku.Color,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var outline = new CheckBox { Content = "文字描边阴影", IsChecked = _danmaku.TextOutline };
            var bold = new CheckBox { Content = "粗体显示", IsChecked = _danmaku.Bold };
            var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { outline, bold } };

            // Type filters
            var typeLabel = new TextBlock { Text = "弹幕类型", Margin = new Thickness(0, 4, 0, 0) };
            var showScroll = new CheckBox { Content = "滚动", IsChecked = _danmaku.ShowScroll, MinWidth = 0 };
            var showTop = new CheckBox { Content = "顶部", IsChecked = _danmaku.ShowTop, MinWidth = 0 };
            var showBottom = new CheckBox { Content = "底部", IsChecked = _danmaku.ShowBottom, MinWidth = 0 };
            var showColor = new CheckBox { Content = "彩色", IsChecked = _danmaku.ShowColor, MinWidth = 0 };
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { showScroll, showTop, showBottom, showColor } };

            // Time offset
            var timeOffset = new NumberBox
            {
                Header = "时间轴偏移（秒，正数提前 / 负数延后）",
                Minimum = -15, Maximum = 15, SmallChange = 0.5, LargeChange = 2.0,
                Value = _danmaku.TimeOffsetSeconds,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };

            // Source & matching
            var source = new AnchoredChoice
            {
                Header = "弹幕来源", Options = "来源自带 / 本地文件|弹弹play 开放弹幕库|哔哩哔哩在线匹配",
                SelectedIndex = _danmaku.Source switch { "dandanplay" => 1, "bilibili" => 2, _ => 0 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var autoMatch = new ToggleSwitch { Header = "换集后自动匹配对应集数", IsOn = _danmaku.AutoMatch };
            var filter = new ToggleSwitch { Header = "基础广告屏蔽规则", IsOn = _danmaku.BasicFilter };
            var words = new TextBox { Header = "自定义屏蔽词", Text = _danmaku.BlockedWords, PlaceholderText = "空格或逗号分隔", MaxLength = 400 };

            void ApplyLiveVisual(bool refilter = false)
            {
                _danmaku = _danmaku with
                {
                    Enabled = enabled.IsOn,
                    Opacity = opacitySlider.Value / 100.0,
                    Size = (int)Math.Round(sizeSlider.Value),
                    Speed = DanmakuSpeeds[Math.Clamp(speed.SelectedIndex, 0, DanmakuSpeeds.Length - 1)],
                    DisplayArea = DanmakuAreas[Math.Clamp(area.SelectedIndex, 0, DanmakuAreas.Length - 1)],
                    Density = Math.Clamp(density.SelectedIndex, 0, 3),
                    Color = Math.Clamp(color.SelectedIndex, 0, 3),
                    TextOutline = outline.IsChecked == true,
                    Bold = bold.IsChecked == true,
                    ShowScroll = showScroll.IsChecked == true,
                    ShowTop = showTop.IsChecked == true,
                    ShowBottom = showBottom.IsChecked == true,
                    ShowColor = showColor.IsChecked == true,
                    TimeOffsetSeconds = double.IsFinite(timeOffset.Value) ? Math.Clamp(timeOffset.Value, -15, 15) : 0,
                    BasicFilter = filter.IsOn,
                    BlockedWords = words.Text ?? string.Empty,
                    Source = source.SelectedIndex switch { 1 => "dandanplay", 2 => "bilibili", _ => "origin" },
                    AutoMatch = autoMatch.IsOn
                };
                opacityHeader.Text = $"不透明度：{(int)Math.Round(_danmaku.Opacity * 100)}%";
                sizeHeader.Text = $"字号：{_danmaku.Size} px";
                UpdateDanmakuToggle();
                DanmakuLayer.SetPreferences(_danmaku);
                if (refilter && _rawComments.Length > 0) ApplyFilteredComments();
                QueueSaveDanmakuPreferences();
            }

            var advanced = new StackPanel
            {
                Spacing = 10,
                Children = { opacityHeader, opacitySlider, sizeHeader, sizeSlider, speed, area, density, color, styleRow, typeLabel, typeRow, timeOffset, source, autoMatch, filter, words },
                Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed
            };

            enabled.Toggled += (_, _) =>
            {
                advanced.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
                ApplyLiveVisual();
                if (enabled.IsOn && _rawComments.Length == 0) _ = ReloadDanmakuAsync(false);
            };
            opacitySlider.ValueChanged += (_, _) => ApplyLiveVisual();
            sizeSlider.ValueChanged += (_, _) => ApplyLiveVisual();
            speed.SelectionChanged += (_, _) => ApplyLiveVisual();
            area.SelectionChanged += (_, _) => ApplyLiveVisual();
            density.SelectionChanged += (_, _) => ApplyLiveVisual();
            color.SelectionChanged += (_, _) => ApplyLiveVisual();
            outline.Click += (_, _) => ApplyLiveVisual();
            bold.Click += (_, _) => ApplyLiveVisual();
            showScroll.Click += (_, _) => ApplyLiveVisual();
            showTop.Click += (_, _) => ApplyLiveVisual();
            showBottom.Click += (_, _) => ApplyLiveVisual();
            showColor.Click += (_, _) => ApplyLiveVisual();
            timeOffset.ValueChanged += (_, _) => ApplyLiveVisual();
            filter.Toggled += (_, _) => ApplyLiveVisual(refilter: true);
            autoMatch.Toggled += (_, _) => ApplyLiveVisual();
            source.SelectionChanged += (_, _) =>
            {
                ApplyLiveVisual();
                if (enabled.IsOn) _ = ReloadDanmakuAsync(false);
            };

            var loadLocal = new Button { Content = "加载本地弹幕 (.xml / .json)", HorizontalAlignment = HorizontalAlignment.Stretch };
            var matchDanDan = new Button { Content = "立即匹配弹弹play 弹幕", Style = Application.Current.Resources["PrimaryButtonStyle"] as Style, HorizontalAlignment = HorizontalAlignment.Stretch };
            var searchDanDan = new Button { Content = "手动搜索弹弹play 剧集…", HorizontalAlignment = HorizontalAlignment.Stretch };
            var matchBili = new Button { Content = "匹配哔哩哔哩弹幕", HorizontalAlignment = HorizontalAlignment.Stretch };
            var resetDefaults = new Button { Content = "恢复默认样式" };
            var save = new Button { Content = "应用并关闭", Style = Application.Current.Resources["SecondaryButtonStyle"] as Style };
            var footerRow = new Grid { ColumnSpacing = 8 };
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(resetDefaults, 0);
            Grid.SetColumn(save, 2);
            footerRow.Children.Add(resetDefaults);
            footerRow.Children.Add(save);

            var options = new StackPanel { Width = 336, Spacing = 10, Children = { enabled, advanced, matchDanDan, searchDanDan, matchBili, loadLocal, footerRow } };
            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
                Content = new ScrollViewer { MaxHeight = Math.Max(180, ActualHeight - 116), Padding = new Thickness(0, 0, 8, 0), Content = options }
            };
            flyout.Closed += (_, _) => { _danmakuFlyout = null; if (IsLoaded) ShowControls(); };

            loadLocal.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
                    picker.FileTypeFilter.Add(".xml");
                    picker.FileTypeFilter.Add(".json");
                    var file = await picker.PickSingleFileAsync();
                    if (file is null) return;
                    _localDanmakuOverride = new Uri(file.Path);
                    _manualDanDanPlayEpisodeId = null;
                    _danmaku = _danmaku with { Enabled = true, Source = "origin" };
                    await PersistDanmakuPreferencesAsync();
                    UpdateDanmakuToggle();
                    await ReloadDanmakuAsync(false);
                    ShowOsd("已加载本地弹幕");
                }
                catch (Exception ex) { ViewModel.QualityMessage = "本地弹幕加载失败：" + ex.Message; }
            };

            matchDanDan.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    _localDanmakuOverride = null;
                    _manualDanDanPlayEpisodeId = null;
                    _danmaku = _danmaku with { Enabled = true, Source = "dandanplay" };
                    await PersistDanmakuPreferencesAsync();
                    UpdateDanmakuToggle();
                    await ReloadDanmakuAsync(true);
                }
                catch (Exception ex) { ViewModel.QualityMessage = "弹弹play 匹配失败：" + ex.Message; }
            };

            searchDanDan.Click += async (_, _) =>
            {
                flyout.Hide();
                await OpenDanDanPlaySearchDialogAsync();
            };

            matchBili.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    _localDanmakuOverride = null;
                    _manualDanDanPlayEpisodeId = null;
                    _danmaku = _danmaku with { Enabled = true, Source = "bilibili" };
                    await PersistDanmakuPreferencesAsync();
                    UpdateDanmakuToggle();
                    await ReloadDanmakuAsync(true);
                }
                catch (Exception ex) { ViewModel.QualityMessage = "B 站在线匹配失败：" + ex.Message; }
            };

            resetDefaults.Click += (_, _) =>
            {
                var keepEnabled = _danmaku.Enabled;
                var keepSource = _danmaku.Source;
                var keepAuto = _danmaku.AutoMatch;
                var keepWords = _danmaku.BlockedWords;
                _danmaku = new DanmakuPreferences(Enabled: keepEnabled, Source: keepSource, AutoMatch: keepAuto, BlockedWords: keepWords);
                opacitySlider.Value = Math.Round(_danmaku.Opacity * 100);
                sizeSlider.Value = _danmaku.Size;
                speed.SelectedIndex = 2;
                area.SelectedIndex = 1;
                density.SelectedIndex = 1;
                color.SelectedIndex = 0;
                outline.IsChecked = true;
                bold.IsChecked = true;
                showScroll.IsChecked = true;
                showTop.IsChecked = true;
                showBottom.IsChecked = true;
                showColor.IsChecked = true;
                timeOffset.Value = 0;
                ApplyLiveVisual(refilter: true);
                ShowOsd("已恢复默认弹幕样式");
            };

            save.Click += async (_, _) =>
            {
                ApplyLiveVisual(refilter: true);
                flyout.Hide();
                await PersistDanmakuPreferencesAsync();
            };

            _danmakuFlyout = flyout;
            if (IsLoaded) flyout.ShowAt(target);
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private void QueueSaveDanmakuPreferences()
    {
        _danmakuSaveTimer.Stop();
        _danmakuSaveTimer.Start();
    }

    private async Task PersistDanmakuPreferencesAsync()
    {
        try
        {
            await App.Services.Database.SetSettingAsync("playback.danmaku", JsonSerializer.Serialize(_danmaku));
        }
        catch (Exception ex) { Diagnostics.Log($"弹幕设置保存失败: {ex.Message}"); }
    }

    private async Task ReadDanmakuPreferencesAsync()
    {
        var saved = await App.Services.Database.GetSettingAsync("playback.danmaku");
        if (!string.IsNullOrWhiteSpace(saved))
            _danmaku = (JsonSerializer.Deserialize<DanmakuPreferences>(saved) ?? new()).Normalize();
        else
            _danmaku = _danmaku.Normalize();
        _danmakuPrefsLoaded = true;
        DanmakuLayer.SetPreferences(_danmaku);
    }

    private void OnDanmakuCleared(object? sender, EventArgs e)
    {
        CancelDanmakuLoad();
        _danmakuAddress = null;
        _localDanmakuOverride = null;
        _manualDanDanPlayEpisodeId = null;
        _rawComments = [];
        DanmakuLayer.Clear();
    }

    private void OnDanmakuChanged(object? sender, Uri? uri)
    {
        _danmakuAddress = uri;
        _localDanmakuOverride = null;
        _manualDanDanPlayEpisodeId = null;
        _ = ReloadDanmakuAsync(false);
    }

    private async Task OpenDanDanPlaySearchDialogAsync()
    {
        if (_danmakuSearchDialog is not null || XamlRoot is null) return;
        _hideTimer.Stop();
        var queryBox = new TextBox
        {
            Header = "搜索番剧 / 影视名称",
            Text = MediaMatching.Title(ViewModel.Title),
            PlaceholderText = "输入作品名称…"
        };
        var searchBtn = new Button { Content = "搜索", Style = Application.Current.Resources["SecondaryButtonStyle"] as Style, VerticalAlignment = VerticalAlignment.Bottom };
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(queryBox, 0);
        Grid.SetColumn(searchBtn, 1);
        searchRow.Children.Add(queryBox);
        searchRow.Children.Add(searchBtn);

        var statusText = new TextBlock { Text = "输入名称后点击搜索，在下方选择对应分集加载弹幕。", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var resultList = new ListView
        {
            Height = 280,
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(DanDanPlayEpisode.DisplayName)
        };

        async Task DoSearchAsync()
        {
            var q = queryBox.Text.Trim();
            if (q.Length == 0) return;
            searchBtn.IsEnabled = false;
            statusText.Text = "正在搜索弹弹play 开放弹幕库…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var groups = await App.Services.DanDanPlay.SearchEpisodesAsync(q, null, cts.Token);
                var flat = groups.SelectMany(g => g.Episodes).Take(120).ToArray();
                resultList.ItemsSource = flat;
                if (flat.Length > 0) resultList.SelectedIndex = 0;
                statusText.Text = flat.Length > 0 ? $"找到 {flat.Length} 个分集，选中后点击确定加载弹幕。" : "未找到匹配分集，请尝试缩短关键词。";
            }
            catch (Exception ex) { statusText.Text = "搜索失败：" + ex.Message; }
            finally { searchBtn.IsEnabled = true; }
        }

        searchBtn.Click += async (_, _) => await DoSearchAsync();
        queryBox.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; await DoSearchAsync(); }
        };

        var panel = new StackPanel { Width = 440, Spacing = 10, Children = { searchRow, statusText, resultList } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "搜索弹弹play 弹幕剧集",
            Content = panel,
            PrimaryButtonText = "加载所选弹幕",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        _danmakuSearchDialog = dialog;
        _ = DoSearchAsync();
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && resultList.SelectedItem is DanDanPlayEpisode picked)
            {
                _localDanmakuOverride = null;
                _manualDanDanPlayEpisodeId = picked.EpisodeId;
                _danmaku = _danmaku with { Enabled = true, Source = "dandanplay" };
                await PersistDanmakuPreferencesAsync();
                UpdateDanmakuToggle();
                ViewModel.QualityMessage = $"弹弹play 已切换：{picked.DisplayName}";
                await ReloadDanmakuAsync(true);
            }
        }
        finally
        {
            _danmakuSearchDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private async Task ReloadDanmakuAsync(bool forceMatch)
    {
        CancelDanmakuLoad();
        _rawComments = [];
        DanmakuLayer.Clear();
        _danmakuLoad = new CancellationTokenSource();
        var token = _danmakuLoad.Token;
        try
        {
            await ReadDanmakuPreferencesAsync();
            UpdateDanmakuToggle();
            if (!_danmaku.Enabled || token.IsCancellationRequested) return;

            IReadOnlyList<DanmakuEntry> comments;
            if (_localDanmakuOverride is { } localUri)
            {
                comments = await App.Services.Danmaku.LoadAsync(localUri, token);
            }
            else if (_danmaku.Source == "dandanplay")
            {
                if (!forceMatch && !_danmaku.AutoMatch && !_manualDanDanPlayEpisodeId.HasValue)
                {
                    ViewModel.QualityMessage = "在线弹幕自动匹配已关闭，可在弹幕设置中手动匹配。";
                    return;
                }
                long epId;
                if (_manualDanDanPlayEpisodeId is { } manualId)
                {
                    epId = manualId;
                }
                else
                {
                    var match = await ViewModel.MatchDanDanPlayAsync(token);
                    epId = match.EpisodeId;
                }
                comments = await App.Services.DanDanPlay.GetCommentsAsync(epId, withRelated: true, chConvert: 1, token);
            }
            else
            {
                var uri = _danmakuAddress;
                var onlineBili = _danmaku.Source == "bilibili";
                if (onlineBili && (forceMatch || _danmaku.AutoMatch && (!ViewModel.IsBilibiliPlayback || uri is null)))
                {
                    var match = await ViewModel.MatchOnlineMediaAsync(token);
                    uri = match.Danmaku;
                }
                else if (onlineBili && !_danmaku.AutoMatch && !forceMatch && !ViewModel.IsBilibiliPlayback)
                {
                    ViewModel.QualityMessage = "在线弹幕自动匹配已关闭，可在弹幕设置中手动匹配。";
                    return;
                }
                if (uri is null)
                {
                    ViewModel.QualityMessage = "当前来源没有弹幕，可在弹幕设置中加载本地文件或切换弹弹play / 哔哩哔哩匹配。";
                    return;
                }
                comments = await App.Services.Danmaku.LoadAsync(uri, token);
            }

            if (token.IsCancellationRequested) return;
            _rawComments = comments as DanmakuEntry[] ?? comments.ToArray();
            ApplyFilteredComments();
            var snap = ViewModel.Service.LastSnapshot;
            DanmakuLayer.SyncPlayback(snap.TimeMs / 1000d,
                snap.CapturedAtTimestamp != 0 ? snap.CapturedAtTimestamp : Stopwatch.GetTimestamp(),
                snap.Rate, snap.IsPlaying, ViewModel.IsBuffering);
            Diagnostics.Log($"Danmaku loaded: {DanmakuLayer.LoadedCount}/{_rawComments.Length} entries (source={_danmaku.Source})");
            if (DanmakuLayer.LoadedCount == 0) ViewModel.QualityMessage = "当前匹配节目没有可显示的弹幕。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ViewModel.QualityMessage = "弹幕加载失败：" + ex.Message; }
    }

    private void ApplyFilteredComments()
    {
        var blocked = _danmaku.BlockedWords.Split([' ', '，', ',', '\n', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var filtered = _rawComments.Where(comment =>
            !blocked.Any(word => comment.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) &&
            (!_danmaku.BasicFilter || !BasicDanmakuBlockedWords.Any(word => comment.Text.Contains(word, StringComparison.Ordinal)))).ToArray();
        DanmakuLayer.SetComments(filtered);
    }

    private void CancelDanmakuLoad()
    {
        var load = _danmakuLoad;
        _danmakuLoad = null;
        load?.Cancel();
        load?.Dispose();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => ShowArrowCursor();

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (AnyFlyoutOpen()) return;
        if (e.OriginalSource is FrameworkElement element && IsInsideControls(element)) return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var next = Math.Clamp(ViewModel.Volume + (delta > 0 ? 5 : -5), 0, 100);
        ViewModel.Volume = next;
        ShowOsd(next == 0 ? "已静音" : $"音量 {next}%");
        e.Handled = true;
    }

    private void OnBottomChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        var stacked = e.NewSize.Width < 800;
        BottomChromeExtraRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(TransportControls, stacked ? 2 : 1);
        Grid.SetRow(MediaControls, stacked ? 1 : 0);
        Grid.SetColumn(MediaControls, stacked ? 0 : 1);
        Grid.SetColumnSpan(MediaControls, stacked ? 2 : 1);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource is FrameworkElement element && IsInsideControls(element)) return;
        if (_controlsVisible) HideControlsNow(); else ShowControls();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource is FrameworkElement element && IsInsideControls(element)) return;
        // PCbox：双击画面暂停/播放（与空格一致）；单击仍负责控制层显隐。
        ViewModel.TogglePauseCommand.Execute(null);
        ShowOsd(ViewModel.IsPlaying ? "继续播放" : "已暂停");
        ShowControls();
        e.Handled = true;
    }

    private bool IsInsideControls(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, TopChromeBar) || ReferenceEquals(current, BottomChromeBar) ||
                ReferenceEquals(current, AiSubtitleBanner) || ReferenceEquals(current, CenterPlayButton) ||
                current is ButtonBase or Slider or Selector or TextBox or NumberBox or ToggleSwitch)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool _centerPlayVisible;

    private void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            SetChromeVisible(true);
        }
        _hideTimer.Stop();
        _hideTimer.Start();
        UpdateCenterPlayVisibility();
    }

    private void HideControlsNow()
    {
        _controlsVisible = false;
        SetChromeVisible(false);
        UpdateCenterPlayVisibility();
    }

    private void SetChromeVisible(bool visible)
    {
        if (visible) ShowArrowCursor();
        else SetCursor(0);
        MotionService.SetPlayerChromeVisible(ControlsLayer, TopChromeBar, BottomChromeBar, visible);
    }

    private void UpdateCenterPlayVisibility()
    {
        var show = _controlsVisible && ViewModel.HasMedia && !ViewModel.IsPlaying
            && !ViewModel.IsBuffering && !ViewModel.ShowStatusPanel;
        if (show == _centerPlayVisible) return;
        _centerPlayVisible = show;
        MotionService.SetFloatingVisible(CenterPlayButton, show, offsetY: 0, hiddenScale: 0.86f);
    }

    private bool AnyFlyoutOpen() => PlaylistFlyout.IsOpen || PlaybackOptionsFlyout.IsOpen || RateFlyout.IsOpen ||
        QualityFlyout.IsOpen || VolumeFlyout.IsOpen ||
        _danmakuFlyout is { IsOpen: true } || _subtitleFlyout is { IsOpen: true } || _timingFlyout is { IsOpen: true } ||
        _sourceDialog is not null || _onlineSubtitleDialog is not null || _danmakuSearchDialog is not null ||
        _bilibiliLoginDialog is not null || _cloudLoginDialog is not null;

    private void OnHideTimer(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsPlaying || AnyFlyoutOpen()) return;
        HideControlsNow();
    }

    private void OnMenuFlyoutOpening(object? sender, object e) => _hideTimer.Stop();

    private void OnMenuFlyoutClosed(object? sender, object e)
    {
        if (IsLoaded) ShowControls();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Window.GoBack();

    private void OnCloudLoginClick(object sender, RoutedEventArgs e) => _ = ShowCloudLoginAsync();

    private async Task ShowCloudLoginAsync()
    {
        if (_cloudLoginDialog is not null || ViewModel.RequiredLoginProvider is not { } provider || XamlRoot is null) return;
        _hideTimer.Stop();
        var coordinator = App.Services.CloudLogin;
        var panel = new CloudLoginPanel { DataContext = coordinator };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{provider} 网盘登录",
            Content = panel,
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close
        };
        _cloudLoginDialog = dialog;
        coordinator.Succeeded += OnCloudLoginSucceeded;
        panel.CloseRequested += (_, _) => dialog.Hide();
        try
        {
            var showing = dialog.ShowAsync();
            await coordinator.StartAsync(provider);
            await showing;
        }
        catch (Exception ex) { ViewModel.QualityMessage = "登录面板无法打开：" + ex.Message; }
        finally
        {
            coordinator.Succeeded -= OnCloudLoginSucceeded;
            coordinator.Close();
            _cloudLoginDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private async void OnCloudLoginSucceeded(object? sender, Models.CloudProviderKind provider)
    {
        if (ViewModel.RequiredLoginProvider != provider) return;
        _cloudLoginDialog?.Hide();
        await ViewModel.ResumeAfterCloudLoginAsync();
    }

    private async void OnSourceRecoveryOffered(object? sender, IReadOnlyList<VodItem> sources)
    {
        if (!IsLoaded || XamlRoot is null || _sourceDialog is not null || sources.Count == 0) return;
        _hideTimer.Stop();
        var choices = new ComboBox
        {
            ItemsSource = sources, DisplayMemberPath = "SiteName", SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, Header = "可用来源"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "当前站点资源可能已失效",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "已找到同名、同年份且包含播放线路的其他来源。是否保留当前集数和进度并切换？", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    choices
                }
            },
            PrimaryButtonText = "切换并播放", CloseButtonText = "暂不切换", DefaultButton = ContentDialogButton.Primary
        };
        _sourceDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is VodItem source)
                await ViewModel.SwitchSourceAsync(source);
        }
        catch (Exception ex) { ViewModel.StatusMessage = "来源切换失败：" + ex.Message; }
        finally
        {
            _sourceDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private void OnTimingSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_timingFlyout is not null) return;
        _hideTimer.Stop();
        var intro = new NumberBox
        {
            Header = "片头结束时间（秒）", Minimum = 0, Maximum = 21600,
            Value = ViewModel.Timing.IntroEndMs / 1000d, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var outro = new NumberBox
        {
            Header = "片尾开始时间（秒），0 为不跳过", Minimum = 0, Maximum = 21600,
            Value = ViewModel.Timing.OutroStartMs / 1000d, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var enabled = new ToggleSwitch { Header = "自动跳过本集片头片尾", IsOn = ViewModel.SkipEnabled };
        var message = new TextBlock { Text = "仅保存到当前站点、线路和集数，不影响其他剧集。", TextWrapping = TextWrapping.Wrap };
        var markIntro = new Button { Content = "当前位置设为片头结束" };
        var markOutro = new Button { Content = "当前位置设为片尾开始" };
        var save = new Button { Content = "保存", Style = Application.Current.Resources["PrimaryButtonStyle"] as Style, HorizontalAlignment = HorizontalAlignment.Right };
        var clear = new Button { Content = "清除本集设置" };
        async Task MarkAsync(bool isIntro)
        {
            try
            {
                await ViewModel.SetTimingFromPositionAsync(isIntro);
                intro.Value = ViewModel.Timing.IntroEndMs / 1000d;
                outro.Value = ViewModel.Timing.OutroStartMs / 1000d;
                enabled.IsOn = ViewModel.SkipEnabled;
                message.Text = ViewModel.TimingMessage;
            }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        markIntro.Click += async (_, _) => await MarkAsync(true);
        markOutro.Click += async (_, _) => await MarkAsync(false);
        clear.Click += async (_, _) =>
        {
            try { await ViewModel.SaveTimingAsync(0, 0, enabled.IsOn); intro.Value = outro.Value = 0; message.Text = "已清除当前集设置。"; }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            try { await ViewModel.SaveTimingAsync(intro.Value, outro.Value, enabled.IsOn); message.Text = ViewModel.TimingMessage; }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        var content = new StackPanel { Width = 332, Spacing = 12, Children = { intro, markIntro, outro, markOutro, enabled, message, clear, save } };
        var flyout = new Flyout { Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(240, ActualHeight - 100) } };
        flyout.Closed += (_, _) => { _timingFlyout = null; if (IsLoaded) ShowControls(); };
        _timingFlyout = flyout;
        PlaybackOptionsFlyout.Hide();
        flyout.ShowAt(PlaybackOptionsButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void OnCastClick(object sender, RoutedEventArgs e)
    {
        if (App.PlayerOnly) ViewModel.QualityMessage = "直达播放模式仅加载本地播放资源；需要投屏时请从完整应用打开。";
        else App.Window.Navigate("cast");
    }

    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e) => _ = ToggleDanmakuAsync();

    // ===== PCbox 影视仓式快捷按钮点击 =====
    private void OnDanmakuCloudSearchClick(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop();
        _ = OpenDanDanPlaySearchDialogAsync();
    }

    private void OnQuickPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasPlaylist) return;
        ShowControls();
        PlaylistFlyout.ShowAt(QuickPlaylistButton);
    }

    private void OnQuickRateClick(object sender, RoutedEventArgs e)
    {
        if (RateFlyout.Items.Count == 0) BuildFlyouts();
        ShowControls();
        RateFlyout.ShowAt(QuickRateButton);
    }

    private async Task ToggleDanmakuAsync()
    {
        try
        {
            if (!_danmakuPrefsLoaded) await ReadDanmakuPreferencesAsync();
            // Instant 0ms visual toggle — persist asynchronously via debounced timer
            _danmaku = _danmaku with { Enabled = !_danmaku.Enabled };
            UpdateDanmakuToggle();
            DanmakuLayer.SetPreferences(_danmaku);
            ShowOsd(_danmaku.Enabled ? "弹幕已开启" : "弹幕已关闭");
            QueueSaveDanmakuPreferences();
            ShowControls();
            if (IsLoaded && _danmaku.Enabled && _rawComments.Length == 0)
                await ReloadDanmakuAsync(false);
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private async Task RefreshDanmakuToggleAsync()
    {
        try
        {
            await ReadDanmakuPreferencesAsync();
            UpdateDanmakuToggle();
        }
        catch (Exception) { }
    }

    private void UpdateDanmakuToggle()
    {
        if (DanmakuOnIndicator is null) return;
        DanmakuOnIndicator.Visibility = _danmaku.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(DanmakuToggleButton, _danmaku.Enabled ? "关闭弹幕 (D · 右键设置)" : "显示弹幕 (D · 右键设置)");
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Volume == 0) ViewModel.Volume = _volumeBeforeMute;
        else
        {
            _volumeBeforeMute = ViewModel.Volume;
            ViewModel.Volume = 0;
        }
        ShowOsd(ViewModel.Volume == 0 ? "已静音" : $"音量 {ViewModel.Volume}%");
    }

    private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        _seekDragging = true;
        _hideTimer.Stop();
        ViewModel.BeginSeek();
    }

    private void OnSeekReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        ViewModel.EndSeek(SeekSlider.Value);
        ShowControls();
    }

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_seekDragging) return;
        var span = TimeSpan.FromMilliseconds(Math.Max(0, e.NewValue));
        ViewModel.PositionText = span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void StepRate(int delta)
    {
        var idx = Array.IndexOf(Rates, Rates.MinBy(r => Math.Abs(r - ViewModel.Rate)));
        var nextIdx = Math.Clamp(idx + delta, 0, Rates.Length - 1);
        ViewModel.Rate = Rates[nextIdx];
        ShowOsd($"倍速 {ViewModel.Rate:0.##}x");
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePauseCommand.Execute(null);
                ShowOsd(ViewModel.IsPlaying ? "继续播放" : "已暂停");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.F:
                ViewModel.FullScreenCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.M:
                OnMuteClick(this, e);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.P:
                if (PlaylistFlyout.IsOpen) PlaylistFlyout.Hide();
                else if (ViewModel.HasPlaylist)
                {
                    ShowControls();
                    PlaylistFlyout.ShowAt(PlaylistButton);
                }
                e.Handled = true;
                break;
            case VirtualKey.D:
                _ = ToggleDanmakuAsync();
                e.Handled = true;
                break;
            case VirtualKey.A:
                ViewModel.SetAspectMode((ViewModel.AspectModeIndex + 1) % PlayerViewModel.AspectModeNames.Length);
                ShowOsd($"画面比例：{ViewModel.AspectModeName}");
                e.Handled = true;
                break;
            case VirtualKey.I:
                ViewModel.ShowMediaInfo = !ViewModel.ShowMediaInfo;
                e.Handled = true;
                break;
            case VirtualKey.N:
                if (ViewModel.CanNext)
                {
                    ViewModel.NextEpisodeCommand.Execute(null);
                    ShowOsd("下一集");
                }
                e.Handled = true;
                break;
            case VirtualKey.J:
                ViewModel.SeekBy(-15);
                ShowOsd("后退 15 秒");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.L:
                ViewModel.SeekBy(15);
                ShowOsd("前进 15 秒");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Z:
                ViewModel.AdjustSubtitleDelay(-500);
                ShowOsd($"字幕前移（当前 {ViewModel.SubtitleDelayMs / 1000.0:+0.#;-0.#;0}s）");
                e.Handled = true;
                break;
            case VirtualKey.X:
                ViewModel.AdjustSubtitleDelay(500);
                ShowOsd($"字幕后移（当前 {ViewModel.SubtitleDelayMs / 1000.0:+0.#;-0.#;0}s）");
                e.Handled = true;
                break;
            case (VirtualKey)190: // '.' frame step
                ViewModel.StepFrame();
                ShowOsd("下一帧");
                e.Handled = true;
                break;
            case (VirtualKey)219: // '[' slower
                StepRate(-1);
                e.Handled = true;
                break;
            case (VirtualKey)221: // ']' faster
                StepRate(1);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (App.Window.IsPlayerFullScreen)
                {
                    ViewModel.FullScreenCommand.Execute(null);
                    ShowControls();
                }
                else if (PlaylistFlyout.IsOpen) PlaylistFlyout.Hide();
                else OnBackClick(this, e);
                e.Handled = true;
                break;
            case VirtualKey.Left:
                var backStep = shift ? -15 : -5;
                ViewModel.SeekBy(backStep);
                ShowOsd($"后退 {-backStep} 秒");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                var fwdStep = shift ? 15 : 5;
                ViewModel.SeekBy(fwdStep);
                ShowOsd($"前进 {fwdStep} 秒");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.Volume = Math.Min(100, ViewModel.Volume + 5);
                ShowOsd($"音量 {ViewModel.Volume}%");
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ViewModel.Volume = Math.Max(0, ViewModel.Volume - 5);
                ShowOsd(ViewModel.Volume == 0 ? "已静音" : $"音量 {ViewModel.Volume}%");
                ShowControls();
                e.Handled = true;
                break;
        }
    }
}
