using System.Diagnostics;
using System.Runtime.InteropServices;
using SpaceLens.Core.Native;

namespace SpaceLens.Core.Monitoring;

/// <summary>Raw per-process counters from one NtQuerySystemInformation call.</summary>
internal readonly record struct RawProcess(
    ProcessKey Key,
    int ParentPid,
    string Name,
    uint SessionId,
    long CpuTime100ns,
    long PrivateBytes,
    long WorkingSet,
    long ReadBytes,
    long WriteBytes,
    long OtherBytes,
    int Threads,
    int Handles,
    uint HardFaults);

/// <summary>
/// Enumerates every process with a single system call. Unlike System.Diagnostics.Process this
/// needs no per-process handle, so it also sees protected/system processes without admin rights.
/// </summary>
internal sealed unsafe class ProcessEnumerator
{
    private IntPtr _buffer;
    private int _bufferSize = 1024 * 1024;

    public List<RawProcess> Read()
    {
        if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_bufferSize);

        while (true)
        {
            uint status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, _buffer, _bufferSize, out int needed);
            if (status == Win32.STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buffer);
                _bufferSize = Math.Max(_bufferSize * 2, needed + 64 * 1024);
                _buffer = Marshal.AllocHGlobal(_bufferSize);
                continue;
            }
            if (status != 0) return [];
            break;
        }

        var list = new List<RawProcess>(400);
        byte* ptr = (byte*)_buffer;
        while (true)
        {
            var info = (Win32.SYSTEM_PROCESS_INFORMATION*)ptr;
            int pid = (int)info->UniqueProcessId;

            string name = pid switch
            {
                0 => "System Idle Process",
                _ when info->ImageName.Buffer == IntPtr.Zero => pid == 4 ? "System" : "pid " + pid,
                _ => new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2),
            };

            list.Add(new RawProcess(
                new ProcessKey(pid, info->CreateTime),
                (int)info->InheritedFromUniqueProcessId,
                name,
                info->SessionId,
                info->KernelTime + info->UserTime,
                (long)(ulong)info->PrivatePageCount,
                (long)(ulong)info->WorkingSetSize,
                info->ReadTransferCount,
                info->WriteTransferCount,
                info->OtherTransferCount,
                (int)info->NumberOfThreads,
                (int)info->HandleCount,
                info->HardFaultCount));

            if (info->NextEntryOffset == 0) break;
            ptr += info->NextEntryOffset;
        }
        return list;
    }

    ~ProcessEnumerator()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
    }
}

/// <summary>Looks up (and caches) executable paths and file descriptions.</summary>
internal sealed class ProcessDetailsCache
{
    private readonly Dictionary<ProcessKey, (string? Path, string? Description)> _byKey = new();
    private readonly Dictionary<string, string?> _descriptionByPath = new(StringComparer.OrdinalIgnoreCase);

    public (string? Path, string? Description) Get(ProcessKey key, string name)
    {
        if (_byKey.TryGetValue(key, out var cached)) return cached;

        string? path = key.Pid is 0 or 4 ? null : QueryPath(key.Pid);
        string? description = name switch
        {
            "System Idle Process" => "Idle time (not a real process)",
            "System" => "Windows kernel & drivers",
            "Registry" => "Windows registry",
            "Memory Compression" => "Windows memory compression",
            "Secure System" => "Virtualization-based security",
            _ => null,
        };

        if (description is null && path is not null)
        {
            if (!_descriptionByPath.TryGetValue(path, out description))
            {
                try { description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
                catch (Exception) { description = null; }
                _descriptionByPath[path] = description;
            }
        }

        var result = (path, description);
        _byKey[key] = result;
        return result;
    }

    public void Prune(HashSet<ProcessKey> alive)
    {
        foreach (var key in _byKey.Keys.Where(k => !alive.Contains(k)).ToList()) _byKey.Remove(key);
    }

    private static string? QueryPath(int pid)
    {
        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return Win32.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }
}

internal static class TcpConnections
{
    /// <summary>Established TCP connections per PID (IPv4 + IPv6).</summary>
    public static Dictionary<int, int> CountByProcess()
    {
        var counts = new Dictionary<int, int>();
        Count(Win32.AF_INET, rowSize: 24, pidOffset: 20, counts);
        Count(Win32.AF_INET6, rowSize: 56, pidOffset: 52, counts);
        return counts;
    }

    private static unsafe void Count(int family, int rowSize, int pidOffset, Dictionary<int, int> counts)
    {
        int size = 0;
        Win32.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = Win32.GetExtendedTcpTable(buffer, ref size, false, family, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: table grew, retry
                if (result != 0) return;

                byte* p = (byte*)buffer;
                uint rows = *(uint*)p;
                byte* row = p + 4;
                for (uint i = 0; i < rows; i++, row += rowSize)
                {
                    // MIB_TCPROW_OWNER_PID: dwState is the first field.
                    // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid.
                    uint state = family == Win32.AF_INET ? *(uint*)row : *(uint*)(row + 48);
                    if (state != Win32.MIB_TCP_STATE_ESTAB) continue;
                    int pid = *(int*)(row + pidOffset);
                    counts[pid] = counts.TryGetValue(pid, out var c) ? c + 1 : 1;
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
