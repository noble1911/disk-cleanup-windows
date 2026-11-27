# SpaceLens

A Windows app that shows where your disk space went, suggests what to clean, and diagnoses
why your PC is slow. In one tool it does three jobs:

- **Disk map**: a live treemap of your space, like SpaceSniffer or WinDirStat.
- **Cleanup advisor**: suggestions for what to remove.
- **Performance doctor**: watches CPU, memory, disk and network, and names the apps responsible.

Windows 10/11 only. Built with C# / WPF on .NET 8.

## Features

### Disk Map
- Scan **one drive, several drives, all drives, or any folder**.
- The multi-threaded scanner uses `FindFirstFileEx` with large fetch. On a warm cache it reads
  about 300,000 files per second (benchmark: 107 GB, 650k files in under 2 s).
- Live **squarified treemap** that fills in while the scan runs. Double-click a folder to zoom in,
  use the breadcrumbs to go back, and right-click for actions. You can colour it by folder or by file type.
- A folder tree sorted by size, with a share-of-parent bar for each item.
- Open items in Explorer, copy paths, or **move items to the Recycle Bin**. The app warns you
  before you touch Windows or Program Files.
- Accurate totals:
  - It never follows junctions or symlinks, so nothing is counted twice.
  - OneDrive "online-only" placeholders count as 0 bytes, because they take no local space.

### Cleanup Advisor
Every suggestion has a risk level: **Safe**, **Review first** or **Advanced**.

| Always checked | Checked when a disk scan is available |
|---|---|
| User and Windows temp files | `node_modules` folders (stale projects pre-selected) |
| Windows Update and Delivery Optimization caches | Build output next to a project file: `bin/obj`, `target`, `build` (Gradle/CMake), `out`, `.next`, `.venv`… |
| Crash dumps and error reports | Large files (500 MB+) untouched for 6+ months |
| Chrome / Edge / Brave / Firefox caches | Duplicate large files (size + sampled SHA-256 fingerprint) |
| GPU shader caches, Teams cache | Huge log and dump files |
| npm / Yarn / pnpm / pip / NuGet / Gradle / Maven / Cargo / Go caches | Oversized app caches in AppData |
| Old JetBrains IDE version folders | OneDrive files you could make online-only |
| Old installers and ISOs in Downloads | |
| Recycle Bin, hiberfil.sys, Windows.old, WinSxS cleanup | |
| Docker / WSL `.vhdx` disks that only grow | |

- **"Clean all safe items"** clears temp files, caches and the Recycle Bin in one click.
- Your own files are never pre-selected.
- The file-level rules skip `.git` folders and anything another suggestion already covers,
  so nothing is counted twice and no repository can be damaged.
- System commands (DISM, powercfg, Delivery Optimization) run in a visible PowerShell window
  that asks for admin rights, so you can see exactly what runs.
- There are also **Largest files** and **File types** tabs.

### System Monitor
- Live CPU, memory, disk and network tiles with 60-second charts.
- A table of every process:
  - CPU, private memory, disk I/O, network, TCP connections, threads and handles.
  - It can **group by app**, so the 30 `chrome.exe` processes show up as one row.
  - It can filter, end a task, open the file location, or look the process up online.
- Processes are read with one `NtQuerySystemInformation` call. That sees protected processes
  without admin rights and costs almost nothing.
- **Per-app network bandwidth** uses kernel ETW tracing when the app runs as administrator.
  Otherwise the table shows connection counts per app.

### Health Check
It looks at the last 30 seconds (up to 5 minutes of history) and explains, in plain language,
what is wrong and what to do about it:
- CPU saturation, apps hogging the CPU, single-threaded apps pinning one core, and high
  kernel/driver time.
- Memory pressure, commit-limit exhaustion, paging, apps using too much memory, and suspected
  **memory and handle leaks**.
- Disk saturation and the apps causing it, plus nearly full drives (with a link to the advisor).
- Network saturation and the apps causing it.
- Long uptime, the Power saver plan, Battery saver, too many startup apps, and process bloat.
- Built-in advice for about 60 well-known processes, for example:
  - Defender scanning your build folders.
  - The Search indexer.
  - WSL's `vmmem`.
  - Gradle daemons.
  - Browsers: it suggests the browser's own task manager rather than killing them.
- A health score, the top resource users, a list of startup apps, and system info.

## Build and run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet run --project src/SpaceLens

# Run the tests (core logic, live system checks, and headless loading of every page)
dotnet test

# Single-file release build -> publish\SpaceLens.exe (needs the .NET 8 Desktop Runtime)
dotnet publish src/SpaceLens -c Release -o publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The app starts as a normal user. **Restart as administrator** (in the sidebar) to:
- scan protected system folders,
- clean Windows temp and update caches,
- see per-app network bandwidth.

Optional benchmark against a real folder (read-only):

```powershell
$env:SPACELENS_BENCH_PATH = 'C:\Users'; dotnet test --filter Benchmark --logger "console;verbosity=detailed"
```

## Project layout

```
src/SpaceLens.Core   UI-free engine
  Scanning/          DiskScanner (parallel Win32 enumeration), FsNode tree, file categories
  Treemap/           Squarified layout algorithm
  Advisor/           CleanupAdvisor rules, KnownLocations, CleanupExecutor (delete / recycle / run)
  Monitoring/        SystemMonitor sampler, NtQuerySystemInformation reader, TCP table, ETW network tracer
  Diagnostics/       PerformanceDiagnostics rules engine, KnownProcesses advice, SystemInfo/startup items
  Native/            P/Invoke declarations
src/SpaceLens        WPF app (MVVM with CommunityToolkit.Mvvm)
  Controls/          TreemapControl (cached DrawingVisual + hover overlay), Sparkline
  ViewModels/ Views/ Disk Map, Cleanup Advisor, System Monitor, Health Check
tests/               xUnit: core logic + headless WPF smoke tests
```

## Known limitations
- **Hard links count once per link.** WinSxS therefore looks larger than it really is, as it
  does in every scanner. Use the WinSxS cleanup suggestion rather than deleting anything there.
- **GPU usage is not monitored yet.**
- **Per-app network bandwidth requires administrator rights.** This is a Windows restriction.
- **Disk activity uses English performance counter names.** On some localized Windows installs
  the disk tile may show "n/a".
- **Errors are logged** to `%LOCALAPPDATA%\SpaceLens\error.log`.
