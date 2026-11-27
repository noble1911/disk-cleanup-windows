using System.Diagnostics;
using SpaceLens.Core.Native;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Core.Advisor;

public static class CleanupExecutor
{
    /// <summary>
    /// Carries out a suggestion. <paramref name="selectedPaths"/> limits the work to items the user ticked;
    /// pass null to use every item.
    /// </summary>
    public static Task<CleanupOutcome> ExecuteAsync(CleanupSuggestion suggestion, IReadOnlyCollection<string>? selectedPaths, CancellationToken ct = default)
    {
        var paths = (selectedPaths ?? suggestion.Items.Select(i => i.Path).ToList()).ToList();
        return suggestion.Kind switch
        {
            SuggestionKind.CleanFolderContents => Task.Run(() => CleanFolderContents(paths, ct), ct),
            SuggestionKind.RecycleItems => Task.Run(() => RecycleItems(paths), ct),
            SuggestionKind.EmptyRecycleBin => Task.Run(EmptyRecycleBin, ct),
            SuggestionKind.RunCommand => Task.FromResult(RunCommand(suggestion.Command!, suggestion.RequiresAdmin)),
            _ => Task.FromResult(Open(suggestion.Command ?? paths.FirstOrDefault())),
        };
    }

    /// <summary>Permanently deletes everything inside each folder, skipping anything locked or in use.</summary>
    public static CleanupOutcome CleanFolderContents(IEnumerable<string> folders, CancellationToken ct = default)
    {
        long freed = 0;
        int removed = 0, failures = 0;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            DeleteChildren(new DirectoryInfo(folder), ref freed, ref removed, ref failures, ct);
        }

        string message = failures == 0
            ? $"Freed {ByteFormat.Format(freed)}."
            : $"Freed {ByteFormat.Format(freed)}. {failures:N0} items were in use or protected and were skipped.";
        return new CleanupOutcome(freed, removed, failures, message);
    }

    private static void DeleteChildren(DirectoryInfo dir, ref long freed, ref int removed, ref int failures, CancellationToken ct)
    {
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception) { failures++; return; }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry is DirectoryInfo sub)
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Remove the link itself, never what it points to.
                        sub.Delete(false);
                        removed++;
                        continue;
                    }
                    DeleteChildren(sub, ref freed, ref removed, ref failures, ct);
                    if (!sub.EnumerateFileSystemInfos().Any())
                    {
                        sub.Delete(false);
                        removed++;
                    }
                }
                else if (entry is FileInfo file)
                {
                    long length = file.Length;
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freed += length;
                    removed++;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failures++;
            }
        }
    }

    /// <summary>Moves files/folders to the Recycle Bin so the user can restore them.</summary>
    public static CleanupOutcome RecycleItems(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) return new CleanupOutcome(0, 0, 0, "Nothing to remove — the items no longer exist.");

        var sizes = existing.ToDictionary(p => p, p => File.Exists(p) ? new FileInfo(p).Length : DiskScanner.MeasureDirectory(p), StringComparer.OrdinalIgnoreCase);

        // SHFileOperation handles up to a few thousand paths per call comfortably; batch to be safe.
        foreach (var batch in existing.Chunk(200))
        {
            var op = new Win32.SHFILEOPSTRUCTW
            {
                wFunc = Win32.FO_DELETE,
                pFrom = string.Join('\0', batch) + "\0\0",
                fFlags = (ushort)(Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_NOERRORUI | Win32.FOF_SILENT),
            };
            Win32.SHFileOperationW(ref op);
        }

        long freed = 0;
        int removed = 0, failures = 0;
        foreach (var path in existing)
        {
            if (File.Exists(path) || Directory.Exists(path)) failures++;
            else { removed++; freed += sizes[path]; }
        }

        string message = $"Moved {removed:N0} item(s), {ByteFormat.Format(freed)}, to the Recycle Bin." +
                         (failures > 0 ? $" {failures:N0} could not be moved (in use or access denied)." : "") +
                         " Empty the Recycle Bin to actually free the space.";
        return new CleanupOutcome(freed, removed, failures, message);
    }

    public static CleanupOutcome EmptyRecycleBin()
    {
        var info = new Win32.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        Win32.SHQueryRecycleBinW(null, ref info);
        int hr = Win32.SHEmptyRecycleBinW(IntPtr.Zero, null, Win32.SHERB_NOCONFIRMATION | Win32.SHERB_NOPROGRESSUI | Win32.SHERB_NOSOUND);
        return hr == 0 || info.i64Size == 0
            ? new CleanupOutcome(info.i64Size, (int)info.i64NumItems, 0, $"Emptied the Recycle Bin and freed {ByteFormat.Format(info.i64Size)}.")
            : new CleanupOutcome(0, 0, 1, $"Could not empty the Recycle Bin (error 0x{hr:X8}).");
    }

    /// <summary>Runs a PowerShell command in a visible window so the user can see what happens.</summary>
    public static CleanupOutcome RunCommand(string command, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : "",
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"Write-Host 'Running: {command.Replace("'", "''")}' -ForegroundColor Cyan; {command}; Write-Host ''; Write-Host 'Done. You can close this window.' -ForegroundColor Green");
            Process.Start(psi);
            return new CleanupOutcome(0, 0, 0, "Started in a PowerShell window. Re-run the analysis afterwards to see the result.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CleanupOutcome(0, 0, 1, "Cancelled — administrator permission was not granted.");
        }
        catch (Exception ex)
        {
            return new CleanupOutcome(0, 0, 1, "Could not start the command: " + ex.Message);
        }
    }

    /// <summary>Opens a folder, file (selected in Explorer), program or settings URI.</summary>
    public static CleanupOutcome Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new CleanupOutcome(0, 0, 0, "");
        try
        {
            if (File.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return new CleanupOutcome(0, 0, 0, "");
        }
        catch (Exception ex)
        {
            return new CleanupOutcome(0, 0, 1, "Could not open: " + ex.Message);
        }
    }
}
