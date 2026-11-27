using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using SpaceLens.Core.Native;

namespace SpaceLens.Core.Diagnostics;

public enum PowerPlan { Unknown, PowerSaver, Balanced, HighPerformance, UltimatePerformance, Custom }

public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    /// <summary>Where it is registered, e.g. "HKCU Run" or "Startup folder".</summary>
    public required string Location { get; init; }
    public bool Enabled { get; init; } = true;
    public bool AllUsers { get; init; }
}

public sealed class DriveSummary
{
    public required string Name { get; init; }
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public DriveType Type { get; init; }
    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? 100.0 * Used / Total : 0;
    public bool IsSystem { get; init; }
}

public sealed class SystemInfo
{
    public string OsName { get; init; } = "";
    public string OsBuild { get; init; } = "";
    public string CpuName { get; init; } = "";
    public int LogicalProcessors { get; init; }
    public long TotalMemory { get; init; }
    public PowerPlan PowerPlan { get; init; }
    public bool OnBattery { get; init; }
    public bool BatterySaver { get; init; }
    public bool IsElevated { get; init; }
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];
    public IReadOnlyList<DriveSummary> Drives { get; init; } = [];

    public static bool CurrentProcessIsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static SystemInfo Collect()
    {
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref mem);
        Win32.GetSystemPowerStatus(out var power);

        return new SystemInfo
        {
            OsName = ReadReg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") is { } product
                ? FixWindows11Name(product)
                : RuntimeInformation.OSDescription,
            OsBuild = $"{ReadReg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")} (build {Environment.OSVersion.Version.Build})",
            CpuName = (ReadReg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "Unknown CPU").Trim(),
            LogicalProcessors = Environment.ProcessorCount,
            TotalMemory = (long)mem.ullTotalPhys,
            PowerPlan = ReadPowerPlan(),
            OnBattery = power.ACLineStatus == 0,
            BatterySaver = power.SystemStatusFlag == 1,
            IsElevated = CurrentProcessIsElevated(),
            StartupItems = ReadStartupItems(),
            Drives = ReadDrives(),
        };
    }

    // The registry still says "Windows 10" on Windows 11.
    private static string FixWindows11Name(string product) =>
        Environment.OSVersion.Version.Build >= 22000 ? product.Replace("Windows 10", "Windows 11") : product;

    private static string? ReadReg(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value)?.ToString();
        }
        catch (Exception) { return null; }
    }

    public static IReadOnlyList<DriveSummary> ReadDrives()
    {
        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
        var list = new List<DriveSummary>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
            try
            {
                if (!d.IsReady) continue;
                list.Add(new DriveSummary
                {
                    Name = d.Name,
                    Label = d.VolumeLabel,
                    Format = d.DriveFormat,
                    Type = d.DriveType,
                    Total = d.TotalSize,
                    Free = d.TotalFreeSpace,
                    IsSystem = d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (Exception) { }
        }
        return list;
    }

    private static PowerPlan ReadPowerPlan()
    {
        try
        {
            if (Win32.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return PowerPlan.Unknown;
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(ptr);
                return guid.ToString().ToLowerInvariant() switch
                {
                    "a1841308-3541-4fab-bc81-f71556f20b4a" => PowerPlan.PowerSaver,
                    "381b4222-f694-41f0-9685-ff5bb260df2e" => PowerPlan.Balanced,
                    "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => PowerPlan.HighPerformance,
                    "e9a42b02-d5df-448d-aa00-03f14749eb61" => PowerPlan.UltimatePerformance,
                    _ => PowerPlan.Custom,
                };
            }
            finally
            {
                Win32.LocalFree(ptr);
            }
        }
        catch (Exception) { return PowerPlan.Unknown; }
    }

    public static IReadOnlyList<StartupItem> ReadStartupItems()
    {
        var items = new List<StartupItem>();

        void FromRunKey(RegistryKey hive, string path, string approvedPath, string location, bool allUsers)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) return;
                using var approved = hive.OpenSubKey(approvedPath);
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    items.Add(new StartupItem
                    {
                        Name = name,
                        Command = key.GetValue(name)?.ToString() ?? "",
                        Location = location,
                        AllUsers = allUsers,
                        Enabled = IsApproved(approved, name),
                    });
                }
            }
            catch (Exception) { }
        }

        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string approvedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string approvedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        const string approvedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        FromRunKey(Registry.CurrentUser, run, approvedRun, "Registry (current user)", false);
        FromRunKey(Registry.LocalMachine, run, approvedRun, "Registry (all users)", true);
        FromRunKey(Registry.LocalMachine, run32, approvedRun32, "Registry (all users, 32-bit)", true);

        void FromFolder(string folder, RegistryKey hive, bool allUsers)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                using var approved = hive.OpenSubKey(approvedFolder);
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(new StartupItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Location = allUsers ? "Startup folder (all users)" : "Startup folder",
                        AllUsers = allUsers,
                        Enabled = IsApproved(approved, fileName),
                    });
                }
            }
            catch (Exception) { }
        }

        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, false);
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, true);

        return items.OrderBy(i => !i.Enabled).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Task Manager stores "disabled" as an odd first byte in StartupApproved.</summary>
    private static bool IsApproved(RegistryKey? approved, string name)
    {
        if (approved?.GetValue(name) is byte[] { Length: > 0 } data) return (data[0] & 1) == 0;
        return true;
    }
}
