using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XmrigFleet.Agent;

/// <summary>A reading of how busy the machine is, with the miner's own share separated out.</summary>
/// <param name="TotalCpuPercent">Everything, miner included.</param>
/// <param name="MinerCpuPercent">The miner alone.</param>
/// <param name="OtherCpuPercent">What is left: the load the throttle is meant to get out of the way of.</param>
/// <param name="MemoryUsedPercent">Physical memory in use.</param>
public readonly record struct SystemLoad(
    double TotalCpuPercent,
    double MinerCpuPercent,
    double OtherCpuPercent,
    double MemoryUsedPercent);

/// <summary>
/// Cheap, once-a-second load sampling for the throttle.
///
/// Deliberately not <see cref="HardwareService"/>: that walks every LibreHardwareMonitor sensor
/// on the machine and takes seconds, which is fine for a screen refresh and useless for a control
/// loop. Two kernel calls and one process handle are enough for what the ladder is read against.
///
/// The miner's own CPU time is subtracted for a reason that is easy to miss: if the throttle read
/// total load, capping the miner would lower that load, which would let the miner back up, which
/// would raise it again. The machine would spend its life oscillating and never settle. What the
/// ladder must react to is the load the miner does not cause.
/// </summary>
public sealed class SystemLoadReader
{
    private const string MinerProcessName = "xmrig";

    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastMinerTicks;
    private bool _primed;

    /// <summary>
    /// Load since the previous call. The first call primes the counters and reports zero: CPU
    /// percentages are differences between two samples, and there is nothing to subtract yet.
    /// </summary>
    public SystemLoad Read()
    {
        var memory = ReadMemoryPercent();

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return new SystemLoad(0, 0, 0, memory);

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);
        var minerTicks = ReadMinerTicks();

        if (!_primed)
        {
            (_lastIdle, _lastKernel, _lastUser, _lastMinerTicks, _primed) =
                (idleTicks, kernelTicks, userTicks, minerTicks, true);
            return new SystemLoad(0, 0, 0, memory);
        }

        // GetSystemTimes counts kernel time with idle time inside it, and every figure is summed
        // across all cores - the same units Process.TotalProcessorTime uses, which is what makes
        // the subtraction below valid.
        var totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
        var idleDelta = idleTicks - _lastIdle;
        var minerDelta = minerTicks - _lastMinerTicks;

        (_lastIdle, _lastKernel, _lastUser, _lastMinerTicks) = (idleTicks, kernelTicks, userTicks, minerTicks);

        if (totalDelta <= 0) return new SystemLoad(0, 0, 0, memory);

        var total = 100.0 * (totalDelta - idleDelta) / totalDelta;
        var miner = 100.0 * minerDelta / totalDelta;

        // A miner that restarted between samples reports negative time; clamping keeps one odd
        // sample from reading as an idle machine and letting everything back up to full speed.
        total = Math.Clamp(total, 0, 100);
        miner = Math.Clamp(miner, 0, 100);

        return new SystemLoad(
            Math.Round(total, 1),
            Math.Round(miner, 1),
            Math.Round(Math.Max(0, total - miner), 1),
            memory);
    }

    /// <summary>Forgets the previous sample, so the next reading starts a fresh difference.</summary>
    public void Reset() => _primed = false;

    private static long ReadMinerTicks()
    {
        long ticks = 0;
        Process[] found;

        try { found = Process.GetProcessesByName(MinerProcessName); }
        catch (InvalidOperationException) { return 0; }

        foreach (var process in found)
        {
            // Several miner processes are unusual but possible - one adopted, one started - and
            // the throttle cares about their combined share, not about which is which.
            try { ticks += process.TotalProcessorTime.Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Exited between the listing and the read, or refused a handle. Skip it.
            }
            finally { process.Dispose(); }
        }

        return ticks;
    }

    private static double ReadMemoryPercent()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return status.dwMemoryLoad;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows, or a stripped API set. Memory simply goes unreported.
        }
        return 0;
    }

    private static long ToTicks(FileTime time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
