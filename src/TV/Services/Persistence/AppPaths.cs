namespace TV.Services.Persistence;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("TV_DATA_ROOT") is { Length: > 0 } profile
        ? Path.GetFullPath(profile)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TV");
    public static string Database { get; } = Path.Combine(Root, "tv.db");
    public static string Config { get; } = Path.Combine(Root, "config", "last-good.json");
    public static string Images { get; } = Path.Combine(Root, "cache", "images");
    public static string UpdateCache { get; } = Path.Combine(Root, "cache", "updates");
    public static string DefaultDownloads { get; } = UserContentDirectory(Environment.SpecialFolder.MyVideos, "Videos", "PCbox");
    public static string DefaultScreenshots { get; } = UserContentDirectory(Environment.SpecialFolder.MyPictures, "Pictures", "PCbox");

    private static string UserContentDirectory(Environment.SpecialFolder folder, string fallbackFolder, string appFolder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = string.IsNullOrWhiteSpace(profile) ? Root : Path.Combine(profile, fallbackFolder);
        }
        return Path.Combine(path, appFolder);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        Directory.CreateDirectory(Images);
    }
}
