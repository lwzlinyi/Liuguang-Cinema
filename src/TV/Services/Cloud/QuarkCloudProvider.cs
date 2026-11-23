using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TV.Services.Persistence;
using TV.Models;

namespace TV.Services.Cloud;

public sealed class QuarkCloudProvider : ICloudDriveProvider
{
    private readonly CloudHttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _credential;
    private readonly DatabaseService _database;
    private readonly string _api;
    private readonly string _home;
    private readonly CloudShareTokenCache _tokens = new();
    private readonly ConcurrentDictionary<string, string> _savedIds = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly CloudCookieJar _jar = new();
    private readonly SemaphoreSlim _jarLock = new(1, 1);
    private string _seed = string.Empty;
    private string _persisted = string.Empty;
    private readonly Dictionary<string, string> _saveDirIds = new(StringComparer.Ordinal);

    public QuarkCloudProvider(CloudHttpClient http, CloudProviderKind kind, Func<CancellationToken, Task<string>> credential, DatabaseService database)
    {
        _http = http;
        Kind = kind;
        _credential = credential;
        _database = database;
        _api = kind == CloudProviderKind.UC ? "https://pc-api.uc.cn/1/clouddrive/" : "https://drive-pc.quark.cn/1/clouddrive/";
        _home = kind == CloudProviderKind.UC ? "https://drive.uc.cn/" : "https://pan.quark.cn/";
    }

    public CloudProviderKind Kind { get; }

    private string ClientQuery => Kind == CloudProviderKind.UC ? "pr=UCBrowser&fr=pc" : "pr=ucpro&fr=pc";
    public bool CanHandle(string url) => CloudShareLink.TryParse(url, out var link) && link.Provider == Kind;

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!CloudShareLink.TryParse(url, out var link) || link.Provider != Kind)
            throw new ArgumentException("网盘分享链接格式无效。", nameof(url));
        var token = await GetShareTokenAsync(link.ShareId, link.Passcode, cancellationToken).ConfigureAwait(false);
        var files = new List<CloudFile>();
        var subtitles = new List<CloudFile>();
        await ListFilesAsync(link, "0", token, string.Empty, files, subtitles, 0, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0) throw new InvalidDataException($"{Kind} 分享中没有可播放的媒体文件。");
        return new CloudShare { Provider = Kind, ShareId = link.ShareId, SourceUrl = url, Name = $"{Kind} 分享",
            Files = files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), SubtitleFiles = subtitles };
    }

    private Task<string> GetShareTokenAsync(string shareId, string passcode, CancellationToken cancellationToken) =>
        _tokens.GetAsync(shareId, passcode, async () =>
        {
            var result = await SendAsync(HttpMethod.Post, _api + "share/sharepage/token?" + ClientQuery, new { pwd_id = shareId, passcode }, string.Empty, CancellationToken.None).ConfigureAwait(false);
            EnsureSuccess(result, "分享令牌获取失败");
            var token = NestedString(result.Json, "data", "stoken");
            if (token.Length == 0) throw new InvalidDataException("分享不存在、已失效或需要提取码。");
            return token;
        }, cancellationToken);

    private async Task ListFilesAsync(CloudShareLink link, string folderId, string token, string parentName, List<CloudFile> files,
        List<CloudFile> subtitles, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8 || files.Count >= 500) return;
        var page = 1;
        while (page <= 50 && files.Count < 500)
        {
            var url = _api + "share/sharepage/detail?" + ClientQuery + "&pwd_id=" + link.ShareId + "&stoken=" + Uri.EscapeDataString(token) + "&pdir_fid=" + Uri.EscapeDataString(folderId) + "&force=0&_page=" + page + "&_size=200&_sort=file_type:asc,file_name:asc";
            var result = await SendAsync(HttpMethod.Get, url, null, string.Empty, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "文件列表读取失败");
            var data = Property(result.Json, "data");
            var list = Property(data, "list");
            if (list.ValueKind != JsonValueKind.Array) break;
            var folders = new List<(string Id, string Name)>();
            foreach (var item in list.EnumerateArray())
            {
                var id = String(item, "fid");
                var name = String(item, "file_name");
                if (!CloudShareLink.IsIdentifier(id)) continue;
                if (Bool(item, "dir")) folders.Add((id, name));
                else if (CloudSubtitleFiles.IsSubtitle(name))
                {
                    if (subtitles.Count < 1000)
                        subtitles.Add(new CloudFile(Kind, link.ShareId, id, String(item, "share_fid_token"), name, Long(item, "size"), parentName, link.Passcode, ParentDirectoryId: folderId));
                }
                else if (IsMedia(item, name) && files.Count < 500)
                    files.Add(new CloudFile(Kind, link.ShareId, id, String(item, "share_fid_token"), name, Long(item, "size"), parentName, link.Passcode, ParentDirectoryId: folderId));
            }
            foreach (var folder in folders) await ListFilesAsync(link, folder.Id, token, folder.Name, files, subtitles, depth + 1, cancellationToken).ConfigureAwait(false);
            var metadata = Property(data, "metadata");
            var total = Long(metadata, "_total");
            if (total <= 0) total = Long(metadata, "total");
            if (list.GetArrayLength() < 200 || total > 0 && page * 200 >= total) break;
            page++;
        }
    }

    public Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default)
    {
        var height = quality.Contains("原画", StringComparison.OrdinalIgnoreCase) ? -1 :
            quality.EndsWith('p') && int.TryParse(quality.AsSpan(0, quality.Length - 1), out var resolution) ? resolution : 0;
        return PlayAsync(file, new QualityOption(quality, height), includeOriginalAlternatives: false, cancellationToken);
    }

    public Task<CloudPlayResult> PlayAsync(CloudFile file, QualityOption quality, CancellationToken cancellationToken = default) =>
        PlayAsync(file, quality, includeOriginalAlternatives: true, cancellationToken);

    private async Task<CloudPlayResult> PlayAsync(CloudFile file, QualityOption quality, bool includeOriginalAlternatives,
        CancellationToken cancellationToken)
    {
        var cookie = await _credential(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cookie)) throw new CloudAuthenticationException(Kind, $"{Kind} 转码与原画需要扫码登录；公开分享浏览无需登录。");
        var savedId = file.ShareId == "drive" ? file.FileId : await SaveFileAsync(file, cookie, cancellationToken).ConfigureAwait(false);
        if (quality.RequestsOriginal)
        {
            var otherQualities = includeOriginalAlternatives ? ReadOptionalTranscodesAsync(savedId, cookie, cancellationToken) : null;
            var original = await GetDownloadUrlAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
            var alternatives = otherQualities is null ? [] : await otherQualities.ConfigureAwait(false);
            if (original.Length == 0) throw new InvalidDataException($"{Kind} 未返回播放地址，请更换清晰度或重试。");
            var contentType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);
            return new CloudPlayResult(original, Headers(cookie), contentType, CloudSubtitleFiles.Extension(file.Name))
            {
                AvailableQualities = includeOriginalAlternatives ? TranscodingOptions(alternatives) : null
            };
        }
        var variants = await ReadTranscodesAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
        if (variants.Length == 0) throw new InvalidDataException($"{Kind} 未返回播放地址，请更换清晰度或重试。");
        var transcoded = variants.Where(item => item.Format.Matches(quality)).OrderByDescending(item => item.Format.Height)
            .ThenByDescending(item => item.Format.FrameRate).FirstOrDefault()
            ?? throw new NotSupportedException($"网盘未提供 {quality.Name}。");
        var options = TranscodingOptions(variants);
        return new CloudPlayResult(transcoded.Url, Headers(cookie), FileExtension: CloudSubtitleFiles.Extension(file.Name))
        {
            AvailableVideoHeights = variants.Where(item => item.Format.Height > 0).Select(item => item.Format.Height).ToHashSet(),
            AvailableQualities = options,
            ResolvedQuality = options.FirstOrDefault(option => !option.RequestsOriginal && transcoded.Format.Matches(option))
        };
    }

    private async Task<string> SaveFileAsync(CloudFile file, string cookie, CancellationToken cancellationToken)
    {
        var account = AccountKey(cookie);
        var key = $"{account}/{file.ShareId}/{file.FileId}";
        if (_savedIds.TryGetValue(key, out var cached)) return cached;
        var settingKey = $"cloud.saved.{Kind}.{key}";
        var stored = await _database.GetSettingAsync(settingKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stored))
        {
            // 会话 Cookie 会轮换，旧版本用整串 Cookie 做账号标识，这里顺手把历史转存记录迁移过来。
            var legacy = await _database.GetSettingAsync($"cloud.saved.{Kind}.{LegacyAccountKey(cookie)}/{file.ShareId}/{file.FileId}", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                stored = legacy;
                await _database.SetSettingAsync(settingKey, legacy, cancellationToken).ConfigureAwait(false);
            }
        }
        if (!string.IsNullOrWhiteSpace(stored)) return _savedIds[key] = stored;
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_savedIds.TryGetValue(key, out cached)) return cached;
            var token = await GetShareTokenAsync(file.ShareId, file.Passcode, cancellationToken).ConfigureAwait(false);
            var dir = await EnsureSaveDirAsync(account, cookie, cancellationToken).ConfigureAwait(false);
            var body = new
            {
                fid_list = new[] { file.FileId }, fid_token_list = new[] { file.FileToken }, to_pdir_fid = dir,
                pwd_id = file.ShareId, stoken = token, pdir_fid = "0", scene = "link"
            };
            var saved = await SendAsync(HttpMethod.Post, _api + "share/sharepage/save?" + ClientQuery, body, cookie, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(saved, "转存失败");
            var taskId = NestedString(saved.Json, "data", "task_id");
            for (var attempt = 0; attempt < 20 && taskId.Length > 0; attempt++)
            {
                var task = await SendAsync(HttpMethod.Get, _api + "task?" + ClientQuery + "&task_id=" + Uri.EscapeDataString(taskId) + "&retry_index=" + attempt, null, cookie, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(task, "转存状态读取失败");
                var saveAs = Property(Property(task.Json, "data"), "save_as");
                var ids = Property(saveAs, "save_as_top_fids");
                if (ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0)
                {
                    var id = ids[0].GetString() ?? string.Empty;
                    if (id.Length > 0)
                    {
                        _savedIds[key] = id;
                        await _database.SetSettingAsync(settingKey, id, cancellationToken).ConfigureAwait(false);
                        return id;
                    }
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("网盘转存超时，请稍后重试。");
        }
        finally { _saveLock.Release(); }
    }

    private async Task<string> EnsureSaveDirAsync(string account, string cookie, CancellationToken cancellationToken)
    {
        if (_saveDirIds.TryGetValue(account, out var savedDirectory)) return savedDirectory;
        var root = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=0&_page=1&_size=200&_sort=file_type:asc,updated_at:desc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(root, "网盘目录读取失败");
        var list = Property(Property(root.Json, "data"), "list");
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (String(item, "file_name") == "PCbox" && Bool(item, "dir") && String(item, "fid") is { Length: > 0 } folder)
                    return _saveDirIds[account] = folder;
        var created = await SendAsync(HttpMethod.Post, _api + "file?" + ClientQuery, new { pdir_fid = "0", file_name = "PCbox", dir_path = "", dir_init_lock = false }, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(created, "创建播放目录失败");
        return _saveDirIds[account] = NestedString(created.Json, "data", "fid") is { Length: > 0 } id ? id : throw new InvalidDataException("网盘未返回播放目录。");
    }

    private async Task<TranscodedVideo[]> ReadOptionalTranscodesAsync(string fileId, string cookie, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1600));
        try { return await ReadTranscodesAsync(fileId, cookie, budget.Token).ConfigureAwait(false); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<TranscodedVideo[]> ReadTranscodesAsync(string fileId, string cookie, CancellationToken cancellationToken)
    {
        const string resolutions = "normal,low,high,super,2k,4k";
        var result = Kind == CloudProviderKind.Quark
            ? await SendAsync(HttpMethod.Post, "https://drive.quark.cn/1/clouddrive/file/v2/play/project?pr=ucpro&fr=pc",
                new { fid = fileId, resolutions, supports = "fmp4_av,m3u8,dolby_vision" }, cookie, cancellationToken).ConfigureAwait(false)
            : await SendAsync(HttpMethod.Post, _api + "file/v2/play?pr=UCBrowser&fr=pc", new { fid = fileId, resolutions, supports = "fmp4" }, cookie, cancellationToken).ConfigureAwait(false);
        if (Kind == CloudProviderKind.Quark && ((int)result.StatusCode is 404 or 405 or >= 500 ||
            String(result.Json, "message").Contains("plf_invalid", StringComparison.OrdinalIgnoreCase)))
            result = await SendAsync(HttpMethod.Post, _api + "file/v2/play?pr=ucpro&fr=pc", new { fid = fileId, resolutions, supports = "fmp4" }, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "转码播放失败");
        var list = Property(Property(result.Json, "data"), "video_list");
        if (list.ValueKind != JsonValueKind.Array) return [];
        var candidates = new List<TranscodedVideo>();
        foreach (var video in list.EnumerateArray())
        {
            var info = Property(video, "video_info");
            var url = String(info, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") ||
                address.UserInfo.Length > 0) continue;
            var height = Long(info, "height");
            if (height == 0) height = Long(video, "height");
            if (height == 0) height = String(video, "resolution") switch
            {
                "4k" => 2160, "2k" => 1440, "super" => 1080, "high" => 720, "normal" => 480,
                "low" => 360, "420p" => 420, _ => 0
            };
            var frameRate = VideoFormat.ReadFrameRate(String(info, "frame_rate"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(info, "fps"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(video, "frame_rate"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(video, "fps"));
            candidates.Add(new TranscodedVideo(address.AbsoluteUri, new VideoFormat(height is > 0 and <= int.MaxValue ? (int)height : 0, frameRate)));
        }
        return [.. candidates];
    }

    private static IReadOnlyList<QualityOption> TranscodingOptions(TranscodedVideo[] variants) =>
        [new QualityOption("原画", -1) { IsOriginal = true }, .. VideoFormat.ToOptions(variants.Select(item => item.Format), markHighestOriginal: false)];

    private sealed record TranscodedVideo(string Url, VideoFormat Format);

    private async Task<string> GetDownloadUrlAsync(string fileId, string cookie, CancellationToken cancellationToken)
    {
        var downloadApi = Kind == CloudProviderKind.Quark ? "https://drive.quark.cn/1/clouddrive/" : _api;
        var body = new { fids = new[] { fileId } };
        var result = await SendAsync(HttpMethod.Post, downloadApi + "file/download?" + ClientQuery, body, cookie, cancellationToken).ConfigureAwait(false);
        if (Kind == CloudProviderKind.Quark && (int)result.StatusCode is 404 or 405 or >= 500)
            result = await SendAsync(HttpMethod.Post, _api + "file/download?" + ClientQuery, body, cookie, cancellationToken).ConfigureAwait(false);
        if (Kind == CloudProviderKind.Quark && (int)result.StatusCode is 400 or >= 500)
            result = await SendAsync(HttpMethod.Post, _api + "file/download?" + ClientQuery + "&uc_param_str=",
                new { fids = fileId }, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "原画地址获取失败");
        var data = Property(result.Json, "data");
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0) data = data[0];
        var url = data.ValueKind == JsonValueKind.String ? data.GetString() : String(data, "download_url");
        return Uri.TryCreate(url, UriKind.Absolute, out var media) && media.Scheme is "http" or "https" ? media.AbsoluteUri : string.Empty;
    }

    private Dictionary<string, string> Headers(string cookie)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) {(Kind == CloudProviderKind.UC ? "uc-cloud-drive" : "quark-cloud-drive")}/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36",
            ["Referer"] = _home,
            ["Origin"] = _home.TrimEnd('/')
        };
        var merged = CookieHeader(cookie);
        if (!string.IsNullOrWhiteSpace(merged)) headers["Cookie"] = merged;
        return headers;
    }

    private string CredentialKey => Kind == CloudProviderKind.Quark ? "cloud.quarkCookie" : "cloud.ucCookie";

    public async Task<bool> VerifyCookieAsync(string cookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return false;
        var result = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=0&_page=1&_size=1",
            null, cookie, cancellationToken).ConfigureAwait(false);
        return result.StatusCode == HttpStatusCode.OK && Property(Property(result.Json, "data"), "list").ValueKind == JsonValueKind.Array;
    }

    public async Task<CloudDriveListing> ListFolderAsync(string folderId, string path, int page, CancellationToken cancellationToken = default)
    {
        var cookie = await _credential(cancellationToken).ConfigureAwait(false);
        if (cookie.Length == 0) throw new CloudAuthenticationException(Kind, $"{Kind} 网盘需要扫码登录。");
        page = Math.Max(1, page);
        var result = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=" + Uri.EscapeDataString(folderId) +
            "&_page=" + page + "&_size=100&_sort=file_type:asc,file_name:asc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "个人云盘目录读取失败");
        return ReadDriveListing(result, folderId, path, page);
    }

    public async Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        var cookie = await _credential(cancellationToken).ConfigureAwait(false);
        if (cookie.Length == 0) throw new CloudAuthenticationException(Kind, $"{Kind} 网盘需要扫码登录。");
        if (string.IsNullOrWhiteSpace(keyword)) return new CloudDriveListing([], false);
        page = Math.Max(1, page);
        var result = await SendAsync(HttpMethod.Get, _api + "file/search?" + ClientQuery + "&q=" + Uri.EscapeDataString(keyword.Trim()) +
            "&_page=" + page + "&_size=100&_sort=file_type:asc,updated_at:desc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "个人云盘搜索失败");
        return ReadDriveListing(result, "0", "/", page);
    }

    private CloudDriveListing ReadDriveListing(CloudHttpResult result, string parentId, string path, int page)
    {
        var data = Property(result.Json, "data");
        var list = Property(data, "list");
        if (list.ValueKind != JsonValueKind.Array) return new CloudDriveListing([], false);
        var items = new List<CloudDriveItem>();
        foreach (var item in list.EnumerateArray())
        {
            var id = String(item, "fid");
            var name = String(item, "file_name");
            if (id.Length == 0 || name.Length == 0) continue;
            var parent = String(item, "pdir_fid") is { Length: > 0 } value ? value : parentId;
            var itemPath = String(item, "file_path") is { Length: > 0 } savedPath ? savedPath : JoinPath(path, name);
            var updated = Long(item, "updated_at");
            DateTimeOffset? modified = updated > 0 ? updated > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : DateTimeOffset.FromUnixTimeSeconds(updated) : null;
            items.Add(new CloudDriveItem(Kind, id, parent, name, itemPath, Long(item, "size"), Bool(item, "dir"), modified));
        }
        var metadata = Property(data, "metadata");
        var total = Long(metadata, "_total") > 0 ? Long(metadata, "_total") : Long(metadata, "total");
        return new CloudDriveListing(items, total > page * 100 || total == 0 && list.GetArrayLength() == 100);
    }

    private static string JoinPath(string parent, string name) => (parent.Length == 0 ? "/" : parent).TrimEnd('/') + "/" + name;

    private static string AccountKey(string cookie) => StableToken(cookie) is { Length: > 0 } stable ? Hash(stable) : LegacyAccountKey(cookie);

    private static string LegacyAccountKey(string cookie) => Hash(cookie);

    /// <summary>会话 Cookie 每次调用都会轮换，只有 __uid 这类长期值能稳定标识账号。</summary>
    private static string StableToken(string cookie)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith("__uid=", StringComparison.Ordinal) && part.Length > 6) return part[6..];
        return string.Empty;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    /// <summary>接口每次调用都会轮换会话 Cookie，播放地址的 CDN 只认最新值，所以统一从合并后的 Cookie 组装请求头。</summary>
    private string CookieHeader(string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie) && !string.Equals(_seed, cookie, StringComparison.Ordinal))
        {
            _seed = cookie;
            _jar.MergeHeader(cookie);
        }
        var merged = _jar.ToHeader();
        return merged.Length > 0 ? merged : cookie;
    }

    private async Task<CloudHttpResult> SendAsync(HttpMethod method, string url, object? json, string cookie, CancellationToken cancellationToken)
    {
        var result = await _http.SendAsync(method, url, json, Headers(cookie), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Cookies.Count > 0) await AbsorbAsync(result.Cookies, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task AbsorbAsync(IReadOnlyList<string> setCookieHeaders, CancellationToken cancellationToken)
    {
        // 公开分享浏览不带登录凭据，那边的 Set-Cookie 属于匿名会话；混进登录凭据会让账号退化成游客。
        if (_seed.Length == 0) return;
        _jar.Absorb(setCookieHeaders);
        var merged = _jar.ToHeader();
        if (merged.Length == 0 || string.Equals(merged, _persisted, StringComparison.Ordinal)) return;
        _persisted = merged;
        await _jarLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _database.SetSettingAsync(CredentialKey, SecretStore.Protect(merged), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Log($"{Kind} 会话 Cookie 保存失败：{ex.Message}"); }
        finally { _jarLock.Release(); }
    }

    private void EnsureSuccess(CloudHttpResult result, string fallback)
    {
        if (result.StatusCode == HttpStatusCode.Unauthorized)
            throw new CloudAuthenticationException(Kind, $"{Kind} 网盘登录已失效，请重新扫码。");
        var code = String(result.Json, "code");
        var message = First(String(result.Json, "message"), String(result.Json, "msg"), code);
        if ((int)result.StatusCode >= 400) throw new InvalidOperationException($"{fallback}（HTTP {(int)result.StatusCode}）{message}");
        if ((int.TryParse(code, out var number) && number != 0) || (result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False))
            throw new InvalidOperationException($"{fallback}：{message}");
    }

    private static bool IsMedia(JsonElement item, string name) => String(item, "obj_category") is "video" or "audio" || Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".mkv" or ".avi" or ".mov" or ".flv" or ".mp3" or ".m4a";
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : Property(element, name).ValueKind is JsonValueKind.Number ? Property(element, name).GetRawText() : string.Empty;
    private static string NestedString(JsonElement element, params string[] path)
    {
        var value = path.Aggregate(element, Property);
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? string.Empty, JsonValueKind.Number => value.GetRawText(), _ => string.Empty };
    }
    private static long Long(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }
    private static bool Bool(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
