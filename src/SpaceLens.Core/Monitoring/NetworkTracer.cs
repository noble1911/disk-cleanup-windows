using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace SpaceLens.Core.Monitoring;

/// <summary>
/// Per-process network byte counts from the kernel TCP/IP ETW provider. Windows exposes no other
/// API for this, and starting a kernel trace session requires administrator rights.
/// </summary>
internal sealed class NetworkTracer : IDisposable
{
    private const string SessionName = "SpaceLens-Network";

    private readonly object _lock = new();
    private Dictionary<int, long> _bytes = new();
    private TraceEventSession? _session;
    private Thread? _thread;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public bool TryStart()
    {
        if (IsRunning) return true;
        if (TraceEventSession.IsElevated() != true)
        {
            Error = "Run as administrator to see per-process network usage.";
            return false;
        }

        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = _session.Source.Kernel;
            kernel.TcpIpRecv += d => Add(d.ProcessID, d.size);
            kernel.TcpIpSend += d => Add(d.ProcessID, d.size);
            kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size);
            kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size);
            kernel.UdpIpRecv += d => Add(d.ProcessID, d.size);
            kernel.UdpIpSend += d => Add(d.ProcessID, d.size);
            kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size);
            kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size);

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; IsRunning = false; }
            })
            { IsBackground = true, Name = "SpaceLens ETW network" };
            _thread.Start();
            IsRunning = true;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = "Per-process network tracing unavailable: " + ex.Message;
            _session?.Dispose();
            _session = null;
            return false;
        }
    }

    private void Add(int pid, int size)
    {
        if (pid < 0 || size <= 0) return;
        lock (_lock)
        {
            _bytes[pid] = _bytes.TryGetValue(pid, out var b) ? b + size : size;
        }
    }

    /// <summary>Returns bytes per PID since the previous call and resets the counters.</summary>
    public Dictionary<int, long> Drain()
    {
        lock (_lock)
        {
            var result = _bytes;
            _bytes = new Dictionary<int, long>(result.Count);
            return result;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _session?.Dispose(); } catch (Exception) { }
        _session = null;
    }
}
