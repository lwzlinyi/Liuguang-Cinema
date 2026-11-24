using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using TV.Models;
using TV.Services;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.ViewModels;

public sealed partial class VodViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<PlaybackRequest> _play;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource _cts = new();
    private int _page = 1;
    private int _pageCount = 1;
    private bool _initialized;
    private bool _recommendationsDirty;
    private bool? _preferDouban;
    private bool _suppressSelection;
    private CancellationTokenSource _detailCts = new();
    private readonly List<VodCardViewModel> _browseCards = [];
    private JsonElement _siteFilters;

    public VodViewModel(AppServices services, Action<PlaybackRequest> play)
    {
        _services = services;
        _play = play;
        _services.Config.Changed += OnConfigChanged;
    }

    public event EventHandler? DetailRequested;
    public ObservableCollection<SourceSite> Sites { get; } = [];
    public ObservableCollection<VodCategory> Categories { get; } = [];
    public ObservableCollection<VodCardViewModel> Items { get; } = [];
    public ObservableCollection<VodCardViewModel> GalleryItems { get; } = [];
    public bool ShowGallery => GalleryItems.Count >= 3;
    public ObservableCollection<PlaybackLine> Lines { get; } = [];
    public ObservableCollection<Episode> Episodes { get; } = [];
    public List<string> TypeFilters { get; } = ["全部", "电影", "电视剧", "动漫", "综艺"];
    public ObservableCollection<VodCategoryFilterChoice> CategoryFilters { get; } = [];
    public bool HasCategoryFilters => CategoryFilters.Count > 0;

    [ObservableProperty] private SourceSite? _selectedSite;
    private SourceSite? _lastContentSite;
    [ObservableProperty] private VodCategory? _selectedCategory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DetailSources)), NotifyPropertyChangedFor(nameof(HasAlternateSources))]
    private VodCardViewModel? _selectedCard;
    public IReadOnlyList<VodCardViewModel> DetailSources => SelectedCard?.MatchedSources is { Count: > 0 } sources
        ? sources : SelectedCard is { } selected ? [selected] : [];
    public bool HasAlternateSources => DetailSources.Count > 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCredits)), NotifyPropertyChangedFor(nameof(CanExpandIntro)),
        NotifyPropertyChangedFor(nameof(DetailMetadata))]
    private VodItem? _detail;
    public IReadOnlyList<string> DetailMetadata => Detail is null ? [] : new string?[] { Detail.Rating, Detail.Year, Detail.TypeName,
        Detail.Area, Detail.Remarks, Detail.SiteName }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
    public bool HasCredits => !string.IsNullOrWhiteSpace(Detail?.Director) || !string.IsNullOrWhiteSpace(Detail?.Actor);
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private Episode? _currentEpisode;
    [ObservableProperty] private string _primaryPlayText = "播放";
    private long _resumePositionMs;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _statusMessage = "正在准备片源…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingVerification))]
    private SiteVerificationRequiredException? _pendingVerification;
    public bool HasPendingVerification => PendingVerification is not null;
    [ObservableProperty] private string _detailStatus = string.Empty;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _typeFilter = "全部";
    [ObservableProperty] private string _yearFilter = string.Empty;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _episodeFilter = string.Empty;
    [ObservableProperty] private bool _isFindingSources;
    [RelayCommand]
    private async Task FindSourcesAsync()
    {
        if (SelectedCard is not { } card || Detail is not { } current || IsFindingSources) return;
        IsFindingSources = true;
        DetailStatus = "正在查找同名、同年份的有效线路…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
            var candidates = await _services.SourceRecovery.FindAsync(current, timeout.Token);
            var sources = DetailSources.Where(source => !candidates.Any(item => item.SiteKey == source.Item.SiteKey))
                .Concat(candidates.Select(item => new VodCardViewModel(item, _services) { Poster = card.Poster, Rating = card.Rating })).ToArray();
            foreach (var source in sources) source.MatchedSources = sources;
            if (!ReferenceEquals(SelectedCard, card)) return;
            OnPropertyChanged(nameof(DetailSources));
            OnPropertyChanged(nameof(HasAlternateSources));
            DetailStatus = candidates.Count > 0 ? $"找到 {candidates.Count} 个新来源，可在“片源”中切换。" : "暂未找到同名、同年份的其他有效来源。";
        }
        catch (OperationCanceledException) { DetailStatus = "换源检索超时，可稍后重试。"; }
        catch (Exception ex) { DetailStatus = "换源检索失败：" + ex.Message; }
        finally { IsFindingSources = false; }
    }

    // PCbox 聚合：后台检索其他站点同名资源并合并到片源下拉，不抢占手动换源的 IsFindingSources 状态。
    private async Task AutoAggregateSourcesAsync(VodCardViewModel card, CancellationToken token)
    {
        try
        {
            var candidates = await _services.SourceRecovery.FindAsync(card.Item, token, limit: 12);
            if (candidates.Count == 0 || token.IsCancellationRequested || !ReferenceEquals(SelectedCard, card)) return;
            var existing = card.MatchedSources.Count > 0 ? card.MatchedSources : [card];
            var sources = existing.Where(source => !candidates.Any(item => item.SiteKey == source.Item.SiteKey))
                .Concat(candidates.Select(item => new VodCardViewModel(item, _services)
                {
                    Poster = card.Poster,
                    Rating = card.Rating
                })).ToArray();
            foreach (var source in sources) source.MatchedSources = sources;
            if (!ReferenceEquals(SelectedCard, card)) return;
            OnPropertyChanged(nameof(DetailSources));
            OnPropertyChanged(nameof(HasAlternateSources));
            if (!token.IsCancellationRequested) DetailStatus = $"已聚合 {sources.Length} 个片源，可在“片源”中切换线路。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log($"自动聚合片源失败: {ex.GetType().Name}"); }
    }

    partial void OnEpisodeFilterChanged(string value) => UpdateEpisodes();

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            if (_recommendationsDirty) await LoadHomeAsync();
            return;
        }
        _initialized = true;
        await ReloadCoreAsync(allowFallback: true);
    }

    internal void UseRecommendationSource(bool preferDouban)
    {
        _preferDouban = preferDouban;
        _recommendationsDirty = true;
    }

    [RelayCommand]
    private Task ReloadAsync() => ReloadCoreAsync(allowFallback: false);

    private async Task ReloadCoreAsync(bool allowFallback)
    {
        var token = BeginRequest();
        IsLoading = true;
        StatusMessage = "正在加载饭太硬配置…";
        Diagnostics.Log("Vod reload started");
        try
        {
            var config = await _services.Config.LoadAsync(false, token);
            var homeSite = (!allowFallback ? SelectedSite?.Key : null)
                ?? await _services.Database.GetSettingAsync("source.homeSite", token);
            token.ThrowIfCancellationRequested();
            Diagnostics.Log($"Vod config loaded: {config.Sites.Count} sites");
            _suppressSelection = true;
            Sites.Clear();
            foreach (var site in config.Sites) Sites.Add(site);
            SelectedSite = Sites.FirstOrDefault(site => site.Key == homeSite && site.IsSupported && !Services.Vod.Native.MyDriveVodProvider.Supports(site))
                ?? AlternateHomeSites().FirstOrDefault()
                ?? Sites.FirstOrDefault(site => site.IsSupported) ?? Sites.FirstOrDefault();
            _suppressSelection = false;
            if (SelectedSite is null) throw new InvalidDataException("配置中没有点播站点。");
            await LoadHomeAsync(allowFallback);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    private async Task LoadHomeAsync(bool allowFallback = false)
    {
        if (SelectedSite is not { } site) return;
        _recommendationsDirty = false;
        var token = BeginRequest();
        IsLoading = true;
        PendingVerification = null;
        StatusMessage = $"正在读取 {site.Name}…";
        try
        {
            var preferDouban = _preferDouban ?? await _services.Database.GetSettingAsync("vod.recommendations", token) == "1";
            var trendingTask = preferDouban ? ReadTrendingSafeAsync(token) : null;
            var siteTask = ReadHomeResultAsync(site, allowFallback, token);
            var searchOnly = Services.Vod.Native.NativeVodProviderRouter.IsSearchOnly(site);
            var cached = !searchOnly ? await ReadHomeCacheAsync(site, allowFallback, token) : null;
            token.ThrowIfCancellationRequested();
            if (cached is { } cachedHome)
            {
                // 启动时先用本地缓存立即出内容（即使已过期），随后网络结果到达再静默替换：
                // 这样「打开就能用」，避免每次都等远端站点超时。
                DisplayHome(cachedHome.Site, cachedHome.Response);
                IsLoading = false;
                Diagnostics.Log($"Vod home from cache: {cachedHome.Site.Key}, fresh={cachedHome.IsFresh}, {cachedHome.Response.List.Count} items");
            }
            if (trendingTask is not null && await trendingTask is { Count: > 0 } selectedTrending)
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in selectedTrending) item.SiteKey = site.Key;
                DisplayHome(site, new CmsResponse { List = selectedTrending.ToList(), Page = 1, PageCount = 1 }, true);
                IsLoading = false;
                var (siteHome, _) = await siteTask;
                token.ThrowIfCancellationRequested();
                if (siteHome is not null)
                {
                    _siteFilters = siteHome.Filters;
                    foreach (var category in siteHome.Categories) Categories.Add(category);
                    UpdateCategoryFilters(SelectedCategory);
                    if (siteHome.List.Count > 0) _ = SaveHomeCacheAsync(site, siteHome, token);
                }
                return;
            }
            var (response, homeStatus) = await siteTask;
            // 首选站点失败时，先尝试其它可用站点的首页（延续 1.1.9 行为），
            // 全部失败后才回退豆瓣推荐——否则每次启动都会被误判成“只能看豆瓣”。
            if (allowFallback && !searchOnly && (response is null || response.List.Count == 0 && response.Categories.Count == 0))
            {
                var alternate = await FindAvailableHomeAsync(site, token);
                if (alternate is { } available)
                {
                    site = available.Site;
                    response = available.Response;
                }
            }
            if (response is null || response.List.Count == 0)
            {
                if (cached is { } previous && previous.Site.Key == site.Key && response is null)
                {
                    DisplayHome(previous.Site, previous.Response);
                    IsLoading = false;
                    return;
                }
                var trending = await (trendingTask ?? ReadTrendingSafeAsync(token));
                token.ThrowIfCancellationRequested();
                if (trending.Count > 0)
                {
                    foreach (var item in trending) item.SiteKey = site.Key;
                    var categories = response?.Categories ?? [];
                    DisplayHome(site, new CmsResponse { List = trending.ToList(), Categories = categories, Filters = response?.Filters ?? default,
                        Page = 1, PageCount = 1 }, true);
                    return;
                }
            }
            token.ThrowIfCancellationRequested();
            if (cached is { } fallback && (response is null || response.List.Count == 0 && response.Categories.Count == 0))
            {
                // 站点这次没取到内容：优先回退到本地缓存（哪怕已过期），
                // 不要直接把首页换成豆瓣推荐，否则用户会以为站点配置失效。
                DisplayHome(fallback.Site, fallback.Response);
                IsLoading = false;
                return;
            }
            if (response is null)
            {
                if (cached is null)
                {
                    DisplayHome(site, new CmsResponse());
                    StatusMessage = PendingVerification is not null ? PendingVerification.Message
                        : homeStatus == HttpStatusCode.Forbidden ? "该站点拒绝访问（403）" : "片源暂不可用";
                }
                return;
            }

            DisplayHome(site, response);
            Diagnostics.Log($"Vod home from network: {site.Key}, {response.List.Count} items");
            if (response.List.Count > 0) _ = SaveHomeCacheAsync(site, response, token);
            var isBilibili = Services.Vod.Native.BilibiliVodProvider.Supports(site);
            if (Items.Count == 0 && Categories.Skip(1).FirstOrDefault(category =>
                !isBilibili || !Services.Vod.Native.BilibiliVodProvider.IsLoginCategory(category.Id)) is { } firstCategory)
            {
                _suppressSelection = true;
                SelectedCategory = firstCategory;
                UpdateCategoryFilters(firstCategory);
                _suppressSelection = false;
                await LoadPageAsync(1, false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Diagnostics.Log($"读取首页失败 ({site.Key}): {ex}");
            if (!token.IsCancellationRequested)
            {
                DisplayHome(site, new CmsResponse());
                StatusMessage = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }
                    ? "该站点拒绝访问（403）" : "片源暂不可用";
            }
        }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    private async Task<(CmsResponse? Response, HttpStatusCode? Status)> ReadHomeResultAsync(SourceSite site, bool allowFallback, CancellationToken token)
    {
        try { return (await TryReadHomeAsync(site, allowFallback, token), null); }
        catch (SiteVerificationRequiredException ex)
        {
            PendingVerification = ex;
            Diagnostics.Log($"读取首页需要验证 ({site.Key}): {ex.Kind}");
            return (null, HttpStatusCode.Forbidden);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or NotSupportedException)
        {
            Diagnostics.Log($"读取首页失败 ({site.Key}): {ex.Message}");
            return (null, (ex as HttpRequestException)?.StatusCode);
        }
    }

    private async Task<IReadOnlyList<VodItem>> ReadTrendingSafeAsync(CancellationToken token)
    {
        try { return await _services.Douban.GetTrendingAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Log($"推荐内容不可用: {ex.GetType().Name}");
            return [];
        }
    }

    private void DisplayHome(SourceSite site, CmsResponse response, bool fromDouban = false)
    {
        _suppressSelection = true;
        SelectedSite = site;
        _siteFilters = response.Filters;
        Categories.Clear();
        if (response.List.Count > 0 || response.Categories.Count > 0)
            Categories.Add(new VodCategory
            {
                Id = string.Empty,
                Name = fromDouban ? "豆瓣推荐" : response.HomeCategoryName is { Length: > 0 } name ? name : "推荐"
            });
        foreach (var category in response.Categories) Categories.Add(category);
        SelectedCategory = Categories.FirstOrDefault();
        UpdateCategoryFilters(SelectedCategory);
        _suppressSelection = false;
        ReplaceItems(response.List);
        _page = 1;
        _pageCount = Math.Max(1, response.PageCount);
        HasMore = response.PageCount > 1;
        StatusMessage = Items.Count > 0 ? (fromDouban ? "豆瓣推荐" : site.Name) + $" · {Items.Count} 个结果" : "暂无推荐";
        if (Items.Count > 0) Diagnostics.Log($"Vod home ready: {site.Key}, {Items.Count} items");
    }

    private IEnumerable<SourceSite> AlternateHomeSites()
    {
        foreach (var api in new[] { "csp_BttwooGuard", "csp_AueteGuard", "csp_Dm84Guard" })
            if (Sites.FirstOrDefault(site => site.IsSupported && site.Api.Equals(api, StringComparison.OrdinalIgnoreCase)) is { } site)
                yield return site;
        foreach (var site in Sites.Where(site => site.IsSupported && site.Type is 0 or 1 or 4))
            yield return site;
    }

    private async Task<CmsResponse?> TryReadHomeAsync(SourceSite site, bool startup, CancellationToken token, TimeSpan? timeout = null)
    {
        if (!startup) return await _services.Vod.GetHomeAsync(site, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // 首页候选站点可能较慢（比特首屏实测 >5s），启动路径给足时间，避免误判失败后回退豆瓣；
        // 真正的“快速可用”由上一次成功首页的本地缓存保证。
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        try { return await _services.Vod.GetHomeAsync(site, deadline.Token); }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Diagnostics.Log($"首页候选不可用 ({site.Key}): {ex.Message}");
            return null;
        }
    }

    private async Task<(SourceSite Site, CmsResponse Response)?> FindAvailableHomeAsync(SourceSite preferred, CancellationToken token)
    {
        using var remaining = CancellationTokenSource.CreateLinkedTokenSource(token);
        var candidates = AlternateHomeSites().Where(site => site.Key != preferred.Key).Take(3)
            .Select(async site => (Site: site, Response: await TryReadHomeAsync(site, true, remaining.Token, TimeSpan.FromSeconds(5)))).ToList();
        try
        {
            while (candidates.Count > 0)
            {
                var completed = await Task.WhenAny(candidates);
                candidates.Remove(completed);
                var (site, response) = await completed;
                if (response is { List.Count: > 0 }) return (site, response);
            }
            return null;
        }
        finally { await remaining.CancelAsync(); }
    }

    private async Task<(SourceSite Site, CmsResponse Response, bool IsFresh)?> ReadHomeCacheAsync(SourceSite site, bool includeAlternates, CancellationToken token)
    {
        // 首选当前站点的新鲜缓存；过期缓存也保留作为“站点失败时的兜底”。
        // includeAlternates=true（启动回退路径）时才允许读取备选站点的缓存。
        (SourceSite Site, CmsResponse Response, bool IsFresh)? stale = null;
        var candidates = includeAlternates
            ? new[] { site }.Concat(AlternateHomeSites().Where(other => other.Key != site.Key))
            : new[] { site };
        foreach (var candidate in candidates)
        {
            try
            {
                var entry = await _services.Database.GetMetadataAsync("vod.home:" + candidate.Key, token);
                if (entry is null ||
                    JsonSerializer.Deserialize<CmsResponse>(entry.Json, JsonUtil.Options) is not { List.Count: > 0 } cached) continue;
                var fresh = entry.ExpiresAt > DateTimeOffset.UtcNow;
                if (fresh) return (candidate, cached, true);
                stale ??= (candidate, cached, false);
            }
            catch (JsonException) { }
        }
        return stale;
    }

    private async Task SaveHomeCacheAsync(SourceSite site, CmsResponse response, CancellationToken token)
    {
        try
        {
            var cached = new CmsResponse { Categories = response.Categories, List = response.List.Take(80).ToList(),
                PageCount = response.PageCount, Filters = response.Filters, HomeCategoryName = response.HomeCategoryName };
            await _services.Database.SaveMetadataAsync("vod.home:" + site.Key, JsonSerializer.Serialize(cached, JsonUtil.Options), DateTimeOffset.UtcNow.AddHours(12), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostics.Log($"首页缓存保存失败 ({site.Key}): {ex.Message}"); }
    }

    [RelayCommand]
    private async Task LoadCategoryAsync()
    {
        if (SelectedSite is null || SelectedCategory is null || _suppressSelection) return;
        if (SelectedCategory.Id.Length == 0) { await LoadHomeAsync(); return; }
        BeginRequest();
        await LoadPageAsync(1, false);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMore || SelectedSite is null) return;
        await LoadPageAsync(_page + 1, true);
    }

    private async Task LoadPageAsync(int page, bool append)
    {
        var token = _cts.Token;
        IsLoading = true;
        PendingVerification = null;
        try
        {
            var filters = HasCategoryFilters ? CategoryFilters.Where(filter => !string.IsNullOrEmpty(filter.Selected.Value))
                .ToDictionary(filter => filter.Key, filter => filter.Selected.Value, StringComparer.Ordinal) : null;
            var category = SelectedCategory;
            var response = await _services.Vod.GetCategoryAsync(SelectedSite!, category?.Id ?? string.Empty, page, filters, token);
            token.ThrowIfCancellationRequested();
            if (!append && !HasCategoryFilters && category is not null && response.Filters.ValueKind == JsonValueKind.Object &&
                response.Filters.TryGetProperty(category.Id, out _))
                UpdateCategoryFilters(category, response.Filters);
            if (!append) _browseCards.Clear();
            var newCards = response.List.Select(item => new VodCardViewModel(item, _services)).ToList();
            _browseCards.AddRange(newCards);
            if (append && SortIndex == 0)
                AppendBrowseCards(newCards);
            else
                ApplyBrowseFilters();
            _page = page;
            _pageCount = Math.Max(1, response.PageCount);
            HasMore = response.PageCount > page || (response.PageCount == 0 && response.List.Count > 0);
            var count = response.Total > 0 ? $"已加载 {_browseCards.Count} / {response.Total} 项" : $"共 {_browseCards.Count} 项";
            StatusMessage = Items.Count > 0 ? $"第 {_page} / {Math.Max(_page, _pageCount)} 页 · {count}" : "该分类暂时没有内容";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SiteVerificationRequiredException ex)
        {
            if (!token.IsCancellationRequested)
            {
                PendingVerification = ex;
                StatusMessage = ex.Message;
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusMessage = ex.Message; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    private async Task SelectDetailAsync(VodCardViewModel? card)
    {
        var previous = _detailCts;
        _detailCts = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _detailCts.Token;
        if (card is null)
        {
            IsDetailLoading = false;
            Detail = null;
            return;
        }

        var site = Sites.FirstOrDefault(item => item.Key == card.Item.SiteKey);
        IsDetailLoading = true;
        DetailStatus = string.Empty;
        if (!IsDownloading) DownloadStatus = string.Empty;
        Detail = card.Item.Copy();
        Lines.Clear();
        Episodes.Clear();
        IsIntroExpanded = false;
        CurrentEpisode = null;
        PrimaryPlayText = "播放";
        _resumePositionMs = 0;
        DetailRequested?.Invoke(this, EventArgs.Empty);
        _ = card.LoadVisualAsync(true);
        try
        {
            var detail = card.Item.Id.StartsWith("msearch:", StringComparison.OrdinalIgnoreCase)
                ? await ResolveMetaDetailAsync(card.Item, token)
                : site is null ? card.Item : await ReadDetailWithFallbackAsync(site, card, token);
            detail = await _services.Cloud.ExpandAsync(detail, token);
            if (token.IsCancellationRequested || !ReferenceEquals(SelectedCard, card)) return;
            card.ReplaceItem(detail);
            Detail = detail;
            Lines.Clear();
            foreach (var line in PlaybackParsing.ParseLines(detail)) Lines.Add(line);
            _suppressSelection = true;
            SelectedLine = Lines.FirstOrDefault();
            _suppressSelection = false;
            UpdateEpisodes();
            await RestoreHistoryAsync(detail, token);
            IsFavorite = await _services.Database.IsFavoriteAsync(DatabaseKey(detail), token);
            if (Lines.Count == 0) DetailStatus = "详情已加载，但没有可播放的线路。";
            // PCbox 聚合：进入详情后若"片源"下拉只有一个来源，后台自动检索其他站点的同名资源，
            // 填充 MatchedSources，使片源下拉可切换搜索结果中的其他线路。检测随详情取消而中止。
            if (Lines.Count > 0 && DetailSources.Count <= 1 && !token.IsCancellationRequested &&
                ReferenceEquals(SelectedCard, card))
                _ = AutoAggregateSourcesAsync(card, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) DetailStatus = ex.Message; }
        finally
        {
            if (ReferenceEquals(SelectedCard, card)) IsDetailLoading = false;
        }
    }

    public void OpenFavorite(VodItem item) => SelectedCard = new VodCardViewModel(item, _services);

    private async Task RestoreHistoryAsync(VodItem detail, CancellationToken token)
    {
        CurrentEpisode = Episodes.FirstOrDefault();
        var entry = await _services.Database.GetHistoryEntryAsync(DatabaseKey(detail), token);
        if (entry is null || entry.IsCompleted) return;
        PlaybackRequest? saved;
        try { saved = JsonSerializer.Deserialize<PlaybackRequest>(entry.RequestJson, JsonUtil.Options); }
        catch (JsonException ex)
        {
            Diagnostics.Log($"播放记录格式无效: {ex.GetType().Name}");
            return;
        }
        if (saved is null) return;
        if (Lines.ElementAtOrDefault(saved.LineIndex) is { } line && !ReferenceEquals(SelectedLine, line))
        {
            _suppressSelection = true;
            SelectedLine = line;
            _suppressSelection = false;
            UpdateEpisodes();
        }
        CurrentEpisode = Episodes.ElementAtOrDefault(saved.EpisodeIndex) ?? Episodes.FirstOrDefault();
        _resumePositionMs = Math.Max(0, entry.PositionMs);
        PrimaryPlayText = "继续观看";
    }

    private async Task<VodItem> ReadDetailWithFallbackAsync(SourceSite site, VodCardViewModel card, CancellationToken token)
    {
        try { return await _services.Vod.GetDetailAsync(site, card.Item.Id, token); }
        catch (Exception ex) when (ex is not OperationCanceledException && card.Item.PlayUrl.Length > 0)
        {
            return card.Item.Copy();
        }
    }

    private async Task<VodItem> ResolveMetaDetailAsync(VodItem source, CancellationToken token)
    {
        var sites = Sites.Where(site => site.IsSupported && site.Searchable != 0 && !site.Api.Contains("Douban", StringComparison.OrdinalIgnoreCase) && !site.Api.Contains("DouDou", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(site => site.Key == source.SiteKey).Take(8).ToList();
        var collected = new System.Collections.Concurrent.ConcurrentBag<(VodItem Item, SourceSite Site, double Score)>();
        await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, async (site, cancellationToken) =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(7));
                var response = await _services.Vod.SearchAsync(site, source.Name, 1, deadline.Token);
                foreach (var item in response.List.Take(8)) collected.Add((item, site, Services.Vod.SearchScoring.Score(source.Name, item)));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { Diagnostics.Log($"片单搜索 {site.Name}: {ex.Message}"); }
        });
        var candidates = collected.Where(candidate => candidate.Score >= 400).OrderByDescending(candidate => candidate.Score).Take(4);
        foreach (var candidate in candidates)
        {
            try
            {
                var detail = await _services.Vod.GetDetailAsync(candidate.Site, candidate.Item.Id, token);
                if (string.IsNullOrWhiteSpace(detail.PlayUrl)) continue;
                if (string.IsNullOrWhiteSpace(detail.Picture)) detail.Picture = source.BestPicture;
                if (string.IsNullOrWhiteSpace(detail.Content)) detail.Content = source.Description;
                return detail;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Log($"片单详情 {candidate.Site.Name}: {ex.Message}"); }
        }
        throw new InvalidDataException("片库已找到条目，但当前站点没有匹配的可播放线路。");
    }

    partial void OnTypeFilterChanged(string value) => ApplyBrowseFilters();
    partial void OnYearFilterChanged(string value) => ApplyBrowseFilters();
    partial void OnSortIndexChanged(int value) => ApplyBrowseFilters();

    private void AppendBrowseCards(IEnumerable<VodCardViewModel> newCards)
    {
        var added = new List<VodCardViewModel>();
        if (HasCategoryFilters)
        {
            foreach (var card in newCards) { Items.Add(card); added.Add(card); }
            _services.Images.Prefetch(added.Select(c => c.Item.BestPicture), maxCount: 16);
            return;
        }
        var year = YearFilter.Trim();
        foreach (var card in newCards)
        {
            if (Services.Vod.SearchScoring.MatchesType(card.Item, TypeFilter) &&
                (year.Length == 0 || card.Item.Year.Contains(year, StringComparison.Ordinal)))
            {
                Items.Add(card);
                added.Add(card);
            }
        }
        if (added.Count > 0) _services.Images.Prefetch(added.Select(c => c.Item.BestPicture), maxCount: 16);
    }

    private void ApplyBrowseFilters()
    {
        if (HasCategoryFilters)
        {
            ReplaceCards(_browseCards);
            return;
        }
        var year = YearFilter.Trim();
        var filtered = _browseCards
            .Where(card => Services.Vod.SearchScoring.MatchesType(card.Item, TypeFilter))
            .Where(card => year.Length == 0 || card.Item.Year.Contains(year, StringComparison.Ordinal));
        ReplaceCards(SortIndex == 1 ? filtered.OrderByDescending(card => card.Item.Year) : filtered);
    }

    partial void OnSelectedSiteChanged(SourceSite? value)
    {
        if (_initialized && !_suppressSelection && value is not null)
        {
            if (Services.Vod.Native.MyDriveVodProvider.Supports(value))
            {
                _suppressSelection = true;
                SelectedSite = _lastContentSite is { } previous && Sites.Contains(previous) ? previous : AlternateHomeSites().FirstOrDefault();
                _suppressSelection = false;
                App.Window.Navigate("cloud");
                return;
            }
            _lastContentSite = value;
            _siteFilters = default;
            UpdateCategoryFilters(null);
            _ = _services.Database.SetSettingAsync("source.homeSite", value.Key);
            _ = LoadHomeAsync();
        }
    }

    partial void OnSelectedCategoryChanged(VodCategory? value)
    {
        foreach (var category in Categories) category.IsSelected = ReferenceEquals(category, value);
        if (!_suppressSelection) UpdateCategoryFilters(value);
        if (_initialized && !_suppressSelection && value is not null) _ = LoadCategoryAsync();
    }

    private void UpdateCategoryFilters(VodCategory? category, JsonElement categoryFilters = default)
    {
        CategoryFilters.Clear();
        var available = categoryFilters.ValueKind == JsonValueKind.Object ? categoryFilters : _siteFilters;
        if (category is not null && SelectedSite is { } site && _services.Vod.CanFilterCategory(site) &&
            available.ValueKind == JsonValueKind.Object && available.TryGetProperty(category.Id, out var definitions) &&
            definitions.ValueKind == JsonValueKind.Array)
        {
            try
            {
                var fields = definitions.Deserialize<List<VodFilterDefinition>>(JsonUtil.Options) ?? [];
                foreach (var field in fields.Where(field => field is not null && !string.IsNullOrWhiteSpace(field.Key))
                             .DistinctBy(field => field.Key, StringComparer.Ordinal))
                {
                    var choices = field.Values?.Where(value => value is not null && !string.IsNullOrWhiteSpace(value.Name))
                        .DistinctBy(value => value.Value, StringComparer.Ordinal).ToArray() ?? [];
                    if (choices.Length == 0) continue;
                    CategoryFilters.Add(new VodCategoryFilterChoice(field.Key, field.Name, choices, () =>
                    {
                        if (!_suppressSelection && ReferenceEquals(category, SelectedCategory) && ReferenceEquals(site, SelectedSite))
                            _ = LoadCategoryAsync();
                    }));
                }
            }
            catch (JsonException) { Diagnostics.Log($"分类筛选格式无效 ({site.Key})"); }
        }
        OnPropertyChanged(nameof(HasCategoryFilters));
    }

    partial void OnSelectedCardChanged(VodCardViewModel? value) => _ = SelectDetailAsync(value);

    partial void OnSelectedLineChanged(PlaybackLine? value)
    {
        if (!_suppressSelection)
        {
            _resumePositionMs = 0;
            PrimaryPlayText = "播放";
            UpdateEpisodes();
            CurrentEpisode = Episodes.FirstOrDefault();
        }
    }

    private void UpdateEpisodes()
    {
        Episodes.Clear();
        if (SelectedLine is not null)
            foreach (var episode in SelectedLine.Episodes.Where(episode => string.IsNullOrWhiteSpace(EpisodeFilter) ||
                episode.Name.Contains(EpisodeFilter.Trim(), StringComparison.CurrentCultureIgnoreCase))) Episodes.Add(episode);
    }

    [RelayCommand]
    private void PlayEpisode(Episode? episode)
    {
        if (CreatePlaybackRequest(episode) is { } request) _play(request);
    }

    private PlaybackRequest? CreatePlaybackRequest(Episode? episode)
    {
        if (episode is null || Detail is null) return null;
        var site = Sites.FirstOrDefault(item => item.Key == Detail.SiteKey);
        var lineIndex = Math.Max(0, Lines.IndexOf(SelectedLine!));
        var episodeIndex = Math.Max(0, SelectedLine?.Episodes.IndexOf(episode) ?? 0);
        return new PlaybackRequest
        {
            Kind = PlaybackKind.Vod, Title = Detail.Name, Subtitle = $"{Detail.SiteName} · {SelectedLine?.Name} · {episode.Name}",
            Url = episode.Url, SiteKey = Detail.SiteKey, VodId = Detail.Id, Picture = Detail.BestPicture,
            StartPositionMs = ReferenceEquals(episode, CurrentEpisode) ? _resumePositionMs : 0,
            Item = Detail, Lines = Lines.ToList(), LineIndex = lineIndex, EpisodeIndex = episodeIndex,
            Headers = site?.GetHeaders().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveEpisodeAsync(Episode? episode, CancellationToken token)
    {
        if (episode is null || IsDownloading || CreatePlaybackRequest(episode) is not { } request) return;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = "正在解析下载地址…";
        PlaybackRequest? media = null;
        try
        {
            media = await _services.Playback.ResolveForDownloadAsync(request, token);
            var extension = await _services.Downloads.GetExtensionAsync(media, token);
            var title = episode.Name;
            if (Path.GetExtension(title).Equals(extension, StringComparison.OrdinalIgnoreCase))
                title = Path.GetFileNameWithoutExtension(title);
            var invalid = Path.GetInvalidFileNameChars();
            var filename = string.Concat($"{request.Title} · {title}".Select(c => invalid.Contains(c) ? '_' : c));
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.Window.AppWindow.Id)
            {
                SuggestedFileName = filename[..Math.Min(filename.Length, 100)].TrimEnd('.', ' '),
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary
            };
            picker.FileTypeChoices.Add(extension == ".m3u8" ? "离线 HLS 播放列表" : "媒体文件", [extension]);
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) { DownloadStatus = string.Empty; return; }
            token.ThrowIfCancellationRequested();
            DownloadStatus = $"正在下载 {episode.Name}";
            await _services.Downloads.SaveAsync(media, destination.Path, new Progress<double>(value => DownloadProgress = value), token);
            await _services.Database.SaveDownloadAsync(new DownloadEntry(destination.Path, request.Title, episode.Name), token);
            DownloadStatus = "已保存到媒体库";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { DownloadStatus = "下载已取消"; }
        catch (Exception ex) { DownloadStatus = "下载失败：" + ex.Message; }
        finally
        {
            if (media is not null) _services.Cloud.ReleaseMedia(media.Url);
            IsDownloading = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveEpisodesAsync(IList<Episode>? episodes, CancellationToken token)
    {
        if (episodes is null || episodes.Count == 0 || Detail is null || IsDownloading) return;
        var requests = new List<(Episode Episode, PlaybackRequest Request)>();
        foreach (var episode in episodes)
            if (CreatePlaybackRequest(episode) is { } request) requests.Add((episode, request));
        if (requests.Count == 0) return;
        IsDownloading = true;
        DownloadProgress = 0;
        var completed = 0;
        var failed = new List<string>();
        try
        {
            var configured = await _services.Database.GetSettingAsync("downloads.defaultFolder", token);
            var directory = string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultDownloads : configured;
            Directory.CreateDirectory(directory);
            for (var index = 0; index < requests.Count; index++)
            {
                var (episode, request) = requests[index];
                DownloadStatus = $"正在解析 {index + 1}/{requests.Count}：{episode.Name}";
                PlaybackRequest? media = null;
                try
                {
                    media = await _services.Playback.ResolveForDownloadAsync(request, token);
                    var extension = await _services.Downloads.GetExtensionAsync(media, token);
                    var filename = DownloadFileName(request, episode, extension);
                    var destination = AvailableDownloadPath(Path.Combine(directory, filename + extension));
                    DownloadStatus = $"正在下载 {index + 1}/{requests.Count}：{episode.Name}";
                    await _services.Downloads.SaveAsync(media, destination,
                        new Progress<double>(value => DownloadProgress = (index + value / 100d) * 100d / requests.Count), token);
                    await _services.Database.SaveDownloadAsync(new DownloadEntry(destination, request.Title, episode.Name), token);
                    completed++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed.Add($"{episode.Name}：{ex.Message}"); }
                finally
                {
                    if (media is not null) _services.Cloud.ReleaseMedia(media.Url);
                }
            }
            DownloadProgress = (double)completed / requests.Count * 100;
            DownloadStatus = $"已完成 {completed}/{requests.Count} 集，保存到 {directory}" +
                (failed.Count > 0 ? $"\n{failed.Count} 集失败：{string.Join("；", failed.Take(3))}" : string.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DownloadStatus = $"下载已取消，已完成的 {completed} 集保留在媒体库。";
        }
        catch (Exception ex) { DownloadStatus = "下载目录不可用：" + ex.Message; }
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        SaveEpisodeCommand.Cancel();
        SaveEpisodesCommand.Cancel();
    }

    private static string DownloadFileName(PlaybackRequest request, Episode episode, string extension)
    {
        var title = Path.GetExtension(episode.Name).Equals(extension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(episode.Name) : episode.Name;
        var invalid = Path.GetInvalidFileNameChars();
        var filename = string.Concat($"{request.Title} · {title}".Select(c => invalid.Contains(c) ? '_' : c));
        return filename[..Math.Min(filename.Length, 100)].TrimEnd('.', ' ');
    }

    private static string AvailableDownloadPath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("无法为下载文件分配不重复的文件名。");
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Detail is not { } detail) return;
        var desired = !IsFavorite;
        IsFavorite = desired;
        try
        {
            await _services.Database.SetFavoriteAsync(DatabaseKey(detail), detail.Name, detail.BestPicture, "Vod", detail.SiteName,
                System.Text.Json.JsonSerializer.Serialize(detail, JsonUtil.Options), desired);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Detail, detail)) IsFavorite = !desired;
            DetailStatus = "收藏保存失败：" + ex.Message;
        }
    }

    [ObservableProperty] private bool _isIntroExpanded;
    public bool CanExpandIntro => (Detail?.Description?.Length ?? 0) > 120;

    [RelayCommand]
    private void ToggleIntro() => IsIntroExpanded = !IsIntroExpanded;

    private static string DatabaseKey(VodItem item) => TV.Services.Persistence.DatabaseService.GetVodId(item.SiteKey, item.Id);

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!_initialized || IsLoading || _services.Config.Current is not { } config) return;
            var selectedKey = SelectedSite?.Key;
            _suppressSelection = true;
            Sites.Clear();
            foreach (var site in config.Sites) Sites.Add(site);
            SelectedSite = Sites.FirstOrDefault(site => site.Key == selectedKey && site.IsSupported)
                ?? Sites.FirstOrDefault(site => site.Api.Equals("csp_Dm84Guard", StringComparison.OrdinalIgnoreCase))
                ?? Sites.FirstOrDefault(site => site.IsSupported) ?? Sites.FirstOrDefault();
            _suppressSelection = false;
            if (SelectedSite is not null && SelectedSite.Key != selectedKey) _ = LoadHomeAsync();
        });
    }

    private void ReplaceItems(IEnumerable<VodItem> items)
    {
        _browseCards.Clear();
        _browseCards.AddRange(items.Select(item => new VodCardViewModel(item, _services)));
        ApplyBrowseFilters();
    }

    private void ReplaceCards(IEnumerable<VodCardViewModel> cards)
    {
        Items.Clear();
        GalleryItems.Clear();
        foreach (var card in cards) Items.Add(card);
        foreach (var card in Items.Take(8)) GalleryItems.Add(card);
        OnPropertyChanged(nameof(ShowGallery));
        if (Items.Count > 0)
            _services.Images.Prefetch(Items.Select(c => c.Item.BestPicture), maxCount: 20);
    }

    private CancellationToken BeginRequest()
    {
        var previous = _cts;
        _cts = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        return _cts.Token;
    }

    internal Task? CancelPendingWork()
    {
        _services.Config.Changed -= OnConfigChanged;
        _cts.Cancel();
        _detailCts.Cancel();
        SaveEpisodeCommand.Cancel();
        SaveEpisodesCommand.Cancel();
        var tasks = new[] { SaveEpisodeCommand.ExecutionTask, SaveEpisodesCommand.ExecutionTask }.Where(task => task is not null).ToArray();
        return tasks.Length > 0 ? Task.WhenAll(tasks!) : null;
    }
}
