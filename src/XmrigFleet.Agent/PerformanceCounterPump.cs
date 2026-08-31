using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace XmrigFleet.Agent;

/// <summary>
/// Keeps Windows' performance-counter subsystem polled, the way Task Manager and Resource
/// Monitor do while their windows are open.
///
/// This looks like it does nothing with its results, and that is correct: the collection itself
/// is the point. Measured on an i7-12700KF mining RandomX, with the miner otherwise untouched:
///
///     nothing watching counters   4 380 H/s   CPU load 51%   "100% of max frequency" absent
///     Task Manager open           7 092 H/s   CPU load 60%
///     Resource Monitor open       7 097 H/s   CPU load 60%
///
/// The two monitors agree to within a tenth of a percent, so the effect belongs to the counter
/// subsystem rather than to either application. The proximate cause looks like processor
/// frequency management - Resource Monitor reports the package at 100% of maximum frequency
/// exactly while it is open - but that part is inference, not measurement, and the fix does not
/// depend on it being right.
///
/// Ruled out first, each by a controlled A/B on the same node: huge pages (constant at
/// 1180/1180 throughout), free memory (20 GB spare either way), competing processes (nothing
/// above 0.5%), xmrig's own priority (worth +26% on its own, not this), the High Performance
/// power plan (no effect), a 1 ms timer resolution (no effect), and opting the process out of
/// EcoQoS power throttling (no effect).
///
/// Counters are added by their English names through PdhAddEnglishCounter: the localised path a
/// tool like typeperf expects fails outright on a non-English Windows, which is how this was
/// first noticed.
/// </summary>
public sealed class PerformanceCounterPump : BackgroundService
{
    // What Task Manager and Resource Monitor read continuously. The frequency counter is the one
    // that matters if the mechanism really is P-state management; the load counter is cheap and
    // makes the query resemble a real monitor more closely.
    private static readonly string[] CounterPaths =
    [
        @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\% Processor Utility",
        @"\Processor(_Total)\% Processor Time",
    ];

    private const uint ErrorSuccess = 0;

    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<PerformanceCounterPump> _log;

    public PerformanceCounterPump(IOptions<AgentOptions> options, ILogger<PerformanceCounterPump> log)
    {
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.PollPerformanceCounters) return;
        if (!OperatingSystem.IsWindows())
        {
            _log.LogDebug("Performance counter polling is a Windows behaviour; skipping.");
            return;
        }

        var query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != ErrorSuccess)
            {
                _log.LogWarning("Could not open a PDH query; hashrate on hybrid CPUs may sit below what this node can do.");
                return;
            }

            var added = 0;
            foreach (var path in CounterPaths)
            {
                // A counter missing on this SKU must not take the others down with it.
                if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out _) == ErrorSuccess) added++;
                else _log.LogDebug("Performance counter not available on this machine: {Counter}", path);
            }

            if (added == 0)
            {
                _log.LogWarning("No performance counters could be added; not polling.");
                return;
            }

            // PDH needs two collections before rate counters mean anything. Nothing reads the
            // values here, but a monitor would, so behave like one.
            PdhCollectQueryData(query);

            var interval = TimeSpan.FromMilliseconds(Math.Clamp(options.PerformanceCounterIntervalMs, 200, 10_000));
            _log.LogInformation("Polling {Count} performance counters every {Interval} to keep the CPU out of its background performance state.", added, interval);

            while (!stoppingToken.IsCancellationRequested)
            {
                PdhCollectQueryData(query);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (DllNotFoundException ex)
        {
            _log.LogWarning(ex, "pdh.dll is unavailable; performance counter polling is off.");
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>English counter names, so a localised Windows resolves them the same way.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
