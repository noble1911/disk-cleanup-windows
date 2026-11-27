namespace SpaceLens.Core.Advisor;

/// <summary>A well-known folder whose contents are safe (or mostly safe) to clear.</summary>
public sealed record KnownLocation(
    string Id,
    string Title,
    string Category,
    string Description,
    RiskLevel Risk,
    string[] Patterns,
    bool RequiresAdmin = false,
    long MinimumBytes = 10 * 1024 * 1024);

public static class KnownLocations
{
    public static readonly IReadOnlyList<KnownLocation> All =
    [
        new("user-temp", "Your temporary files", "Temporary files",
            "Files apps leave behind in your temp folder. Anything still in use is skipped.",
            RiskLevel.Safe, [@"%TEMP%"]),

        new("windows-temp", "Windows temporary files", "Temporary files",
            "System-wide temporary files. Anything still in use is skipped.",
            RiskLevel.Safe, [@"%WINDIR%\Temp"], RequiresAdmin: true),

        new("windows-update-cache", "Windows Update download cache", "System",
            "Update packages Windows already installed. Windows downloads them again if it needs to.",
            RiskLevel.Safe, [@"%WINDIR%\SoftwareDistribution\Download"], RequiresAdmin: true, MinimumBytes: 50 * 1024 * 1024),

        new("crash-dumps", "Crash dumps and error reports", "System",
            "Memory dumps and Windows Error Reporting files from past crashes. Only useful when debugging those crashes.",
            RiskLevel.Safe,
            [@"%LOCALAPPDATA%\CrashDumps", @"%WINDIR%\Minidump", @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive",
             @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue", @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive",
             @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue", @"%PROGRAMDATA%\Microsoft\Windows\WER\Temp"]),

        new("browser-chrome", "Google Chrome cache", "Browser caches",
            "Cached web pages and scripts. Chrome rebuilds them as you browse. Close Chrome first for best results.",
            RiskLevel.Safe,
            [@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache", @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Code Cache",
             @"%LOCALAPPDATA%\Google\Chrome\User Data\*\GPUCache", @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Service Worker\CacheStorage"]),

        new("browser-edge", "Microsoft Edge cache", "Browser caches",
            "Cached web pages and scripts. Edge rebuilds them as you browse. Close Edge first for best results.",
            RiskLevel.Safe,
            [@"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Cache", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Code Cache",
             @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\GPUCache", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Service Worker\CacheStorage"]),

        new("browser-brave", "Brave cache", "Browser caches",
            "Cached web pages and scripts. Close Brave first for best results.",
            RiskLevel.Safe,
            [@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Cache", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Code Cache"]),

        new("browser-firefox", "Firefox cache", "Browser caches",
            "Cached web content. Close Firefox first for best results.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2"]),

        new("shader-cache", "Graphics shader caches", "System",
            "Compiled shaders from your graphics driver and DirectX. Games rebuild them on next launch (first load may be slower).",
            RiskLevel.Safe,
            [@"%LOCALAPPDATA%\D3DSCache", @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache",
             @"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\AMD\GLCache", @"%LOCALAPPDATA%\Intel\ShaderCache"],
            MinimumBytes: 50 * 1024 * 1024),

        new("teams-cache", "Microsoft Teams cache", "App caches",
            "Teams' cached files. Quit Teams first; it rebuilds the cache when it starts.",
            RiskLevel.Safe,
            [@"%APPDATA%\Microsoft\Teams\Cache", @"%APPDATA%\Microsoft\Teams\Service Worker\CacheStorage",
             @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache"],
            MinimumBytes: 50 * 1024 * 1024),

        new("npm-cache", "npm cache", "Developer caches",
            "Downloaded npm packages. npm downloads them again when needed (equivalent to 'npm cache clean --force').",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\npm-cache"], MinimumBytes: 100 * 1024 * 1024),

        new("yarn-cache", "Yarn cache", "Developer caches",
            "Downloaded Yarn packages. Yarn downloads them again when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Yarn\Cache"], MinimumBytes: 100 * 1024 * 1024),

        new("pnpm-store", "pnpm store", "Developer caches",
            "pnpm's shared package store. Run 'pnpm store prune' to remove only unused packages, or clear it fully here.",
            RiskLevel.Review, [@"%LOCALAPPDATA%\pnpm\store"], MinimumBytes: 200 * 1024 * 1024),

        new("pip-cache", "pip cache", "Developer caches",
            "Downloaded Python wheels. pip downloads them again when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\pip\Cache"], MinimumBytes: 100 * 1024 * 1024),

        new("nuget-cache", "NuGet package cache", "Developer caches",
            "Downloaded NuGet packages. They are restored on the next build (equivalent to 'dotnet nuget locals all --clear').",
            RiskLevel.Review, [@"%USERPROFILE%\.nuget\packages", @"%LOCALAPPDATA%\NuGet\v3-cache"], MinimumBytes: 500 * 1024 * 1024),

        new("gradle-cache", "Gradle caches", "Developer caches",
            "Downloaded dependencies and build caches. Gradle downloads them again on the next build.",
            RiskLevel.Review, [@"%USERPROFILE%\.gradle\caches", @"%USERPROFILE%\.gradle\wrapper\dists"], MinimumBytes: 500 * 1024 * 1024),

        new("maven-repo", "Maven local repository", "Developer caches",
            "Downloaded Maven artifacts. Maven downloads them again on the next build.",
            RiskLevel.Review, [@"%USERPROFILE%\.m2\repository"], MinimumBytes: 500 * 1024 * 1024),

        new("cargo-cache", "Cargo registry cache", "Developer caches",
            "Downloaded Rust crates. Cargo downloads them again when needed.",
            RiskLevel.Review, [@"%USERPROFILE%\.cargo\registry\cache", @"%USERPROFILE%\.cargo\registry\src"], MinimumBytes: 500 * 1024 * 1024),

        new("go-cache", "Go build cache", "Developer caches",
            "Go's compiled build cache (equivalent to 'go clean -cache').",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\go-build"], MinimumBytes: 200 * 1024 * 1024),

        new("vs-cache", "Visual Studio component cache", "Developer caches",
            "Visual Studio's MEF component cache. It is rebuilt the next time Visual Studio starts. Close Visual Studio first.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\VisualStudio\*\ComponentModelCache"],
            MinimumBytes: 100 * 1024 * 1024),
    ];

    /// <summary>
    /// Expands environment variables and single-segment '*' wildcards (e.g. browser profile folders).
    /// Returns only folders that exist.
    /// </summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        string expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (expanded.Contains('%')) yield break;

        var parts = expanded.Split('\\');
        var current = new List<string> { parts[0] + "\\" };
        for (int i = 1; i < parts.Length; i++)
        {
            var next = new List<string>();
            foreach (var basePath in current)
            {
                if (parts[i].Contains('*'))
                {
                    IEnumerable<string> dirs;
                    try { dirs = Directory.EnumerateDirectories(basePath, parts[i]).ToList(); }
                    catch (Exception) { continue; }
                    next.AddRange(dirs);
                }
                else
                {
                    next.Add(Path.Join(basePath, parts[i]));
                }
            }
            current = next;
        }

        foreach (var path in current)
            if (Directory.Exists(path)) yield return path.TrimEnd('\\');
    }
}
