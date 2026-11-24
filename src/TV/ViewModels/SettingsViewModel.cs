using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TV.Models;
using TV.Services;
using TV.Services.AI;
using TV.Services.Cloud;
using TV.Services.Persistence;
using TV.Services.Maintenance;
using TV.Services.Playback;
using TV.Services.Updates;
using TV.Views;

namespace TV.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loaded;
    private bool _hasLoaded;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly Dictionary<string, string> _pendingWrites = [];
    private readonly SemaphoreSlim _settingsWriteGate = new(1, 1);

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _saveTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(250);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await FlushPendingWritesAsync();
    }

    [ObservableProperty] private string _activeSource = "尚未加载";
    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _doubanEnabled = true;
    [ObservableProperty] private int _recommendationSourceIndex;
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<ImportedRepository> ImportedRepositories { get; } = [];
    public ObservableCollection<CloudAccountInfo> CloudAccounts { get; } = [];
    public ObservableCollection<CleanupCategory> CleanupCategories { get; } = [];
    [ObservableProperty] private bool _hasCleanupResults;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _cleanupSummary = string.Empty;
    [ObservableProperty] private bool _hardwareDecoding = true;
    [ObservableProperty] private bool _autoNext = true;
    [ObservableProperty] private bool _resumePlayback = true;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private int _closeBehaviorIndex;
    private bool _settingCloseBehavior;
    [ObservableProperty] private bool _autoRefreshSources = true;
    [ObservableProperty] private string _sourceSummary = string.Empty;
    [ObservableProperty] private string _lastSourceRefresh = string.Empty;
    [ObservableProperty] private string _storageSummary = string.Empty;
    [ObservableProperty] private string _downloadFolder = string.Empty;
    [ObservableProperty] private int _networkCache = 3600;
    [ObservableProperty] private int _liveCache = 1800;
    [ObservableProperty] private bool _adaptiveQuality = true;
    [ObservableProperty] private int _defaultVolume = 85;
    [ObservableProperty] private int _defaultQualityIndex;
    [ObservableProperty] private bool _autoSwitchLine = true;
    [ObservableProperty] private int _searchConcurrency = 3;
    [ObservableProperty] private bool _cloudAcceleration = true;
    [ObservableProperty] private int _cloudThreads = 3;
    [ObservableProperty] private int _cloudChunkMb = 2;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _dnsIndex;
    [ObservableProperty] private string _aliToken = string.Empty;
    [ObservableProperty] private string _quarkCookie = string.Empty;
    [ObservableProperty] private string _ucCookie = string.Empty;
    [ObservableProperty] private string _baiduCookie = string.Empty;
    public CloudLoginCoordinator CloudLogin => _services.CloudLogin;

    // --- 阿里云百炼 AI 字幕与翻译 ---
    [ObservableProperty] private string _bailianApiKeyInput = string.Empty;
    [ObservableProperty] private string _bailianKeyStatus = "尚未配置 API Key";
    [ObservableProperty] private int _bailianRegionIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AsrModelHint))] private int _asrModelIndex = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MtModelHint))] private int _mtModelIndex;
    [ObservableProperty] private int _mtTargetLanguageIndex;
    [ObservableProperty] private bool _aiBilingualSubtitles = true;
    [ObservableProperty] private bool _aiRealtimeTranslate;
    [ObservableProperty] private bool _isTestingAi;
    public string AsrModelHint => AiSubtitleModels.AsrModels[Math.Clamp(AsrModelIndex, 0, AiSubtitleModels.AsrModels.Count - 1)].Description;
    public string MtModelHint => AiSubtitleModels.MtModels[Math.Clamp(MtModelIndex, 0, AiSubtitleModels.MtModels.Count - 1)].Description;

    // --- 弹弹play 开放弹幕库 ---
    [ObservableProperty] private string _danDanPlayStatus = "正在检测…";
    [ObservableProperty] private string _danDanPlayAppIdInput = string.Empty;
    [ObservableProperty] private string _danDanPlayAppSecretInput = string.Empty;
    [ObservableProperty] private bool _isTestingDanDanPlay;

    [ObservableProperty] private bool _autoCheckUpdates = false;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string _updateStatus = "尚未检查更新";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAvailableUpdate))] private AppUpdateRelease? _availableUpdate;
    public bool HasAvailableUpdate => AvailableUpdate is not null;

    internal Task CheckUpdatesOnStartupAsync()
    {
        // PCbox：永久禁用自动更新检测，不再弹出升级提示（设置里仍可手动检查）。
        AutoCheckUpdates = false;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckUpdateAsync() => await CheckForUpdatesAsync(userInitiated: true);

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        UpdateStatus = userInitiated ? "正在检查 GitHub Releases…" : "正在后台检查更新…";
        try
        {
            AvailableUpdate = await _services.Updates.CheckAsync();
            UpdateStatus = AvailableUpdate is null
                ? $"当前已是最新版本（{_services.Updates.CurrentVersion.ToString(3)}）"
                : $"发现新版本 {AvailableUpdate.Version.ToString(3)}，可下载安装";
            if (AvailableUpdate is not null) await App.Window.ShowUpdateAsync(AvailableUpdate);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"检查更新失败: {ex.GetType().Name} {ex.Message}");
            UpdateStatus = userInitiated ? "检查更新失败：" + ex.Message : "自动检查暂不可用，将在下次启动时重试";
        }
        finally { IsCheckingUpdates = false; }
    }

    [RelayCommand]
    private async Task ShowUpdateAsync()
    {
        if (AvailableUpdate is not null) await App.Window.ShowUpdateAsync(AvailableUpdate);
    }

    public async Task LoadAsync()
    {
        if (_hasLoaded) return;
        _loaded = false;
        ActiveSource = _services.Config.ActiveUrl;
        await LoadImportsAsync();
        await RefreshCloudAccountsAsync();
        await UpdateSourceSummaryAsync();
        await RefreshStorageAsync();
        DoubanEnabled = await Bool("douban.enabled", true);
        RecommendationSourceIndex = await Int("vod.recommendations", 0) == 1 ? 1 : 0;
        HardwareDecoding = await Bool("playback.hardware", true);
        AutoNext = await Bool("playback.autoNext", true);
        ResumePlayback = await Bool("playback.resume", true);
        AnimationsEnabled = await Bool("ui.animations", true);
        MinimizeToTray = await Bool("ui.minimizeToTray", true);
        var behavior = await Int("ui.closeBehavior", 0);
        CloseBehaviorIndex = Enum.IsDefined((WindowCloseBehavior)behavior) ? behavior : 0;
        AutoRefreshSources = await Bool("source.autoRefresh", true);
        NetworkCache = await Int("playback.networkCache", 3600);
        LiveCache = await Int("playback.liveCache", 1800);
        AdaptiveQuality = await Bool("playback.adaptiveQuality", true);
        DefaultVolume = await Int("playback.volume", 85);
        DefaultQualityIndex = Math.Clamp(await Int("playback.defaultQuality", 0), 0, 4);
        AutoSwitchLine = await Bool("playback.autoSwitchLine", true);
        SearchConcurrency = await Int("search.concurrency", 3);
        CloudAcceleration = await Bool("playback.cloudAcceleration", true);
        CloudThreads = await Int("playback.cloudThreads", 3);
        CloudChunkMb = await Int("playback.cloudChunkMb", 2);
        ThemeIndex = await Int("ui.theme", 0);
        AutoCheckUpdates = await Bool("updates.autoCheck", false);
        DnsIndex = await Int("network.dns", 0);
        DownloadFolder = await _services.Database.GetSettingAsync("downloads.defaultFolder") ?? AppPaths.DefaultDownloads;
        AliToken = string.Empty;
        QuarkCookie = string.Empty;
        UcCookie = string.Empty;
        BaiduCookie = string.Empty;
        await LoadAiAndDanmakuSettingsAsync();
        _loaded = true;
        _hasLoaded = true;
    }

    private async Task LoadAiAndDanmakuSettingsAsync()
    {
        var aiConfig = await _services.BailianSubtitle.GetConfigAsync();
        BailianApiKeyInput = string.Empty;
        BailianKeyStatus = aiConfig.HasApiKey
            ? $"已加密保存（尾号 {(aiConfig.ApiKey.Length >= 4 ? aiConfig.ApiKey[^4..] : "****")}）"
            : "尚未配置，点击右侧按钮前往阿里云百炼免费生成密钥";
        BailianRegionIndex = string.Equals(aiConfig.Region, "intl", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AsrModelIndex = AiSubtitleModels.FindAsrIndex(aiConfig.AsrModel);
        MtModelIndex = AiSubtitleModels.FindMtIndex(aiConfig.MtModel);
        MtTargetLanguageIndex = AiSubtitleModels.FindTargetLanguageIndex(aiConfig.TargetLanguage);
        AiBilingualSubtitles = aiConfig.Bilingual;
        AiRealtimeTranslate = aiConfig.RealtimeTranslate;

        DanDanPlayAppIdInput = await _services.Database.GetSettingAsync(DanDanPlayService.SettingAppId) ?? string.Empty;
        DanDanPlayAppSecretInput = string.Empty;
        await RefreshDanDanPlayStatusAsync();
    }

    private async Task RefreshDanDanPlayStatusAsync()
    {
        if (await _services.DanDanPlay.HasCustomCredentialsAsync())
            DanDanPlayStatus = "已启用自定义弹弹play 开放平台凭证（DPAPI 加密保存）";
        else if (_services.DanDanPlay.HasBuiltInCredentials)
            DanDanPlayStatus = "已接入内置弹弹play 开放弹幕库（支持主备密钥自动切换）";
        else
            DanDanPlayStatus = "尚未配置凭证；请在下方填入自定义 AppId / AppSecret";
    }

    [RelayCommand]
    private async Task OpenBailianConsoleAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(AiSubtitleModels.BailianApiKeyConsoleUrl));
            StatusMessage = "已在浏览器打开阿里云百炼 API Key 管理页，生成密钥后粘贴到下方保存即可使用。";
        }
        catch (Exception ex) { StatusMessage = "无法打开浏览器：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveBailianKeyAsync()
    {
        var key = BailianApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusMessage = "请先粘贴阿里云百炼 API Key（通常以 sk- 开头）。";
            return;
        }
        try
        {
            await _services.BailianSubtitle.SaveApiKeyAsync(key);
            BailianApiKeyInput = string.Empty;
            BailianKeyStatus = $"已加密保存（尾号 {(key.Length >= 4 ? key[^4..] : "****")}）";
            StatusMessage = "阿里云百炼 API Key 已使用当前用户 DPAPI 加密保存，输入框已清空。";
        }
        catch (Exception ex) { StatusMessage = "保存百炼 API Key 失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task VerifyBailianKeyAsync()
    {
        if (IsTestingAi) return;
        IsTestingAi = true;
        StatusMessage = "正在验证阿里云百炼 API Key 连通性…";
        try
        {
            var region = BailianRegionIndex == 1 ? "intl" : "cn";
            var (ok, message) = await _services.BailianSubtitle.VerifyApiKeyAsync(
                string.IsNullOrWhiteSpace(BailianApiKeyInput) ? null : BailianApiKeyInput, region);
            StatusMessage = message;
            if (ok && !string.IsNullOrWhiteSpace(BailianApiKeyInput))
                await SaveBailianKeyAsync();
        }
        finally { IsTestingAi = false; }
    }

    [RelayCommand]
    private async Task ClearBailianKeyAsync()
    {
        await _services.BailianSubtitle.ClearApiKeyAsync();
        BailianApiKeyInput = string.Empty;
        BailianKeyStatus = "尚未配置，点击右侧按钮前往阿里云百炼免费生成密钥";
        StatusMessage = "已清除本机保存的阿里云百炼 API Key。";
    }

    [RelayCommand]
    private async Task OpenDanDanPlayDocsAsync()
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(DanDanPlayService.OpenPlatformUrl)); }
        catch (Exception ex) { StatusMessage = "无法打开浏览器：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveDanDanPlayAsync()
    {
        var appId = DanDanPlayAppIdInput.Trim();
        var appSecret = DanDanPlayAppSecretInput.Trim();
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            StatusMessage = "请同时填写弹弹play 的 AppId 与 AppSecret。";
            return;
        }
        try
        {
            await _services.DanDanPlay.SaveCustomCredentialsAsync(appId, appSecret);
            DanDanPlayAppSecretInput = string.Empty;
            await RefreshDanDanPlayStatusAsync();
            StatusMessage = "自定义弹弹play 凭证已加密保存。";
        }
        catch (Exception ex) { StatusMessage = "保存弹弹play 凭证失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task VerifyDanDanPlayAsync()
    {
        if (IsTestingDanDanPlay) return;
        IsTestingDanDanPlay = true;
        StatusMessage = "正在验证弹弹play 开放弹幕库连接…";
        try
        {
            var id = string.IsNullOrWhiteSpace(DanDanPlayAppIdInput) ? null : DanDanPlayAppIdInput.Trim();
            var secret = string.IsNullOrWhiteSpace(DanDanPlayAppSecretInput) ? null : DanDanPlayAppSecretInput.Trim();
            var (ok, msg) = await _services.DanDanPlay.VerifyCredentialsAsync(id, secret);
            StatusMessage = ok ? msg : "弹弹play 验证失败：" + msg;
        }
        finally { IsTestingDanDanPlay = false; }
    }

    [RelayCommand]
    private async Task ClearDanDanPlayAsync()
    {
        await _services.DanDanPlay.ClearCustomCredentialsAsync();
        DanDanPlayAppIdInput = string.Empty;
        DanDanPlayAppSecretInput = string.Empty;
        await RefreshDanDanPlayStatusAsync();
        StatusMessage = "已清除自定义弹弹play 凭证。";
    }

    partial void OnBailianRegionIndexChanged(int value) =>
        Save(AiSubtitleModels.SettingRegion, value == 1 ? "intl" : "cn");

    partial void OnAsrModelIndexChanged(int value)
    {
        var model = AiSubtitleModels.AsrModels[Math.Clamp(value, 0, AiSubtitleModels.AsrModels.Count - 1)];
        Save(AiSubtitleModels.SettingAsrModel, model.Id);
    }

    partial void OnMtModelIndexChanged(int value)
    {
        var model = AiSubtitleModels.MtModels[Math.Clamp(value, 0, AiSubtitleModels.MtModels.Count - 1)];
        Save(AiSubtitleModels.SettingMtModel, model.Id);
    }

    partial void OnMtTargetLanguageIndexChanged(int value)
    {
        var lang = AiSubtitleModels.TargetLanguages[Math.Clamp(value, 0, AiSubtitleModels.TargetLanguages.Count - 1)];
        Save(AiSubtitleModels.SettingTargetLanguage, lang.ApiName);
    }

    partial void OnAiBilingualSubtitlesChanged(bool value) =>
        Save(AiSubtitleModels.SettingBilingual, value);

    partial void OnAiRealtimeTranslateChanged(bool value) =>
        Save(AiSubtitleModels.SettingRealtimeTranslate, value);

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusMessage = "正在更新…";
        try
        {
            await _services.Config.LoadAsync(true);
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            StatusMessage = "已更新";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        try
        {
            await _services.Config.ImportAsync(NewSourceUrl);
            NewSourceUrl = string.Empty;
            await LoadImportsAsync();
            StatusMessage = "已导入";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(ImportedRepository? repository)
    {
        if (repository is null) return;
        try
        {
            await _services.Config.RemoveImportAsync(repository.Url);
            await LoadImportsAsync();
            StatusMessage = "已删除";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task LoadImportsAsync()
    {
        var repositories = await _services.Config.GetImportsAsync();
        ImportedRepositories.Clear();
        foreach (var repository in repositories) ImportedRepositories.Add(repository);
    }

    private async Task UpdateSourceSummaryAsync()
    {
        var config = _services.Config.Current;
        SourceSummary = config is null ? "尚未加载配置" : $"{config.Sites.Count} 个点播站点 · {config.Lives.Count} 个直播源 · {ImportedRepositories.Count} 个导入仓库";
        if (DateTimeOffset.TryParse(await _services.Database.GetSettingAsync("source.lastRefresh"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var refreshed))
            LastSourceRefresh = "最近刷新 " + refreshed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        else LastSourceRefresh = "尚未记录刷新时间";
    }

    [RelayCommand]
    private async Task RefreshCloudAccountsAsync()
    {
        var accounts = new List<CloudAccountInfo>();
        foreach (var provider in Enum.GetValues<CloudProviderKind>()) accounts.Add(await _services.Cloud.GetAccountAsync(provider));
        CloudAccounts.Clear();
        foreach (var account in accounts) CloudAccounts.Add(account);
    }

    [RelayCommand]
    private async Task VerifyCloudAsync(CloudProviderKind provider)
    {
        var index = CloudAccounts.ToList().FindIndex(account => account.Provider == provider);
        if (index >= 0) CloudAccounts[index] = CloudAccounts[index] with { State = CloudAccountState.Checking, Status = "正在验证…" };
        try
        {
            var account = await _services.Cloud.VerifyAccountAsync(provider);
            if (index >= 0 && index < CloudAccounts.Count && CloudAccounts[index].Provider == provider) CloudAccounts[index] = account;
            StatusMessage = $"{account.Name}：{account.Status}";
        }
        catch (Exception ex)
        {
            if (index >= 0 && index < CloudAccounts.Count) CloudAccounts[index] = CloudAccounts[index] with
            { State = CloudAccountState.Error, Status = "验证失败，请稍后重试" };
            StatusMessage = "网盘账号验证失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenCloudDrive() => App.Window.Navigate("cloud");

    [RelayCommand]
    private void ResetCloudAcceleration()
    {
        CloudAcceleration = true;
        CloudThreads = 3;
        CloudChunkMb = 2;
        StatusMessage = "已恢复安全加速默认值";
    }

    [RelayCommand]
    private async Task RefreshStorageAsync()
    {
        try
        {
            long images = 0;
            long database = 0;
            await Task.Run(() =>
            {
                if (Directory.Exists(AppPaths.Images))
                    images = Directory.EnumerateFiles(AppPaths.Images, "*.img").Sum(path => new FileInfo(path).Length);
                if (File.Exists(AppPaths.Database)) database = new FileInfo(AppPaths.Database).Length;
                var wal = AppPaths.Database + "-wal";
                if (File.Exists(wal)) database += new FileInfo(wal).Length;
            });
            var history = await _services.Database.CountRowsAsync("history");
            var favorites = await _services.Database.CountRowsAsync("favorites");
            var downloads = await _services.Database.CountRowsAsync("downloads");
            var metadata = await _services.Database.CountRowsAsync("metadata");
            StorageSummary = $"图片缓存 {FormatSize(images)} · 元数据 {metadata} 项 · 数据库 {FormatSize(database)}\n播放记录 {history} 项 · 收藏 {favorites} 项 · 下载 {downloads} 项";
        }
        catch (Exception ex) { StorageSummary = "存储统计暂不可用：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SelectDownloadFolderAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) DownloadFolder = folder.Path;
        }
        catch (Exception ex) { StatusMessage = "选择下载目录失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(AppPaths.Root);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
    }

    [RelayCommand]
    private async Task OpenDiagnosticsAsync()
    {
        var path = Path.Combine(AppPaths.Root, "diagnostic.log");
        if (!File.Exists(path)) { StatusMessage = "诊断日志尚未生成"; return; }
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.##} GB" : $"{bytes / 1024d / 1024:0.##} MB";

    [RelayCommand]
    private async Task SaveCloudAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AliToken)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Ali, AliToken);
            if (!string.IsNullOrWhiteSpace(QuarkCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Quark, QuarkCookie);
            if (!string.IsNullOrWhiteSpace(UcCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.UC, UcCookie);
            if (!string.IsNullOrWhiteSpace(BaiduCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Baidu, BaiduCookie);
            _services.Cloud.ClearShareCache();
            AliToken = QuarkCookie = UcCookie = BaiduCookie = string.Empty;
            await RefreshCloudAccountsAsync();
            StatusMessage = "网盘凭据已加密保存，输入框已清空";
        }
        catch (Exception ex) { StatusMessage = "网盘凭据保存失败：" + ex.Message; }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartCloudLoginAsync(string? provider)
    {
        var kind = CloudDriveService.ParseProvider(provider ?? string.Empty);
        await CloudLogin.StartAsync(kind);
    }

    [RelayCommand]
    private void CancelCloudLogin() => CloudLogin.Close();

    [RelayCommand]
    private async Task LogoutCloudAsync(CloudProviderKind kind)
    {
        await _services.Cloud.SaveCredentialAsync(kind, string.Empty);
        if (kind == CloudProviderKind.Ali) AliToken = string.Empty;
        else if (kind == CloudProviderKind.Quark) QuarkCookie = string.Empty;
        else if (kind == CloudProviderKind.UC) UcCookie = string.Empty;
        else BaiduCookie = string.Empty;
        _services.Cloud.ClearShareCache();
        await RefreshCloudAccountsAsync();
        StatusMessage = $"{kind} 已退出";
    }

    [RelayCommand]
    private async Task ScanCleanupAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        try
        {
            var categories = await _services.Cleanup.ScanAsync(CancellationToken.None);
            CleanupCategories.Clear();
            foreach (var category in categories) CleanupCategories.Add(category);
            HasCleanupResults = CleanupCategories.Count > 0;
            CleanupSummary = $"共扫描到 {CleanupCategories.Sum(category => category.Bytes) / 1024d / 1024:0.##} MB；高风险项目默认不选中。";
        }
        catch (Exception ex) { CleanupSummary = "扫描失败：" + ex.Message; }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (IsCleaning) return;
        var selected = CleanupCategories.Where(category => category.Selected).Select(category => category.Id).ToArray();
        if (selected.Length == 0) { StatusMessage = "请先勾选要清理的项目"; return; }
        IsCleaning = true;
        try
        {
            var result = await _services.Cleanup.CleanAsync(selected, new Progress<string>(value => StatusMessage = value), CancellationToken.None);
            StatusMessage = result.Summary;
            IsCleaning = false;
            await ScanCleanupAsync();
            await RefreshStorageAsync();
        }
        catch (Exception ex) { StatusMessage = "清理失败：" + ex.Message; }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task ClearCachesAsync()
    {
        await _services.Images.ClearAsync();
        await _services.Database.ClearTableAsync("metadata");
        _services.Cloud.ClearShareCache();
        await RefreshStorageAsync();
        StatusMessage = "缓存已清理";
    }

    [RelayCommand]
    private async Task ClearImagesAsync() { await _services.Images.ClearAsync(); await RefreshStorageAsync(); StatusMessage = "图片缓存已清除"; }

    [RelayCommand]
    private async Task ClearHistoryAsync() { await _services.Database.ClearTableAsync("history"); StatusMessage = "播放记录已清除"; }

    [RelayCommand]
    private async Task ClearFavoritesAsync() { await _services.Database.ClearTableAsync("favorites"); StatusMessage = "收藏已清除"; }

    [RelayCommand]
    private async Task ClearMetadataAsync() { await _services.Database.ClearTableAsync("metadata"); await RefreshStorageAsync(); StatusMessage = "元数据缓存已清除"; }

    partial void OnDoubanEnabledChanged(bool value) => Save("douban.enabled", value);
    partial void OnRecommendationSourceIndexChanged(int value)
    {
        if (!_loaded || value is not (0 or 1)) return;
        App.Window.ViewModel.Vod.UseRecommendationSource(value == 1);
        _ = SaveRecommendationSourceAsync(value);
    }

    private async Task SaveRecommendationSourceAsync(int value)
    {
        try
        {
            await _services.Database.SetSettingAsync("vod.recommendations", value == 1 ? "1" : "0");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"推荐来源保存失败: {ex.Message}");
            StatusMessage = "推荐来源保存失败";
        }
    }
    partial void OnHardwareDecodingChanged(bool value) => Save("playback.hardware", value);
    partial void OnAutoNextChanged(bool value) => Save("playback.autoNext", value);
    partial void OnResumePlaybackChanged(bool value) => Save("playback.resume", value);
    partial void OnAutoRefreshSourcesChanged(bool value) => Save("source.autoRefresh", value);
    partial void OnAutoCheckUpdatesChanged(bool value) => Save("updates.autoCheck", value);
    partial void OnNetworkCacheChanged(int value) => Save("playback.networkCache", Math.Clamp(value, 1200, 12000));
    partial void OnLiveCacheChanged(int value) => Save("playback.liveCache", Math.Clamp(value, 500, 8000));
    partial void OnAdaptiveQualityChanged(bool value) => Save("playback.adaptiveQuality", value);
    partial void OnDefaultVolumeChanged(int value) => Save("playback.volume", Math.Clamp(value, 0, 100));
    partial void OnDefaultQualityIndexChanged(int value) => Save("playback.defaultQuality", Math.Clamp(value, 0, 4));
    partial void OnAutoSwitchLineChanged(bool value) => Save("playback.autoSwitchLine", value);
    partial void OnSearchConcurrencyChanged(int value) => Save("search.concurrency", Math.Clamp(value, 1, 6));
    partial void OnCloudAccelerationChanged(bool value) => Save("playback.cloudAcceleration", value);
    partial void OnCloudThreadsChanged(int value) => Save("playback.cloudThreads", Math.Clamp(value, 1, 6));
    partial void OnCloudChunkMbChanged(int value) => Save("playback.cloudChunkMb", Math.Clamp(value, 1, 8));

    partial void OnAnimationsEnabledChanged(bool value)
    {
        Save("ui.animations", value);
        MotionService.Enabled = value;
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (!_loaded) return;
        App.Window.SetMinimizeToTray(value);
        Save("ui.minimizeToTray", value);
    }

    partial void OnCloseBehaviorIndexChanged(int value)
    {
        if (_loaded && !_settingCloseBehavior && Enum.IsDefined((WindowCloseBehavior)value))
            _ = SaveCloseBehaviorAsync((WindowCloseBehavior)value);
    }

    internal async Task SaveCloseBehaviorAsync(WindowCloseBehavior behavior)
    {
        try
        {
            await _services.Database.SetSettingAsync("ui.closeBehavior", ((int)behavior).ToString(System.Globalization.CultureInfo.InvariantCulture));
            App.Window.SetCloseBehavior(behavior);
            _settingCloseBehavior = true;
            CloseBehaviorIndex = (int)behavior;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"关闭偏好保存失败: {ex.Message}");
            StatusMessage = "关闭偏好保存失败";
        }
        finally { _settingCloseBehavior = false; }
    }

    partial void OnDnsIndexChanged(int value) => Save("network.dns", Math.Clamp(value, 0, 5));
    partial void OnDownloadFolderChanged(string value) => Save("downloads.defaultFolder", value.Trim());

    partial void OnThemeIndexChanged(int value)
    {
        Save("ui.theme", value);
        if (_loaded) App.Window.SetTheme(value switch { 1 => ElementTheme.Light, 2 => ElementTheme.Default, _ => ElementTheme.Dark });
    }

    private async Task<bool> Bool(string key, bool fallback) => await _services.Database.GetSettingAsync(key) is not { } value ? fallback : value != "false";
    private async Task<int> Int(string key, int fallback) => int.TryParse(await _services.Database.GetSettingAsync(key),
        System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private void Save(string key, object value)
    {
        if (!_loaded) return;
        _pendingWrites[key] = value is bool flag ? (flag ? "true" : "false") : value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : value.ToString()!;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal Task FlushAsync()
    {
        _saveTimer.Stop();
        return FlushPendingWritesAsync();
    }

    private async Task FlushPendingWritesAsync()
    {
        await _settingsWriteGate.WaitAsync();
        try
        {
            while (_pendingWrites.Count > 0)
            {
                var writes = _pendingWrites.ToArray();
                _pendingWrites.Clear();
                foreach (var (key, value) in writes)
                {
                    try { await _services.Database.SetSettingAsync(key, value); }
                    catch (Exception ex)
                    {
                        Diagnostics.Log($"设置保存失败 ({key}): {ex.GetType().Name}");
                        StatusMessage = $"设置保存失败：{ex.Message}";
                    }
                }
            }
        }
        catch (Exception ex) { Diagnostics.Log($"设置写入队列失败: {ex.GetType().Name}"); }
        finally { _settingsWriteGate.Release(); }
    }
}
