using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SpaceLens.Core.Native;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Core.Advisor;

/// <summary>
/// Works out where space can be reclaimed. Known cache/temp locations are always checked;
/// when a scan result is available it also looks for project build output, node_modules,
/// duplicates, and large files nobody has touched for a long time.
/// </summary>
public sealed partial class CleanupAdvisor
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    public static readonly TimeSpan StaleProjectAge = TimeSpan.FromDays(90);
    public static readonly TimeSpan OldFileAge = TimeSpan.FromDays(180);

    private readonly Func<DateTime> _now;

    public CleanupAdvisor(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public Task<IReadOnlyList<CleanupSuggestion>> AnalyzeAsync(ScanResult? scan, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Analyze(scan, progress, ct), ct);

    public IReadOnlyList<CleanupSuggestion> Analyze(ScanResult? scan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<CleanupSuggestion>();

        void Run(string what, Func<IEnumerable<CleanupSuggestion>> rule)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(what);
            try { results.AddRange(rule()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* one failing rule must not hide the others */ }
        }

        Run("Checking temporary files and caches…", () => KnownLocationRules(scan, ct));
        Run("Checking the Recycle Bin…", RecycleBinRule);
        Run("Checking Windows system files…", () => SystemRules(scan, ct));
        Run("Checking Downloads…", () => DownloadsRule(ct));
        Run("Checking old IDE versions…", () => JetBrainsRule(scan, ct));
        Run("Checking virtual disks…", VirtualDiskRule);
        Run("Checking OneDrive…", () => OneDriveRule(scan));

        if (scan is not null)
        {
            Run("Looking for node_modules and build output…", () => ProjectRules(scan, ct));

            // Folders already offered as a whole are skipped by the file-level rules, so nothing is counted twice.
            var covered = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
            foreach (var item in results.Where(r => r.Id is "node-modules" or "build-output").SelectMany(r => r.Items))
                if (scan.Find(item.Path) is { } node) covered.Add(node);
            var userFiles = UserFileFilter(scan, covered);

            Run("Looking for large, old files…", () => LargeOldFilesRule(scan, userFiles));
            Run("Looking for large log files…", () => LogFilesRule(scan, userFiles));
            Run("Looking for big application caches…", () => AppCacheRule(scan));
            Run("Looking for duplicate files…", () => DuplicatesRule(scan, userFiles, ct));
        }

        return results
            .Where(r => r.Bytes > 0 || r.Kind is SuggestionKind.RunCommand or SuggestionKind.OpenLocation)
            .OrderBy(r => r.Risk)
            .ThenByDescending(r => r.Bytes)
            .ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static long SizeOf(string path, ScanResult? scan, CancellationToken ct)
    {
        if (scan is not null && scan.Covers(path))
        {
            var node = scan.Find(path);
            if (node is not null && node.IsRead && !node.AccessDenied) return node.Size;
            if (node is null && !scan.Cancelled) return 0;
        }
        return DiskScanner.MeasureDirectory(path, ct);
    }

    private static readonly Lazy<string[]> SystemPrefixes = new(() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => p.TrimEnd('\\') + "\\")
        .ToArray());

    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery", "Windows.old", "$Windows.~BT", "$Windows.~WS",
    };

    private static readonly HashSet<string> SystemRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
    };

    /// <summary>True for paths inside Windows, Program Files or ProgramData.</summary>
    public static bool IsSystemPath(string path)
    {
        foreach (var prefix in SystemPrefixes.Value)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static bool IsDriveRoot(FsNode n) =>
        n.Kind == NodeKind.Directory && n.Name.Length == 3 && n.Name[1] == ':' && n.Name[2] == '\\';

    /// <summary>Version-control metadata and dependency folders: files inside must never be offered one by one.</summary>
    private static readonly HashSet<string> NeverDescendNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules",
    };

    /// <summary>
    /// Builds a filter for rules that look at the user's own files: it skips Windows, Program Files,
    /// ProgramData, system folders at the root of each drive, version-control metadata (deleting a
    /// "duplicate" git pack file would corrupt the repository) and any folder in <paramref name="covered"/>.
    /// </summary>
    private static Func<FsNode, bool> UserFileFilter(ScanResult scan, HashSet<FsNode>? covered = null, bool allowDependencyFolders = false)
    {
        var systemNodes = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
        foreach (var prefix in SystemPrefixes.Value)
            if (scan.Find(prefix.TrimEnd('\\')) is { } node) systemNodes.Add(node);

        return node =>
            !systemNodes.Contains(node) &&
            !(covered?.Contains(node) ?? false) &&
            !(NeverDescendNames.Contains(node.Name) && !(allowDependencyFolders && node.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) &&
            !(node.Parent is { } parent && IsDriveRoot(parent) && SystemFolderNames.Contains(node.Name));
    }

    private static bool IsRootSystemFile(FsNode file) =>
        file.Parent is { } p && IsDriveRoot(p) && SystemRootFiles.Contains(file.Name);

    // ------------------------------------------------------------------ rules

    private IEnumerable<CleanupSuggestion> KnownLocationRules(ScanResult? scan, CancellationToken ct)
    {
        foreach (var location in KnownLocations.All)
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<SuggestionItem>();
            foreach (var pattern in location.Patterns)
            foreach (var path in KnownLocations.Expand(pattern))
            {
                long size = SizeOf(path, scan, ct);
                if (size > 0) items.Add(new SuggestionItem { Path = path, Bytes = size, LastWriteUtc = SafeLastWrite(path) });
            }

            long total = items.Sum(i => i.Bytes);
            if (total < location.MinimumBytes) continue;

            yield return new CleanupSuggestion
            {
                Id = location.Id,
                Title = location.Title,
                Category = location.Category,
                Description = location.Description,
                Risk = location.Risk,
                Kind = SuggestionKind.CleanFolderContents,
                RequiresAdmin = location.RequiresAdmin,
                Bytes = total,
                Items = items.OrderByDescending(i => i.Bytes).ToList(),
            };
        }

        // Delivery Optimization keeps its cache in a protected folder; use the supported cmdlet.
        string doCache = Environment.ExpandEnvironmentVariables(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
        long doSize = Directory.Exists(doCache) ? DiskScanner.MeasureDirectory(doCache, ct) : 0;
        if (doSize >= 100 * MB)
        {
            yield return new CleanupSuggestion
            {
                Id = "delivery-optimization",
                Title = "Delivery Optimization cache",
                Category = "System",
                Description = "Update files Windows keeps to share with other PCs. Clearing it is safe; Windows re-downloads only what it needs.",
                Risk = RiskLevel.Safe,
                Kind = SuggestionKind.RunCommand,
                Command = "Delete-DeliveryOptimizationCache -Force",
                RequiresAdmin = true,
                Bytes = doSize,
                Items = [new SuggestionItem { Path = doCache, Bytes = doSize }],
            };
        }
    }

    private static IEnumerable<CleanupSuggestion> RecycleBinRule()
    {
        var info = new Win32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        if (Win32.SHQueryRecycleBinW(null, ref info) != 0 || info.i64Size < MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "recycle-bin",
            Title = "Recycle Bin",
            Category = "Recycle Bin",
            Description = $"{info.i64NumItems:N0} deleted items are still using space. Emptying the Recycle Bin removes them for good.",
            Risk = RiskLevel.Safe,
            Kind = SuggestionKind.EmptyRecycleBin,
            Bytes = info.i64Size,
        };
    }

    private static IEnumerable<CleanupSuggestion> SystemRules(ScanResult? scan, CancellationToken ct)
    {
        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";

        long hiber = DiskScanner.FileSize(Path.Join(systemDrive, "hiberfil.sys"));
        if (hiber >= GB)
        {
            yield return new CleanupSuggestion
            {
                Id = "hibernation",
                Title = "Hibernation file (hiberfil.sys)",
                Category = "System",
                Description = "Windows reserves this file for hibernate and Fast Startup. Turning hibernation off deletes it, " +
                              "but you lose hibernate and Fast Startup (boot may be a few seconds slower). " +
                              "On laptops that you hibernate, keep it — or shrink it with 'powercfg /h /type reduced'.",
                Risk = RiskLevel.Advanced,
                Kind = SuggestionKind.RunCommand,
                Command = "powercfg /hibernate off",
                RequiresAdmin = true,
                Bytes = hiber,
                Items = [new SuggestionItem { Path = Path.Join(systemDrive, "hiberfil.sys"), Bytes = hiber }],
            };
        }

        var upgradeLeftovers = new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS" }
            .Select(n => Path.Join(systemDrive, n))
            .Where(Directory.Exists)
            .Select(p => new SuggestionItem { Path = p, Bytes = SizeOf(p, scan, ct) })
            .ToList();
        if (upgradeLeftovers.Count > 0)
        {
            yield return new CleanupSuggestion
            {
                Id = "windows-old",
                Title = "Previous Windows installation",
                Category = "System",
                Description = "Files kept from a Windows upgrade so you can roll back. Once you're happy with the upgrade, remove them with " +
                              "Disk Cleanup → 'Clean up system files' → 'Previous Windows installation(s)'. You will no longer be able to roll back.",
                Risk = RiskLevel.Advanced,
                Kind = SuggestionKind.OpenLocation,
                Command = "cleanmgr.exe",
                Bytes = upgradeLeftovers.Sum(i => i.Bytes),
                Items = upgradeLeftovers,
            };
        }

        string winsxs = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
        if (Directory.Exists(winsxs))
        {
            yield return new CleanupSuggestion
            {
                Id = "component-store",
                Title = "Windows component store (WinSxS) cleanup",
                Category = "System",
                Description = "Removes superseded versions of updated Windows components. Often frees 1–5 GB on PCs that haven't been cleaned for a while. " +
                              "Note: WinSxS looks bigger than it is in any scanner because most of its files are hard links shared with System32. " +
                              "Afterwards you can't uninstall updates that are already installed.",
                Risk = RiskLevel.Advanced,
                Kind = SuggestionKind.RunCommand,
                Command = "Dism.exe /Online /Cleanup-Image /StartComponentCleanup",
                RequiresAdmin = true,
                Bytes = 0,
            };
        }
    }

    private static readonly HashSet<string> DownloadClutterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".iso", ".img", ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".dmg", ".cab",
    };

    private IEnumerable<CleanupSuggestion> DownloadsRule(CancellationToken ct)
    {
        string? downloads = GetDownloadsFolder();
        if (downloads is null || !Directory.Exists(downloads)) yield break;

        var cutoff = _now() - TimeSpan.FromDays(30);
        var items = new List<SuggestionItem>();
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(downloads).EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception) { yield break; }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!DownloadClutterExtensions.Contains(file.Extension) || file.Length < MB) continue;
            var last = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
            if (last > cutoff) continue;
            items.Add(new SuggestionItem
            {
                Path = file.FullName,
                Bytes = file.Length,
                LastWriteUtc = last,
                Note = file.Extension.ToLowerInvariant() switch
                {
                    ".exe" or ".msi" or ".msix" or ".msixbundle" or ".appx" => "Installer",
                    ".iso" or ".img" or ".dmg" => "Disk image",
                    _ => "Archive",
                },
            });
        }

        long total = items.Sum(i => i.Bytes);
        if (total < 50 * MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "downloads-clutter",
            Title = "Old installers and archives in Downloads",
            Category = "Downloads",
            Description = "Setup files, ISOs and archives you downloaded more than 30 days ago. Installed programs don't need their installer, " +
                          "and archives are usually already extracted.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.RecycleItems,
            Bytes = total,
            Items = items.OrderByDescending(i => i.Bytes).ToList(),
        };
    }

    [GeneratedRegex(@"^(?<product>[A-Za-z]+?)(?<version>\d{4}\.\d+)$")]
    private static partial Regex JetBrainsFolder();

    private static IEnumerable<CleanupSuggestion> JetBrainsRule(ScanResult? scan, CancellationToken ct)
    {
        var roots = new[] { @"%LOCALAPPDATA%\JetBrains", @"%APPDATA%\JetBrains" }
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(Directory.Exists);

        var found = new List<(string Product, Version Version, string Path)>();
        foreach (var root in roots)
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception) { continue; }
            foreach (var dir in dirs)
            {
                var match = JetBrainsFolder().Match(Path.GetFileName(dir));
                if (match.Success && Version.TryParse(match.Groups["version"].Value, out var v))
                    found.Add((match.Groups["product"].Value, v, dir));
            }
        }

        var items = new List<SuggestionItem>();
        foreach (var product in found.GroupBy(f => f.Product, StringComparer.OrdinalIgnoreCase))
        {
            var latest = product.Max(p => p.Version)!;
            foreach (var old in product.Where(p => p.Version < latest))
            {
                long size = SizeOf(old.Path, scan, ct);
                if (size > 0)
                    items.Add(new SuggestionItem { Path = old.Path, Bytes = size, Note = $"{product.Key} {old.Version} (latest installed: {latest})" });
            }
        }

        long total = items.Sum(i => i.Bytes);
        if (total < 100 * MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "jetbrains-old",
            Title = "Old JetBrains IDE versions' caches and settings",
            Category = "Developer caches",
            Description = "Each IntelliJ/WebStorm/PyCharm/… upgrade leaves the previous version's caches, indexes and logs behind. " +
                          "Newer versions have already imported your settings, so these folders are dead weight.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.RecycleItems,
            Bytes = total,
            Items = items.OrderByDescending(i => i.Bytes).ToList(),
        };
    }

    private static IEnumerable<CleanupSuggestion> VirtualDiskRule()
    {
        var patterns = new[]
        {
            @"%LOCALAPPDATA%\Docker\wsl\*", @"%LOCALAPPDATA%\Docker\wsl\*\*",
            @"%LOCALAPPDATA%\Packages\*\LocalState", @"%LOCALAPPDATA%\wsl\*",
        };
        var items = new List<SuggestionItem>();
        foreach (var pattern in patterns)
        foreach (var dir in KnownLocations.Expand(pattern))
        {
            IEnumerable<string> vhds;
            try { vhds = Directory.EnumerateFiles(dir, "*.vhdx").ToList(); }
            catch (Exception) { continue; }
            foreach (var vhd in vhds)
            {
                long size = DiskScanner.FileSize(vhd);
                if (size > 0 && items.All(i => !i.Path.Equals(vhd, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new SuggestionItem { Path = vhd, Bytes = size, Recommended = false });
            }
        }

        long total = items.Sum(i => i.Bytes);
        if (total < 5 * GB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "virtual-disks",
            Title = "Docker / WSL virtual disks",
            Category = "Developer caches",
            Description = "WSL and Docker Desktop store Linux files in .vhdx disks that grow but never shrink on their own. " +
                          "Free space inside first ('docker system prune -a', delete unused files in WSL), then run 'wsl --shutdown' and compact: " +
                          "'wsl --manage <distro> --set-sparse true' or Optimize-VHD. Only part of this size is reclaimable.",
            Risk = RiskLevel.Advanced,
            Kind = SuggestionKind.OpenLocation,
            IsEstimate = true,
            Bytes = total,
            Items = items.OrderByDescending(i => i.Bytes).ToList(),
        };
    }

    private static IEnumerable<CleanupSuggestion> OneDriveRule(ScanResult? scan)
    {
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrEmpty(oneDrive) || !Directory.Exists(oneDrive) || scan is null || !scan.Covers(oneDrive)) yield break;

        var node = scan.Find(oneDrive);
        if (node is null || node.Size < 5 * GB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "onedrive",
            Title = "OneDrive files stored on this PC",
            Category = "Cloud storage",
            Description = "These files are already backed up in OneDrive. Right-click the OneDrive folder (or a big sub-folder) and choose " +
                          "'Free up space' to keep them online-only; they download again when you open them.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.OpenLocation,
            IsEstimate = true,
            Bytes = node.Size,
            Items = node.Children.Where(c => c.IsContainer && c.Size >= 500 * MB).Take(10)
                .Select(c => new SuggestionItem { Path = c.FullPath, Bytes = c.Size, LastWriteUtc = c.LastWriteUtc }).ToList(),
        };
    }

    // ---- scan-based rules

    private static readonly Dictionary<string, Func<string, string?>> BuildOutputMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bin"] = p => HasFile(p, "*.csproj", "*.fsproj", "*.vbproj") ? ".NET build output" : null,
        ["obj"] = p => HasFile(p, "*.csproj", "*.fsproj", "*.vbproj") ? ".NET intermediate output" : null,
        ["target"] = p => HasFile(p, "pom.xml") ? "Maven build output" : HasFile(p, "Cargo.toml") ? "Rust build output" : HasFile(p, "build.sbt") ? "sbt build output" : null,
        ["build"] = p => HasFile(p, "build.gradle", "build.gradle.kts") ? "Gradle build output" : HasFile(p, "CMakeLists.txt") ? "CMake build output" : null,
        ["cmake-build-debug"] = p => HasFile(p, "CMakeLists.txt") ? "CLion build output" : null,
        ["cmake-build-release"] = p => HasFile(p, "CMakeLists.txt") ? "CLion build output" : null,
        [".gradle"] = p => HasFile(p, "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts") ? "Gradle project cache" : null,
        ["out"] = p => HasFile(p, "*.iml") || Directory.Exists(Path.Join(p, ".idea")) ? "IntelliJ build output" : null,
        [".next"] = p => HasFile(p, "package.json") ? "Next.js build cache" : null,
        [".nuxt"] = p => HasFile(p, "package.json") ? "Nuxt build cache" : null,
        [".angular"] = p => HasFile(p, "angular.json") ? "Angular build cache" : null,
        [".parcel-cache"] = p => HasFile(p, "package.json") ? "Parcel cache" : null,
        [".turbo"] = p => HasFile(p, "package.json", "turbo.json") ? "Turborepo cache" : null,
        [".venv"] = p => HasFile(Path.Join(p, ".venv"), "pyvenv.cfg") ? "Python virtual environment" : null,
        ["venv"] = p => HasFile(Path.Join(p, "venv"), "pyvenv.cfg") ? "Python virtual environment" : null,
    };

    private static bool HasFile(string dir, params string[] patterns)
    {
        try
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Contains('*'))
                {
                    if (Directory.EnumerateFiles(dir, pattern).Any()) return true;
                }
                else if (File.Exists(Path.Join(dir, pattern)))
                {
                    return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    private IEnumerable<CleanupSuggestion> ProjectRules(ScanResult scan, CancellationToken ct)
    {
        var nodeModules = new List<SuggestionItem>();
        var buildOutputs = new List<SuggestionItem>();
        var stale = _now() - StaleProjectAge;

        // node_modules must stay visible here (it is what this rule looks for); .git and system folders are still skipped.
        var descend = UserFileFilter(scan, allowDependencyFolders: true);
        var stack = new Stack<FsNode>();
        stack.Push(scan.Root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = stack.Pop();
            foreach (var child in node.Children)
            {
                if (!child.IsContainer || !descend(child)) continue;

                if (child.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    if (child.Size >= 10 * MB)
                    {
                        var project = node.FullPath;
                        var lastUsed = ProjectLastTouched(node);
                        nodeModules.Add(new SuggestionItem
                        {
                            Path = child.FullPath,
                            Bytes = child.Size,
                            LastWriteUtc = lastUsed,
                            Recommended = lastUsed < stale,
                            Note = lastUsed < stale ? $"Project untouched since {lastUsed.ToLocalTime():d}" : "Project in active use",
                        });
                    }
                    continue; // never walk into node_modules
                }

                if (BuildOutputMarkers.TryGetValue(child.Name, out var marker) && child.Size >= 10 * MB)
                {
                    var kind = marker(node.FullPath);
                    if (kind is not null)
                    {
                        var lastUsed = ProjectLastTouched(node);
                        buildOutputs.Add(new SuggestionItem
                        {
                            Path = child.FullPath,
                            Bytes = child.Size,
                            LastWriteUtc = lastUsed,
                            Recommended = lastUsed < stale,
                            Note = kind + (lastUsed < stale ? $" · untouched since {lastUsed.ToLocalTime():d}" : " · active project"),
                        });
                        continue;
                    }
                }

                stack.Push(child);
            }
        }

        if (nodeModules.Sum(i => i.Bytes) >= 100 * MB)
        {
            yield return new CleanupSuggestion
            {
                Id = "node-modules",
                Title = $"node_modules folders ({nodeModules.Count})",
                Category = "Developer projects",
                Description = "JavaScript dependencies that 'npm install' / 'yarn' / 'pnpm install' can recreate at any time. " +
                              "Projects you haven't touched in 90 days are pre-selected.",
                Risk = RiskLevel.Review,
                Kind = SuggestionKind.RecycleItems,
                Bytes = nodeModules.Sum(i => i.Bytes),
                Items = nodeModules.OrderByDescending(i => i.Bytes).ToList(),
            };
        }

        if (buildOutputs.Sum(i => i.Bytes) >= 100 * MB)
        {
            yield return new CleanupSuggestion
            {
                Id = "build-output",
                Title = $"Project build output ({buildOutputs.Count} folders)",
                Category = "Developer projects",
                Description = "Compiled output (bin/obj, target, build, out, .next, virtual envs…) next to a project file. " +
                              "A rebuild recreates them. Projects you haven't touched in 90 days are pre-selected.",
                Risk = RiskLevel.Review,
                Kind = SuggestionKind.RecycleItems,
                Bytes = buildOutputs.Sum(i => i.Bytes),
                Items = buildOutputs.OrderByDescending(i => i.Bytes).ToList(),
            };
        }
    }

    /// <summary>Most recent write time among a project folder and its direct children (excluding dependency folders).</summary>
    private static DateTime ProjectLastTouched(FsNode project)
    {
        var latest = project.LastWriteUtc;
        foreach (var c in project.Children)
        {
            if (c.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.LastWriteUtc > latest) latest = c.LastWriteUtc;
        }
        return latest;
    }

    private IEnumerable<CleanupSuggestion> LargeOldFilesRule(ScanResult scan, Func<FsNode, bool> userFiles)
    {
        var cutoff = _now() - OldFileAge;
        var items = scan.Root.Descendants(userFiles)
            .Where(n => n.Kind == NodeKind.File && n.Size >= 500 * MB && n.LastWriteUtc != DateTime.MinValue && n.LastWriteUtc < cutoff)
            .Where(n => !IsRootSystemFile(n) && !n.Name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Size)
            .Take(100)
            .Select(n => new SuggestionItem
            {
                Path = n.FullPath,
                Bytes = n.Size,
                LastWriteUtc = n.LastWriteUtc,
                Recommended = false,
                Note = FileCategories.Of(n.Extension).ToString(),
            })
            .ToList();

        long total = items.Sum(i => i.Bytes);
        if (total < GB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "large-old-files",
            Title = $"Large files not changed in 6+ months ({items.Count})",
            Category = "Large & old files",
            Description = "Big files (500 MB+) outside Windows and Program Files that haven't been modified for over six months — " +
                          "old videos, disk images, backups, exports. Move them to external/cloud storage or delete the ones you no longer need. " +
                          "Nothing is pre-selected.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.RecycleItems,
            Bytes = total,
            Items = items,
        };
    }

    private static IEnumerable<CleanupSuggestion> LogFilesRule(ScanResult scan, Func<FsNode, bool> userFiles)
    {
        var items = scan.Root.Descendants(userFiles)
            .Where(n => n.Kind == NodeKind.File && n.Size >= 100 * MB && !IsRootSystemFile(n))
            .Where(n => n.Extension is "log" or "etl" or "dmp" or "mdmp" or "hdmp" || n.Name.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Size)
            .Take(50)
            .Select(n => new SuggestionItem { Path = n.FullPath, Bytes = n.Size, LastWriteUtc = n.LastWriteUtc })
            .ToList();

        long total = items.Sum(i => i.Bytes);
        if (total < 200 * MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "large-logs",
            Title = $"Large log and dump files ({items.Count})",
            Category = "Temporary files",
            Description = "Log, trace and memory-dump files of 100 MB or more. Programs recreate logs as needed; " +
                          "a log that keeps growing can point to an app stuck in an error loop.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.RecycleItems,
            Bytes = total,
            Items = items,
        };
    }

    private static readonly HashSet<string> CacheFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "caches", "cache2", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs", "ShaderCache", "webcache", "HttpCache",
    };

    private static IEnumerable<CleanupSuggestion> AppCacheRule(ScanResult scan)
    {
        var appDataRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }
            .Where(p => !string.IsNullOrEmpty(p) && scan.Covers(p))
            .ToList();
        if (appDataRoots.Count == 0) yield break;

        var known = KnownLocations.All.SelectMany(l => l.Patterns).SelectMany(KnownLocations.Expand).ToList();
        bool Covered(string path) => known.Any(k =>
            path.StartsWith(k, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));

        var items = new List<SuggestionItem>();
        foreach (var root in appDataRoots)
        {
            var rootNode = scan.Find(root);
            if (rootNode is null) continue;
            foreach (var node in rootNode.Descendants(n => !CacheFolderNames.Contains(n.Name)))
            {
                if (!node.IsContainer || !CacheFolderNames.Contains(node.Name) || node.Size < 300 * MB) continue;
                var path = node.FullPath;
                if (Covered(path)) continue;
                string app = path[(root.Length + 1)..].Split('\\')[0];
                items.Add(new SuggestionItem { Path = path, Bytes = node.Size, LastWriteUtc = node.LastWriteUtc, Note = $"{app} — close it first" });
            }
        }

        long total = items.Sum(i => i.Bytes);
        if (total < 300 * MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "app-caches",
            Title = $"Large application caches ({items.Count})",
            Category = "App caches",
            Description = "Cache folders inside AppData that have grown past 300 MB. Apps rebuild caches on demand, " +
                          "but close the app before clearing its cache.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.CleanFolderContents,
            Bytes = total,
            Items = items.OrderByDescending(i => i.Bytes).ToList(),
        };
    }

    private static IEnumerable<CleanupSuggestion> DuplicatesRule(ScanResult scan, Func<FsNode, bool> userFiles, CancellationToken ct)
    {
        const int maxCandidates = 600;
        var bySize = scan.Root.Descendants(userFiles)
            .Where(n => n.Kind == NodeKind.File && n.Size >= 50 * MB && !IsRootSystemFile(n))
            .GroupBy(n => n.Size)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key)
            .ToList();

        var items = new List<SuggestionItem>();
        int hashed = 0;
        foreach (var group in bySize)
        {
            if (hashed > maxCandidates) break;
            var byHash = new Dictionary<string, List<FsNode>>();
            foreach (var file in group)
            {
                ct.ThrowIfCancellationRequested();
                hashed++;
                var hash = SampledHash(file.FullPath, file.Size);
                if (hash is null) continue;
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                list.Add(file);
            }

            foreach (var copies in byHash.Values.Where(l => l.Count > 1))
            {
                // Keep the copy with the shortest path (usually the "original" location).
                var keep = copies.OrderBy(c => c.FullPath.Length).ThenBy(c => c.LastWriteUtc).First();
                foreach (var dup in copies.Where(c => !ReferenceEquals(c, keep)))
                {
                    items.Add(new SuggestionItem
                    {
                        Path = dup.FullPath,
                        Bytes = dup.Size,
                        LastWriteUtc = dup.LastWriteUtc,
                        Note = "Same content as " + keep.FullPath,
                    });
                }
            }
        }

        long total = items.Sum(i => i.Bytes);
        if (total < 100 * MB) yield break;

        yield return new CleanupSuggestion
        {
            Id = "duplicates",
            Title = $"Duplicate large files ({items.Count} extra copies)",
            Category = "Duplicates",
            Description = "Files of 50 MB+ that have identical size and content fingerprints. One copy of each (the one with the shortest path) " +
                          "is kept; the extra copies are listed here.",
            Risk = RiskLevel.Review,
            Kind = SuggestionKind.RecycleItems,
            Bytes = total,
            Items = items.OrderByDescending(i => i.Bytes).ToList(),
        };
    }

    /// <summary>Fingerprint from size plus 3 × 256 KB samples (start, middle, end). Fast even for multi-GB files.</summary>
    internal static string? SampledHash(string path, long size)
    {
        const int chunk = 256 * 1024;
        try
        {
            using var stream = new FileStream(Win32.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(size));
            var buffer = new byte[chunk];
            foreach (long offset in new[] { 0, Math.Max(0, size / 2 - chunk / 2), Math.Max(0, size - chunk) })
            {
                stream.Position = offset;
                int read = stream.ReadAtLeast(buffer, Math.Min(chunk, (int)Math.Min(int.MaxValue, size - offset)), throwOnEndOfStream: false);
                sha.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch (Exception) { return DateTime.MinValue; }
    }

    public static string? GetDownloadsFolder()
    {
        if (Win32.SHGetKnownFolderPath(Win32.FOLDERID_Downloads, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Win32.CoTaskMemFree(ptr); }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Join(profile, "Downloads");
    }
}
