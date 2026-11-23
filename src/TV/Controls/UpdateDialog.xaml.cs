using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TV.Services.Updates;

namespace TV.Controls;

public sealed partial class UpdateDialog : ContentDialog
{
    private readonly AppUpdateRelease _release;
    private readonly AppUpdateService _updates;
    private CancellationTokenSource? _download;

    public UpdateDialog(AppUpdateRelease release, AppUpdateService updates)
    {
        _release = release;
        _updates = updates;
        InitializeComponent();
        VersionText.Text = $"{updates.CurrentVersion.ToString(3)} → {release.Version.ToString(3)}";
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(release.Title) ? "PCbox " + release.Version.ToString(3) : release.Title;
        NotesText.Text = FormatNotes(release.Notes);
        try
        {
            var package = updates.SelectPackage(release);
            PackageText.Text = $"{(package.Incremental ? "增量更新" : "完整安装")} · {FormatSize(package.Asset.Size)} · 下载后校验 SHA-256";
        }
        catch (Exception ex)
        {
            PackageText.Text = ex.Message;
        }
        PrimaryButtonClick += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        CloseButtonClick += (_, _) => CancelDownload();
        Closing += (_, _) => CancelDownload();
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_download is not null) return;
        _download = new CancellationTokenSource();
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        PrimaryButtonText = "正在下载";
        CloseButtonText = "取消下载";
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "正在连接 GitHub Release，下载完成后会自动校验并安装。";
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(value.Percent, 0, 100);
            ProgressText.Text = $"{FormatSize(value.Downloaded)} / {FormatSize(value.Total)} · {value.Percent:0}%";
        });
        try
        {
            var downloaded = await _updates.DownloadAsync(_release, progress, _download.Token);
            StatusText.Text = $"{(downloaded.Package.Incremental ? "增量补丁" : "完整安装包")}已校验，正在启动安装。";
            PrimaryButtonText = "正在安装";
            CloseButtonText = "请稍候";
            App.Window.BeginUpdateInstallation(downloaded.Path);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载，可稍后继续；已下载的部分会保留用于断点续传。";
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Log($"更新下载失败: {ex.GetType().Name} {ex.Message}");
            StatusText.Text = "更新失败：" + ex.Message;
        }
        finally
        {
            _download.Dispose();
            _download = null;
            if (!App.Window.IsClosing)
            {
                IsPrimaryButtonEnabled = true;
                IsSecondaryButtonEnabled = true;
                PrimaryButtonText = "重试下载";
                CloseButtonText = "关闭";
            }
        }
    }

    private async void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var uri = new Uri("https://github.com/binchen6/Liuguang-Cinema/releases/tag/" + Uri.EscapeDataString(_release.Tag));
        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void CancelDownload()
    {
        if (_download is null) return;
        _download.Cancel();
        StatusText.Text = "正在取消下载…";
    }

    internal static string FormatNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "此版本未提供详细更新说明。";
        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder();
        foreach (var raw in lines.Take(500))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (output.Length > 0 && output[^1] != '\n') output.AppendLine();
                continue;
            }
            line = Regex.Replace(line, @"^#{1,6}\s*", string.Empty, RegexOptions.CultureInvariant);
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "• " + line[2..];
            line = line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
            if (output.Length > 0 && output[^1] != '\n') output.AppendLine();
            output.Append(line);
            if (output.Length > 40_000) break;
        }
        return output.ToString().Trim();
    }

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024:0.##} MB";
}
