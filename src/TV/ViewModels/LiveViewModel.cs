using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TV.Models;
using TV.Services;

namespace TV.ViewModels;

/// <summary>直播单条线路：同一频道的多个播放地址聚合后按线路展示，点击直接播放。</summary>
public sealed class LiveChannelLine
{
    public LiveChannelLine(LiveChannel channel, int index)
    {
        Channel = channel;
        Label = $"线路 {index + 1}";
        Quality = DetectQuality(channel);
        Detail = string.IsNullOrWhiteSpace(channel.SourceName) ? channel.Url : channel.SourceName;
    }

    public LiveChannel Channel { get; }
    public string Label { get; }
    public string Quality { get; }
    public string Detail { get; }
    public string Meta => $"{Label} · {Quality}";

    /// <summary>从频道名/地址推断清晰度标签（数据模型没有独立清晰度字段）。</summary>
    private static string DetectQuality(LiveChannel channel)
    {
        var text = $"{channel.Name} {channel.Url}".ToUpperInvariant();
        if (text.Contains("8K")) return "8K";
        if (text.Contains("4K") || text.Contains("UHD")) return "4K";
        if (text.Contains("蓝光")) return "蓝光";
        if (text.Contains("超清") || text.Contains("FHD") || text.Contains("1080")) return "超清";
        if (text.Contains("高清") || text.Contains("HD") || text.Contains("720")) return "高清";
        if (text.Contains("标清") || text.Contains("SD")) return "标清";
        return "默认画质";
    }
}

/// <summary>直播频道聚合项：同名频道的全部真实线路合并为一项，避免列表重复。</summary>
public sealed class LiveChannelItem
{
    public LiveChannelItem(string name, string group, string logo, IReadOnlyList<LiveChannelLine> lines)
    {
        Name = name;
        Group = group;
        Logo = logo;
        Lines = lines;
    }

    public string Name { get; }
    public string Group { get; }
    public string Logo { get; }
    public IReadOnlyList<LiveChannelLine> Lines { get; }
    public LiveChannelLine Primary => Lines[0];
    public int LineCount => Lines.Count;
    public string CountText => LineCount > 1 ? $"{LineCount} 条线路" : "单线路";

    public string QualityText
    {
        get
        {
            var values = new List<string>();
            foreach (var line in Lines)
                if (!values.Contains(line.Quality, StringComparer.CurrentCultureIgnoreCase)) values.Add(line.Quality);
            return string.Join(" / ", values);
        }
    }

    public string MetaText => $"{CountText} · {QualityText}";
}

public sealed partial class LiveViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<PlaybackRequest> _play;
    private readonly List<LiveChannel> _allChannels = [];
    private List<LiveChannelItem> _allGrouped = [];
    private Dictionary<string, List<LiveChannelItem>> _groupedByCategory = new(StringComparer.Ordinal);
    private int _epgVersion;
    private CancellationTokenSource? _load;
    private bool _changingSources;

    public LiveViewModel(AppServices services, Action<PlaybackRequest> play)
    {
        _services = services;
        _play = play;
    }

    public ObservableCollection<string> Groups { get; } = [];

    /// <summary>已按同名频道聚合的频道集合（每项包含多条可点播线路）。</summary>
    public ObservableCollection<LiveChannelItem> Channels { get; } = [];

    [ObservableProperty] private IReadOnlyList<LiveSource> _availableSources = [];
    [ObservableProperty] private LiveSource? _selectedRepository;
    [ObservableProperty] private string? _selectedGroup;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedChannel))] private LiveChannel? _selectedChannel;
    public bool HasSelectedChannel => SelectedChannel is not null;
    [ObservableProperty] private LiveChannelItem? _selectedItem;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EmptyHint))] private string _statusMessage = "直播源将在打开页面时加载";
    [ObservableProperty] private string _programInfo = string.Empty;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _directUrl = string.Empty;

    /// <summary>当前筛选结果是否含可播放频道（决定频道区显示列表还是空状态）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasChannels;

    /// <summary>最近一次加载是否失败（决定空状态文案与重试引导）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyHint))]
    private bool _hasError;

    [ObservableProperty] private string _channelsSummary = string.Empty;

    public bool IsEmpty => !IsLoading && !HasChannels;
    public string EmptyTitle => HasError ? "直播源加载失败" : "没有可播放的频道";
    public string EmptyHint => HasError ? StatusMessage : "试试刷新，或切换到其他直播源与分组";

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ReloadAsync()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = new CancellationTokenSource();
        var token = _load.Token;
        IsLoading = true;
        HasError = false;
        StatusMessage = "正在加载直播频道…";
        try
        {
            var config = await _services.Config.LoadAsync(cancellationToken: token);
            var selectedUrl = SelectedRepository?.Url;
            var custom = await LoadCustomSourcesAsync(token);
            // 远程源 + 用户自定义源（自定义源优先展示、IsBuiltIn=false）
            var fresh = config.Lives.Concat(custom)
                .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(source => source.IsBuiltIn).ToArray();
            if (!AvailableSources.SequenceEqual(fresh))
            {
                var firstImport = AvailableSources.Any(source => !source.IsBuiltIn)
                    ? null : fresh.FirstOrDefault(source => !source.IsBuiltIn);
                _changingSources = true;
                try
                {
                    AvailableSources = fresh;
                    SelectedRepository = fresh.FirstOrDefault(source => source.Url == selectedUrl) ?? firstImport;
                }
                finally { _changingSources = false; }
            }
            var sources = SelectedRepository is { } selected ? new[] { selected } :
                fresh.Where(source => source.IsBuiltIn).ToArray();
            List<LiveChannel> channels = sources.Length > 0 ? await _services.Live.LoadAsync(sources, token) : [];
            token.ThrowIfCancellationRequested();
            _allChannels.Clear();
            _allChannels.AddRange(channels);
            BuildGroupedCache(channels);
            Groups.Clear();
            Groups.Add("全部频道");
            foreach (var group in _groupedByCategory.Keys) Groups.Add(group);
            SelectedGroup = Groups.FirstOrDefault();
            ApplyFilter();
            HasError = false;
            StatusMessage = $"已加载 {Channels.Count} 个频道";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                HasError = true;
                StatusMessage = ex.Message;
                Channels.Clear();
                SelectedItem = null;
                HasChannels = false;
                ChannelsSummary = "没有匹配的频道";
            }
        }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    [RelayCommand]
    private void SelectBuiltIn()
    {
        if (SelectedRepository is null) _ = ReloadAsync();
        else SelectedRepository = null;
    }

    private const string CustomLiveSourcesKey = "custom_live_sources";

    private async Task<List<LiveSource>> LoadCustomSourcesAsync(CancellationToken token)
    {
        try
        {
            var raw = await _services.Database.GetSettingAsync(CustomLiveSourcesKey, token);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            var list = JsonSerializer.Deserialize<List<LiveSource>>(raw, JsonUtil.Options) ?? [];
            foreach (var source in list) source.IsBuiltIn = false;
            return list;
        }
        catch { return []; }
    }

    private async Task SaveCustomSourcesAsync(IReadOnlyList<LiveSource> sources, CancellationToken token = default)
    {
        var raw = JsonSerializer.Serialize(sources, JsonUtil.Options);
        await _services.Database.SetSettingAsync(CustomLiveSourcesKey, raw, token);
    }

    /// <summary>添加一个自定义直播源（m3u/m3u8 订阅地址或单频道播放地址）。返回错误信息，成功返回 null。</summary>
    public async Task<string?> AddCustomSourceAsync(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(name)) return "请输入直播源名称";
        if (string.IsNullOrWhiteSpace(url)) return "请输入直播源地址";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "地址必须是 http/https 开头的完整链接";

        var token = _load?.Token ?? CancellationToken.None;
        var sources = await LoadCustomSourcesAsync(token);
        if (sources.Any(source => string.Equals(source.Url, url.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "该直播源已存在";
        sources.Add(new LiveSource { Name = name.Trim(), Url = url.Trim(), Type = 0, IsBuiltIn = false });
        await SaveCustomSourcesAsync(sources, token);
        await ReloadAsync();
        SelectedRepository = AvailableSources.LastOrDefault(source => !source.IsBuiltIn && source.Url == url.Trim());
        StatusMessage = $"已添加直播源「{name.Trim()}」";
        return null;
    }

    /// <summary>删除一个自定义直播源。</summary>
    public async Task RemoveCustomSourceAsync(LiveSource source)
    {
        if (source is null || source.IsBuiltIn) return;
        var sources = await LoadCustomSourcesAsync(CancellationToken.None);
        sources.RemoveAll(item => string.Equals(item.Url, source.Url, StringComparison.OrdinalIgnoreCase));
        await SaveCustomSourcesAsync(sources);
        await ReloadAsync();
        StatusMessage = $"已删除直播源「{source.Name}」";
    }

    partial void OnSelectedRepositoryChanged(LiveSource? value)
    {
        if (!_changingSources) _ = ReloadAsync();
    }

    private void BuildGroupedCache(IReadOnlyList<LiveChannel> channels)
    {
        static List<LiveChannelItem> GroupItems(IEnumerable<LiveChannel> source)
        {
            var result = new List<LiveChannelItem>();
            foreach (var grouped in source.GroupBy(ChannelName, StringComparer.CurrentCultureIgnoreCase))
            {
                var lines = grouped
                    .GroupBy(channel => channel.Url, StringComparer.Ordinal)
                    .Select(line => line.First())
                    .Select((channel, index) => new LiveChannelLine(channel, index))
                    .ToList();
                if (lines.Count == 0) continue;
                var first = grouped.First();
                result.Add(new LiveChannelItem(grouped.Key, first.Group, first.Logo, lines));
            }
            return result;
        }

        _allGrouped = GroupItems(channels);
        _groupedByCategory = new Dictionary<string, List<LiveChannelItem>>(StringComparer.Ordinal);
        foreach (var cat in channels.GroupBy(c => c.Group, StringComparer.Ordinal))
            _groupedByCategory[cat.Key] = GroupItems(cat);
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        var selectedName = SelectedItem?.Name;
        Channels.Clear();
        var keyword = SearchText.Trim();
        IReadOnlyList<LiveChannelItem> pool = SelectedGroup is null or "全部频道"
            ? _allGrouped
            : (_groupedByCategory.TryGetValue(SelectedGroup, out var catList) ? catList : []);

        foreach (var item in pool)
        {
            if (keyword.Length == 0 || item.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                Channels.Add(item);
        }
        SelectedItem = selectedName is null
            ? null
            : Channels.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        HasChannels = Channels.Count > 0;
        ChannelsSummary = Channels.Count == 0 ? "没有匹配的频道" : $"共 {Channels.Count} 个频道";
    }

    private static string ChannelName(LiveChannel channel) =>
        string.IsNullOrWhiteSpace(channel.Name) ? "未命名频道" : channel.Name.Trim();

    partial void OnSelectedItemChanged(LiveChannelItem? value) => SelectedChannel = value?.Primary.Channel;

    partial void OnSelectedGroupChanged(string? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedChannelChanged(LiveChannel? value)
    {
        ProgramInfo = string.Empty;
        var version = ++_epgVersion;
        if (value is not null)
        {
            _ = UpdateFavoriteAsync(value, version);
            _ = LoadEpgAsync(value, version);
        }
    }

    private async Task LoadEpgAsync(LiveChannel channel, int version)
    {
        if (string.IsNullOrWhiteSpace(channel.EpgUrl)) return;
        ProgramInfo = $"{channel.Group} · {channel.Name}\n正在读取节目单…";
        try
        {
            var program = await _services.Epg.GetCurrentProgramAsync(channel);
            if (version == _epgVersion && program is not null) ProgramInfo = $"{channel.Name}\n{program}";
        }
        catch (Exception ex)
        {
            if (version == _epgVersion) ProgramInfo = $"{channel.Group} · {channel.Name}\n节目单暂不可用：{ex.Message}";
        }
    }

    [RelayCommand]
    private void PlayChannel(LiveChannel? channel)
    {
        channel ??= SelectedChannel;
        if (channel is null) return;
        _play(new PlaybackRequest
        {
            Kind = PlaybackKind.Live, Title = channel.Name, Subtitle = $"{channel.Group} · 直播",
            Url = channel.Url, Picture = channel.Logo, Channel = channel, Headers = channel.Headers
        });
    }

    [RelayCommand]
    private void PlayDirect()
    {
        if (!Uri.TryCreate(DirectUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "rtmp" or "rtsp")) return;
        var channel = new LiveChannel { Name = "网络串流", Group = uri.Host, Url = uri.ToString() };
        _play(new PlaybackRequest { Kind = PlaybackKind.Live, Title = channel.Name, Subtitle = channel.Group, Url = channel.Url, Channel = channel });
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedChannel is not { } channel) return;
        var desired = !IsFavorite;
        IsFavorite = desired;
        try
        {
            await _services.Database.SetFavoriteAsync(TV.Services.Persistence.DatabaseService.GetLiveId(channel.Url), channel.Name,
                channel.Logo, "Live", channel.Group, System.Text.Json.JsonSerializer.Serialize(channel, JsonUtil.Options), desired);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedChannel, channel)) IsFavorite = !desired;
            StatusMessage = "收藏保存失败：" + ex.Message;
        }
    }

    private async Task UpdateFavoriteAsync(LiveChannel channel, int version)
    {
        try
        {
            var favorite = await _services.Database.IsFavoriteAsync(TV.Services.Persistence.DatabaseService.GetLiveId(channel.Url));
            if (version == _epgVersion && ReferenceEquals(SelectedChannel, channel)) IsFavorite = favorite;
        }
        catch (Exception ex) { Diagnostics.Log($"直播收藏读取失败: {ex.GetType().Name}"); }
    }
}
