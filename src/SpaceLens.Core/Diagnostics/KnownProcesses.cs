namespace SpaceLens.Core.Diagnostics;

/// <summary>What a well-known process is, and what to do when it misbehaves.</summary>
public sealed record ProcessAdvice(string FriendlyName, string What, string? CpuAdvice = null, string? MemoryAdvice = null, string? DiskAdvice = null, string? NetworkAdvice = null, bool IsSystem = false);

public static class KnownProcesses
{
    private const string BrowserCpu = "Find the busy tab or extension with the browser's own task manager (Shift+Esc in Chrome/Edge, about:processes in Firefox) and close it. Video, ads and web apps left open are the usual cause.";
    private const string BrowserMemory = "Every tab and extension is its own process. Close tabs you aren't using, remove extensions you don't need, and turn on tab sleeping (Edge: Settings → System and performance → Sleeping tabs; Chrome: Settings → Performance → Memory Saver).";

    private static readonly Dictionary<string, ProcessAdvice> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = new("Google Chrome", "Web browser", BrowserCpu, BrowserMemory, NetworkAdvice: "Check for downloads, video streams or sync in progress."),
        ["msedge.exe"] = new("Microsoft Edge", "Web browser", BrowserCpu, BrowserMemory, NetworkAdvice: "Check for downloads, video streams or sync in progress."),
        ["firefox.exe"] = new("Firefox", "Web browser", BrowserCpu, BrowserMemory),
        ["brave.exe"] = new("Brave", "Web browser", BrowserCpu, BrowserMemory),
        ["opera.exe"] = new("Opera", "Web browser", BrowserCpu, BrowserMemory),
        ["msedgewebview2.exe"] = new("Edge WebView2", "Embedded browser used by apps such as Teams, Outlook and Widgets",
            "Usually belongs to Teams, new Outlook or Widgets — restart the app that owns it.",
            "Each app using WebView2 runs its own copy; quit apps you aren't using (Widgets can be disabled in taskbar settings)."),

        ["MsMpEng.exe"] = new("Microsoft Defender Antivirus", "Real-time malware protection",
            "Defender is scanning — normal after updates or when many files change. If it's constant, add exclusions for build/output folders " +
            "(e.g. node_modules, .gradle, bin/obj) in Windows Security → Virus & threat protection → Exclusions, and schedule full scans for idle times.",
            DiskAdvice: "Scanning reads lots of files. Excluding developer build folders and VM disks greatly reduces this.", IsSystem: true),
        ["MpDefenderCoreService.exe"] = new("Defender core service", "Microsoft Defender component", IsSystem: true),
        ["SearchIndexer.exe"] = new("Windows Search indexer", "Builds the search index",
            "Indexing runs after many files change. Limit indexed locations in Settings → Privacy & security → Searching Windows, or exclude big folders.",
            DiskAdvice: "Exclude large or frequently-changing folders (build output, VMs) from indexing.", IsSystem: true),
        ["SearchHost.exe"] = new("Windows Search UI", "Start menu / taskbar search", IsSystem: true),
        ["TiWorker.exe"] = new("Windows Modules Installer Worker", "Installs Windows updates",
            "Windows Update is installing or cleaning up components. Let it finish (keep the PC on and plugged in); it stops by itself.",
            DiskAdvice: "Windows Update is installing — let it finish.", IsSystem: true),
        ["TrustedInstaller.exe"] = new("Windows Modules Installer", "Installs Windows updates", "Windows Update is working — let it finish.", IsSystem: true),
        ["MoUsoCoreWorker.exe"] = new("Windows Update orchestrator", "Checks for and schedules updates", "Windows Update is checking or downloading — let it finish.", IsSystem: true),
        ["WmiPrvSE.exe"] = new("WMI Provider Host", "Answers system-management queries for other programs",
            "Something is polling WMI heavily (often monitoring/RGB/vendor utilities). Close such utilities one by one to find it.", IsSystem: true),
        ["svchost.exe"] = new("Service Host", "Runs Windows services",
            "One of the services inside is busy. Check Task Manager → Details → right-click → 'Go to service(s)' to see which one.", IsSystem: true),
        ["System"] = new("System", "Windows kernel and drivers",
            "High CPU in 'System' usually means a driver problem (network, storage, audio) or heavy disk I/O. Update chipset, storage and network drivers.",
            MemoryAdvice: "Large kernel memory use can mean a driver leak; update drivers and restart.", IsSystem: true),
        ["Memory Compression"] = new("Memory Compression", "Compresses memory pages to avoid paging to disk",
            MemoryAdvice: "This grows when RAM is short. It's a symptom, not the cause: close memory-hungry apps or add more RAM.", IsSystem: true),
        ["dwm.exe"] = new("Desktop Window Manager", "Draws the desktop and windows",
            "High GPU/CPU here is often a graphics-driver issue or many animated windows. Update your graphics driver; reduce high refresh-rate multi-monitor setups if it persists.", IsSystem: true),
        ["explorer.exe"] = new("Windows Explorer", "Desktop, taskbar and file manager",
            "Explorer can get stuck on a slow network drive or a buggy shell extension. Restart it from Task Manager.", IsSystem: true),
        ["csrss.exe"] = new("Client Server Runtime", "Core Windows process", IsSystem: true),
        ["lsass.exe"] = new("Local Security Authority", "Handles sign-in and security", IsSystem: true),
        ["audiodg.exe"] = new("Windows Audio Device Graph", "Audio processing",
            "Audio enhancements or a buggy audio driver. Turn off audio enhancements in Sound settings or update the audio driver.", IsSystem: true),
        ["spoolsv.exe"] = new("Print Spooler", "Printing", "A stuck print job — clear the print queue.", IsSystem: true),
        ["SgrmBroker.exe"] = new("System Guard Runtime Monitor", "Windows security", IsSystem: true),

        ["OneDrive.exe"] = new("OneDrive", "Cloud file sync",
            "OneDrive is syncing many files. Pause syncing temporarily from the tray icon, or exclude big folders.",
            DiskAdvice: "Syncing reads/writes files. Pause sync if you need full performance right now.",
            NetworkAdvice: "OneDrive is uploading/downloading. Pause sync or set upload/download limits in OneDrive settings → Sync and backup → Advanced."),
        ["Dropbox.exe"] = new("Dropbox", "Cloud file sync", "Dropbox is syncing — pause it from the tray icon.", NetworkAdvice: "Set bandwidth limits in Dropbox preferences."),
        ["GoogleDriveFS.exe"] = new("Google Drive", "Cloud file sync", "Google Drive is syncing — pause it from the tray icon.", NetworkAdvice: "Set bandwidth limits in Google Drive preferences."),
        ["steam.exe"] = new("Steam", "Game store", NetworkAdvice: "Steam may be downloading updates. Limit download speed in Steam → Settings → Downloads."),
        ["steamwebhelper.exe"] = new("Steam web helper", "Steam's embedded browser", MemoryAdvice: "Steam's browser UI. Use Steam's small/library mode or close Steam when not gaming."),
        ["EpicGamesLauncher.exe"] = new("Epic Games Launcher", "Game store", NetworkAdvice: "May be downloading updates; throttle in launcher settings."),

        ["Teams.exe"] = new("Microsoft Teams (classic)", "Chat and meetings", "Turn off GPU hardware acceleration in Teams settings, or switch to new Teams.", "Teams is memory hungry — quit it when not needed."),
        ["ms-teams.exe"] = new("Microsoft Teams", "Chat and meetings", "Restart Teams; disable background effects during calls.", "Quit Teams when not needed, or use it in the browser."),
        ["Slack.exe"] = new("Slack", "Chat", "Restart Slack; disable unread animation and GIFs.", "Slack is Electron-based; quitting it or using the browser version saves memory."),
        ["Discord.exe"] = new("Discord", "Chat", "Disable hardware acceleration and in-game overlay in Discord settings.", "Discord is Electron-based; quit it when not needed."),
        ["Spotify.exe"] = new("Spotify", "Music", MemoryAdvice: "Lower the cache size in Spotify → Settings → Storage."),
        ["Code.exe"] = new("Visual Studio Code", "Code editor",
            "Usually an extension or the TypeScript/language server. Run 'Developer: Show Running Extensions' to find it.",
            "Each window and extension host uses memory. Close unused windows and disable unused extensions."),
        ["devenv.exe"] = new("Visual Studio", "IDE", "Background analysis/IntelliSense. Disable full-solution analysis for large solutions.", "Close unused solutions; unload projects you aren't working on."),
        ["idea64.exe"] = new("IntelliJ IDEA", "IDE", "Probably indexing. Let it finish, or exclude build/output folders from indexing (right-click → Mark Directory as → Excluded).",
            "Lower or raise -Xmx (Help → Change Memory Settings) to fit your RAM; close unused projects."),
        ["java.exe"] = new("Java", "Java application or build tool (Gradle/Maven daemon, IDE, server)",
            "Often a Gradle daemon or a dev server. Gradle daemons can be stopped with 'gradle --stop'.",
            "Gradle/Kotlin daemons stay in memory after builds. Run 'gradle --stop' or set org.gradle.daemon.idletimeout."),
        ["node.exe"] = new("Node.js", "JavaScript runtime (dev servers, build tools, language servers)",
            "Usually a dev server/watcher or build. Stop watchers you aren't using.", "Dev servers and TypeScript servers can grow large; restart them."),
        ["python.exe"] = new("Python", "Python program", "Check what script is running (see its command line in Task Manager → Details)."),
        ["dotnet.exe"] = new("dotnet", ".NET app or build server", "Build servers stay resident; 'dotnet build-server shutdown' stops them.", "'dotnet build-server shutdown' frees MSBuild/Razor/Roslyn servers."),
        ["vmmem"] = new("WSL / virtual machine memory", "Memory used by WSL 2 or Docker's Linux VM",
            "Something is busy inside WSL/Docker. Check with 'top' inside WSL or 'docker stats'.",
            "WSL 2 keeps memory for its file cache. Run 'wsl --shutdown' when you're done, or cap memory in %UserProfile%\\.wslconfig ([wsl2] memory=4GB)."),
        ["vmmemWSL"] = new("WSL memory", "Memory used by WSL 2",
            "Something is busy inside WSL. Check with 'top' inside WSL.",
            "Run 'wsl --shutdown' when done, or cap memory in %UserProfile%\\.wslconfig ([wsl2] memory=4GB)."),
        ["Docker Desktop.exe"] = new("Docker Desktop", "Container runtime", MemoryAdvice: "Limit Docker's resources in Settings → Resources, or quit Docker Desktop when not needed."),
        ["com.docker.backend.exe"] = new("Docker backend", "Container runtime", MemoryAdvice: "Quit Docker Desktop when not needed."),
        ["OUTLOOK.EXE"] = new("Outlook", "Email", "Outlook may be indexing or syncing a large mailbox; let it finish, or reduce 'Mail to keep offline'.", "Reduce cached mail ('Mail to keep offline') in account settings."),
        ["olk.exe"] = new("Outlook (new)", "Email"),
        ["WINWORD.EXE"] = new("Word", "Documents"),
        ["EXCEL.EXE"] = new("Excel", "Spreadsheets", "A big workbook recalculating; switch to manual calculation (Formulas → Calculation Options)."),
        ["PhoneExperienceHost.exe"] = new("Phone Link", "Connects your phone", "Turn off Phone Link if you don't use it (Settings → Bluetooth & devices → Mobile devices)."),
        ["Widgets.exe"] = new("Widgets", "Windows widgets board", MemoryAdvice: "Turn off Widgets in Settings → Personalization → Taskbar if you don't use them."),
        ["CompPkgSrv.exe"] = new("Component Package Support Server", "Windows Store app support", IsSystem: true),
        ["RuntimeBroker.exe"] = new("Runtime Broker", "Manages Store app permissions", "A Store app is misbehaving; restart recently used Store apps.", IsSystem: true),
        ["backgroundTaskHost.exe"] = new("Background Task Host", "Runs Store apps' background tasks", IsSystem: true),
        ["CCleaner64.exe"] = new("CCleaner", "Cleaning tool"),
        ["NVDisplay.Container.exe"] = new("NVIDIA display container", "NVIDIA driver helper", "Known to spin CPU at times; update the NVIDIA driver or restart the 'NVIDIA Display Container LS' service.", IsSystem: true),
    };

    public static ProcessAdvice? Get(string processName)
    {
        if (Map.TryGetValue(processName, out var advice)) return advice;
        // vmmem has no .exe extension; also accept names without extension.
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Map.TryGetValue(processName + ".exe", out advice)) return advice;
        return null;
    }

    public static bool IsBrowser(string processName) =>
        processName.ToLowerInvariant() is "chrome.exe" or "msedge.exe" or "firefox.exe" or "brave.exe" or "opera.exe" or "vivaldi.exe";

    /// <summary>Processes the user should never be offered to end.</summary>
    public static bool IsCritical(string processName) =>
        processName.ToLowerInvariant() is "system" or "system idle process" or "registry" or "smss.exe" or "csrss.exe" or "wininit.exe"
            or "winlogon.exe" or "services.exe" or "lsass.exe" or "lsaiso.exe" or "memory compression" or "secure system" or "dwm.exe"
            or "fontdrvhost.exe" or "svchost.exe" or "msmpeng.exe";
}
