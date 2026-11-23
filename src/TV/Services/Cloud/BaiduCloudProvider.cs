using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Persistence;

namespace TV.Services.Cloud;

public sealed class BaiduCloudProvider : ICloudDriveProvider
{
    private const string AppId = "250528";
    private const string SaveDirectory = "/PCbox";
    private readonly CloudHttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _credential;
    private readonly DatabaseService _database;
    private readonly ConcurrentDictionary<string, ShareContext> _shareContexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public BaiduCloudProvider(CloudHttpClient http, Func<CancellationToken, Task<string>> credential, DatabaseService database)
    {
        _http = http;
        _credential = credential;
        _database = database;
    }

    private sealed record ShareContext(string NumericShareId, string Uk, string Sign, string Timestamp, string BdsToken, string Cookie);

    public CloudProviderKind Kind => CloudProviderKind.Baidu;
    public bool CanHandle(string url) => CloudShareLink.TryParse(url, out var link) && link.Provider == Kind;

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!CloudShareLink.TryParse(url, out var link) || link.Provider != Kind)
            throw new ArgumentException("百度分享链接格式无效。", nameof(url));
        var credential = await RequireCredentialAsync(cancellationToken).ConfigureAwait(false);
        var cookie = credential;
        if (link.Passcode.Length > 0)
        {
            var shortId = link.ShareId.StartsWith('1') ? link.ShareId[1..] : link.ShareId;
            var verify = await RequestAsync(HttpMethod.Post,
                $"https://pan.baidu.com/share/verify?surl={Uri.EscapeDataString(shortId)}&channel=chunlei&web=1&app_id={AppId}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                cookie, [new("pwd", link.Passcode), new("vcode", ""), new("vcode_str", "")], cancellationToken).ConfigureAwait(false);
            EnsureSuccess(ReadJson(verify), "百度分享提取码验证失败");
            cookie = MergeShareCookie(cookie, verify.Cookies);
        }
        var page = await RequestAsync(HttpMethod.Get, link.ToShareUrl(), cookie, null, cancellationToken).ConfigureAwait(false);
        if (page.StatusCode != HttpStatusCode.OK) throw new InvalidDataException($"百度分享页面读取失败（HTTP {(int)page.StatusCode}）。");
        var numericShareId = Number(page.Text, "shareid");
        var uk = Number(page.Text, "uk");
        if (numericShareId.Length == 0 || uk.Length == 0)
            throw new InvalidDataException("百度分享不存在、已失效或需要重新验证提取码。");
        var context = new ShareContext(numericShareId, uk, Text(page.Text, "sign"), Number(page.Text, "timestamp"),
            Text(page.Text, "bdstoken"), cookie);
        var files = new List<CloudFile>();
        var subtitles = new List<CloudFile>();
        await ListShareFilesAsync(link, context, "/", string.Empty, files, subtitles, 0, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0) throw new InvalidDataException("百度分享中没有可播放的媒体文件。");
        return new CloudShare
        {
            Provider = Kind, ShareId = link.ShareId, SourceUrl = url,
            Name = Text(page.Text, "sharename"), Files = files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SubtitleFiles = subtitles
        };
    }

    private async Task ListShareFilesAsync(CloudShareLink link, ShareContext context, string directory, string parentName,
        List<CloudFile> files, List<CloudFile> subtitles, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8 || files.Count >= 500) return;
        for (var page = 1; page <= 50 && files.Count < 500; page++)
        {
            var url = "https://pan.baidu.com/share/list?channel=chunlei&web=1&app_id=" + AppId +
                "&uk=" + Uri.EscapeDataString(context.Uk) + "&shareid=" + Uri.EscapeDataString(context.NumericShareId) +
                "&order=name&desc=0&showempty=0&num=200&page=" + page + "&dir=" + Uri.EscapeDataString(directory);
            var response = await RequestAsync(HttpMethod.Get, url, context.Cookie, null, cancellationToken).ConfigureAwait(false);
            var root = ReadJson(response);
            EnsureSuccess(root, "百度分享目录读取失败");
            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) break;
            foreach (var item in list.EnumerateArray())
            {
                var id = String(item, "fs_id");
                var name = String(item, "server_filename");
                var path = String(item, "path");
                if (!CloudShareLink.IsIdentifier(id)) continue;
                if (Long(item, "isdir") == 1)
                {
                    if (path.Length > 0) await ListShareFilesAsync(link, context, path, name, files, subtitles, depth + 1, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var file = new CloudFile(Kind, link.ShareId, id, string.Empty, name, Long(item, "size"), parentName,
                    link.Passcode, link.FolderId, directory);
                if (CloudSubtitleFiles.IsSubtitle(name)) subtitles.Add(file);
                else if (IsMedia(name)) files.Add(file);
                _shareContexts[$"{link.ShareId}/{id}"] = context;
            }
            if (list.GetArrayLength() < 200) break;
        }
    }

    public async Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default)
    {
        if (!quality.Contains("原画", StringComparison.OrdinalIgnoreCase) && quality != "自动")
            throw new NotSupportedException("百度网盘当前线路仅提供原画。");
        var credential = await RequireCredentialAsync(cancellationToken).ConfigureAwait(false);
        var savedId = file.ShareId == "drive" ? file.FileId : await SaveFileAsync(file, credential, cancellationToken).ConfigureAwait(false);
        return await GetDownloadAsync(savedId, credential, file.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SaveFileAsync(CloudFile file, string credential, CancellationToken cancellationToken)
    {
        if (!_shareContexts.TryGetValue($"{file.ShareId}/{file.FileId}", out var context))
            throw new InvalidDataException("百度分享会话已过期，请重新打开详情。");
        var account = AccountKey(credential);
        var settingKey = $"cloud.saved.Baidu.{account}/{file.ShareId}/{file.FileId}";
        var stored = await _database.GetSettingAsync(settingKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stored = await _database.GetSettingAsync(settingKey, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored)) return stored;
            await EnsureSaveDirectoryAsync(credential, cancellationToken).ConfigureAwait(false);
            var transferUrl = "https://pan.baidu.com/share/transfer?shareid=" + Uri.EscapeDataString(context.NumericShareId) +
                "&from=" + Uri.EscapeDataString(context.Uk) + "&channel=chunlei&web=1&app_id=" + AppId +
                "&bdstoken=" + Uri.EscapeDataString(context.BdsToken);
            var response = await RequestAsync(HttpMethod.Post, transferUrl, context.Cookie,
                [new("fsidlist", "[" + file.FileId + "]"), new("path", SaveDirectory)], cancellationToken).ConfigureAwait(false);
            EnsureSuccess(ReadJson(response), "百度分享转存失败");
            var saved = await FindSavedFileAsync(file.Name, file.Size, credential, cancellationToken).ConfigureAwait(false);
            if (saved.Length == 0) throw new InvalidDataException("百度转存后未找到播放文件，请在我的云盘中确认。");
            await _database.SetSettingAsync(settingKey, saved, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally { _saveLock.Release(); }
    }

    private async Task EnsureSaveDirectoryAsync(string credential, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(HttpMethod.Post,
            $"https://pan.baidu.com/api/create?channel=chunlei&web=1&app_id={AppId}", credential,
            [new("path", SaveDirectory), new("isdir", "1"), new("block_list", "[]")], cancellationToken).ConfigureAwait(false);
        var root = ReadJson(response);
        if (ErrorCode(root) == -8) return;
        EnsureSuccess(root, "百度播放目录创建失败");
    }

    private async Task<string> FindSavedFileAsync(string name, long size, string credential, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(HttpMethod.Get,
            $"https://pan.baidu.com/api/list?dir={Uri.EscapeDataString(SaveDirectory)}&order=time&desc=1&showempty=0&web=1&page=1&num=200&app_id={AppId}",
            credential, null, cancellationToken).ConfigureAwait(false);
        var root = ReadJson(response);
        EnsureSuccess(root, "百度播放目录读取失败");
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return string.Empty;
        return list.EnumerateArray().Where(item => Long(item, "isdir") != 1 && String(item, "server_filename") == name)
            .OrderByDescending(item => Long(item, "size") == size).ThenByDescending(item => Long(item, "server_mtime"))
            .Select(item => String(item, "fs_id")).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    }

    private async Task<CloudPlayResult> GetDownloadAsync(string fileId, string credential, string name, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(HttpMethod.Get,
            $"https://pan.baidu.com/api/filemetas?fsids=%5B{Uri.EscapeDataString(fileId)}%5D&dlink=1&channel=chunlei&web=1&app_id={AppId}",
            credential, null, cancellationToken).ConfigureAwait(false);
        var root = ReadJson(response);
        EnsureSuccess(root, "百度原画地址获取失败");
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException("百度未返回播放地址。");
        var url = String(list[0], "dlink");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var media) || media.Scheme is not ("http" or "https"))
            throw new InvalidDataException("百度播放地址无效。");
        if (media.Scheme == "http") media = new UriBuilder(media) { Scheme = "https", Port = -1 }.Uri;
        var contentType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(name), null);
        return new CloudPlayResult(media.AbsoluteUri, Headers(credential), contentType, CloudSubtitleFiles.Extension(name))
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(6)
        };
    }

    public async Task<CloudDriveListing> ListFolderAsync(string folderId, string path, int page, CancellationToken cancellationToken = default)
    {
        var credential = await RequireCredentialAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var response = await RequestAsync(HttpMethod.Get,
            $"https://pan.baidu.com/api/list?dir={Uri.EscapeDataString(path)}&order=name&desc=0&showempty=0&web=1&page={page}&num=100&app_id={AppId}",
            credential, null, cancellationToken).ConfigureAwait(false);
        var root = ReadJson(response);
        EnsureSuccess(root, "百度个人云盘目录读取失败");
        return ReadDriveListing(root, folderId, path, page);
    }

    public async Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new CloudDriveListing([], false);
        var credential = await RequireCredentialAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var response = await RequestAsync(HttpMethod.Get,
            $"https://pan.baidu.com/api/search?key={Uri.EscapeDataString(keyword.Trim())}&dir=%2F&recursion=1&web=1&page={page}&num=100&app_id={AppId}",
            credential, null, cancellationToken).ConfigureAwait(false);
        var root = ReadJson(response);
        EnsureSuccess(root, "百度个人云盘搜索失败");
        return ReadDriveListing(root, "0", "/", page);
    }

    private static CloudDriveListing ReadDriveListing(JsonElement root, string parentId, string path, int page)
    {
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return new CloudDriveListing([], false);
        var items = new List<CloudDriveItem>();
        foreach (var item in list.EnumerateArray())
        {
            var id = String(item, "fs_id");
            var name = String(item, "server_filename");
            if (id.Length == 0 || name.Length == 0) continue;
            var itemPath = String(item, "path") is { Length: > 0 } saved ? saved : (path.TrimEnd('/') + "/" + name);
            var parent = String(item, "parent_path") is { Length: > 0 } parentPath ? parentPath : parentId;
            var updated = Long(item, "server_mtime");
            items.Add(new CloudDriveItem(CloudProviderKind.Baidu, id, parent, name, itemPath, Long(item, "size"), Long(item, "isdir") == 1,
                updated > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated) : null));
        }
        return new CloudDriveListing(items, list.GetArrayLength() == 100);
    }

    private async Task<string> RequireCredentialAsync(CancellationToken cancellationToken)
    {
        var credential = await _credential(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential)) throw new CloudAuthenticationException(Kind, "百度网盘需要扫码登录。");
        return credential;
    }

    private Task<CloudRawResult> RequestAsync(HttpMethod method, string url, string credential,
        IEnumerable<KeyValuePair<string, string>>? form, CancellationToken cancellationToken) =>
        _http.SendRawAsync(method, url, Headers(credential), form, cancellationToken);

    private static Dictionary<string, string> Headers(string credential) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "pan.baidu.com",
        ["Referer"] = "https://pan.baidu.com/",
        ["Cookie"] = credential
    };

    private static string MergeShareCookie(string credential, IReadOnlyList<string> cookies)
    {
        var values = credential.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        foreach (var header in cookies)
        {
            var pair = header.Split(';', 2)[0].Split('=', 2);
            if (pair.Length == 2 && pair[0] == "BDCLND") values[pair[0]] = pair[1];
        }
        return string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static void EnsureSuccess(JsonElement root, string fallback)
    {
        var error = ErrorCode(root);
        if (error == 0) return;
        if (error is -6 or 31045) throw new CloudAuthenticationException(CloudProviderKind.Baidu, "百度网盘登录已失效，请重新扫码。");
        var message = error switch
        {
            -9 => "提取码不正确",
            -12 => "分享不存在或已失效",
            -7 => "当前账号没有访问权限",
            _ => fallback + $"（{error}）"
        };
        throw new InvalidDataException(message + "。");
    }

    private static int ErrorCode(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errno", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var error) ? error : 0;

    private static JsonElement ReadJson(CloudRawResult response)
    {
        try { return JsonDocument.Parse(response.Text).RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidDataException($"百度网盘返回了非 JSON 内容（HTTP {(int)response.StatusCode}）。", ex); }
    }

    private static string Number(string text, string name) => FieldRegex(name).Match(text) is { Success: true } match ? match.Groups[1].Value : string.Empty;
    private static string Text(string text, string name) => StringFieldRegex(name).Match(text) is { Success: true } match ? match.Groups[1].Value : string.Empty;
    private static Regex FieldRegex(string name) => new("[\\\"']" + Regex.Escape(name) + "[\\\"']\\s*:\\s*[\\\"']?(\\d+)", RegexOptions.CultureInvariant);
    private static Regex StringFieldRegex(string name) => new("[\\\"']" + Regex.Escape(name) + "[\\\"']\\s*:\\s*[\\\"']([^\\\"']*)", RegexOptions.CultureInvariant);
    private static string String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : string.Empty : string.Empty;
    private static long Long(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0 : 0;
    private static string AccountKey(string credential)
    {
        var bduss = credential.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith("BDUSS=", StringComparison.OrdinalIgnoreCase)) ?? credential;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bduss)))[..12];
    }
    private static bool IsMedia(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".m4v" or ".mkv" or ".avi" or ".mov" or ".webm" or ".flv" or ".ts" or ".mp3" or ".m4a" or ".aac";
}
