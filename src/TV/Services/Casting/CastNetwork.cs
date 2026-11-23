using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TV.Services.Persistence;

namespace TV.Services.Casting;

internal sealed record CastOffer(Guid Id, string Title, IPAddress Sender, Uri? InternetMedia, string? LocalMedia);
internal enum CastOperation { Play, Pause, Stop, Seek, Volume, Mute }
internal sealed record CastControl(Guid Id, CastOperation Operation, double Value = 0);
internal sealed record CastStatus(string State, TimeSpan Position, TimeSpan Duration, int Volume, bool Muted)
{
    public static readonly CastStatus Idle = new("NO_MEDIA_PRESENT", TimeSpan.Zero, TimeSpan.Zero, 100, false);
}

internal sealed class CastNetwork : IAsyncDisposable
{
    private const long MaximumVideoBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv", ".mpeg", ".mpg", ".3gp" };
    private readonly string _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
    private readonly object _gate = new();
    private readonly IPAddress _address;
    private readonly byte[] _mask;
    private readonly WebApplication _host;
    private readonly DlnaRenderer _dlna;
    private readonly string _directory;
    private readonly CancellationTokenSource _lifetime = new();
    private CastOffer? _pending;
    private CastOffer? _active;
    private int _uploading;

    internal CastNetwork()
    {
        var adapter = FindLocalNetwork();
        _address = adapter.Address;
        _mask = adapter.IPv4Mask.GetAddressBytes();
        DeviceName = $"PCbox · {Environment.MachineName}";
        _directory = Path.Combine(AppPaths.Root, "cache", "cast-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(CastNetwork).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(_address, 0);
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = 12;
            server.Limits.MaxRequestBodySize = MaximumVideoBytes;
        });
        _host = builder.Build();
        _dlna = new DlnaRenderer(this);
        _host.Run(RespondAsync);
    }

    public string DeviceName { get; }
    public Uri? SenderAddress => BaseAddress is { } address ? new Uri(address, "sender/" + _session) : null;
    public Uri? BaseAddress { get; private set; }
    public bool IsDiscoverable => _dlna.IsRunning;
    internal CastStatus Status { get; set; } = CastStatus.Idle;
    public event Action<CastOffer>? Offered;
    public event Action<CastControl>? Controlled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        BaseAddress = new Uri(_host.Urls.Single());
        _dlna.StartDiscovery(_address, _lifetime.Token);
    }

    internal bool IsLocal(IPAddress? remote) => remote is not null && IsSameSubnet(remote);

    private bool IsSameSubnet(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var candidate = remote.GetAddressBytes();
        var local = _address.GetAddressBytes();
        for (var index = 0; index < local.Length; index++)
            if ((local[index] & _mask[index]) != (candidate[index] & _mask[index])) return false;
        return true;
    }

    internal void Offer(CastOffer offer)
    {
        lock (_gate)
        {
            DeleteVideo(_pending?.LocalMedia);
            _pending = offer;
        }
        Offered?.Invoke(offer);
    }

    public CastOffer? Accept(Guid id)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Id != id) return null;
            DeleteVideo(_active?.LocalMedia);
            _pending = null;
            return _active = pending;
        }
    }

    public void Reject(Guid id)
    {
        lock (_gate)
        {
            if (_pending?.Id != id) return;
            DeleteVideo(_pending.LocalMedia);
            _pending = null;
        }
    }

    internal void Control(IPAddress sender, CastOperation operation, double value = 0)
    {
        Guid id;
        lock (_gate)
        {
            if (_active is not { LocalMedia: null } active || !active.Sender.Equals(sender)) return;
            id = active.Id;
        }
        Controlled?.Invoke(new CastControl(id, operation, value));
    }

    private async Task RespondAsync(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!IsLocal(context.Connection.RemoteIpAddress) || _lifetime.IsCancellationRequested ||
            !context.Request.Host.Host.Equals(_address.ToString(), StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        var path = context.Request.Path.Value;
        if (context.Request.Method == "GET" && path == "/sender/" + _session)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            await context.Response.WriteAsync(SenderPage).ConfigureAwait(false);
        }
        else if (context.Request.Method == "POST" && path == "/file/" + _session)
            await ReceiveFileAsync(context).ConfigureAwait(false);
        else if (!await _dlna.RespondAsync(context).ConfigureAwait(false))
            context.Response.StatusCode = 404;
    }

    private async Task ReceiveFileAsync(HttpContext context)
    {
        var header = context.Request.Headers["X-Video-Name"].ToString();
        if (header.Length is 0 or > 1600)
        {
            context.Response.StatusCode = 400;
            return;
        }
        var filename = Path.GetFileName(Uri.UnescapeDataString(header));
        var extension = Path.GetExtension(filename);
        if (!VideoExtensions.Contains(extension) || context.Request.ContentLength is not > 0 or > MaximumVideoBytes)
        {
            context.Response.StatusCode = 415;
            return;
        }
        if (Interlocked.Exchange(ref _uploading, 1) != 0)
        {
            context.Response.StatusCode = 409;
            return;
        }

        var file = Path.Combine(_directory, Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());
        var complete = false;
        try
        {
            Directory.CreateDirectory(_directory);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await context.Request.Body.CopyToAsync(target, deadline.Token).ConfigureAwait(false);
                if (target.Length != context.Request.ContentLength) return;
            }
            var title = CleanTitle(Path.GetFileNameWithoutExtension(filename));
            Offer(new CastOffer(Guid.NewGuid(), title, context.Connection.RemoteIpAddress!, null, file));
            complete = true;
            context.Response.StatusCode = 204;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { if (!context.Response.HasStarted) context.Response.StatusCode = 507; }
        finally
        {
            if (!complete) DeleteVideo(file);
            Volatile.Write(ref _uploading, 0);
        }
    }

    internal static string CleanTitle(string? title)
    {
        var result = new string((title ?? "").Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        return result.Length > 0 ? result : "投送视频";
    }

    private static UnicastIPAddressInformation FindLocalNetwork()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .Select(item => (Adapter: item, Info: item.GetIPProperties()))
            .OrderByDescending(item => item.Info.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(item => item.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .SelectMany(item => item.Info.UnicastAddresses)
            .FirstOrDefault(item => IsPrivateV4(item.Address)) ?? throw new InvalidOperationException("请先连接家庭 Wi-Fi 或局域网。");
    }

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    private static void DeleteVideo(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _dlna.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _host.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            if (Directory.Exists(_directory))
            {
                foreach (var path in Directory.EnumerateFiles(_directory)) DeleteVideo(path);
                try { Directory.Delete(_directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private const string SenderPage = """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="color-scheme" content="light dark"><title>投屏 · PCbox</title><style>
        body{font:16px system-ui,sans-serif;margin:0}main{max-width:30rem;margin:12vh auto;padding:0 24px}h1{font-size:2rem;margin-bottom:2rem}
        input,button,progress{box-sizing:border-box;width:100%;margin-top:12px}button{font:inherit;font-weight:650;padding:14px;border:0;border-radius:10px;color:white;background:#3054b9}button:disabled{opacity:.6}input{font:inherit}
        </style></head><body><main><h1>投屏到PCbox</h1><input id="video" aria-label="选择视频" type="file" accept="video/*,.mkv,.avi"><button id="send" disabled>发送视频</button>
        <progress id="progress" max="100" value="0" hidden></progress><p id="status" role="status"></p></main><script>
        const files=document.querySelector('#video'), send=document.querySelector('#send'), status=document.querySelector('#status'), bar=document.querySelector('#progress');
        files.onchange=()=>{send.disabled=!files.files.length;status.textContent='';bar.hidden=true;};
        send.onclick=()=>{const video=files.files[0];if(!video)return;send.disabled=files.disabled=true;bar.hidden=false;bar.value=0;
          status.textContent='正在发送…';const request=new XMLHttpRequest();
          request.open('POST',location.pathname.replace('/sender/','/file/'));request.setRequestHeader('X-Video-Name',encodeURIComponent(video.name));
          request.upload.onprogress=e=>{if(e.lengthComputable)bar.value=e.loaded/e.total*100;};
          request.onloadend=()=>{files.disabled=false;send.disabled=false;if(request.status===204){bar.value=100;status.textContent='已发送，请在电脑上接收。';}
            else {bar.hidden=true;status.textContent=request.status===415?'请选择受支持的本地视频，大小不超过 2 GB。':'发送失败，请确认电脑仍在接收。';}};
          request.send(video);
        };</script></body></html>
        """;
}
