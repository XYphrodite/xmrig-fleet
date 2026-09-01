using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XmrigFleet.Agent;

/// <summary>
/// Holds the miner to a share of the machine's CPU, using a Windows job object.
///
/// Why this and not fewer mining threads: changing the thread count means rewriting xmrig's
/// config and restarting its backend, which re-allocates the RandomX dataset. On a node whose
/// memory is already tight that hand-back can fail, and a miner that loses its huge pages runs
/// several times slower with nothing to show for it - measured at 4.5x on the Xeon. A job object
/// caps the same process instantly, leaves the dataset and the huge pages exactly where they are,
/// and the scale it takes is already 0-100.
///
/// The cap is a share of the whole machine, not of one core, which is what makes it mean the same
/// thing on a 6-thread node and a 28-thread one.
///
/// Surviving an agent restart takes some care. A process cannot be removed from a job object, and
/// nested jobs combine by taking the *most* restrictive limit - so an agent that forgot its job
/// and created a second one could lower the cap but never raise it, and a node left at 25% could
/// not be brought back to full speed without killing the miner. The job is therefore named, and a
/// restarted agent re-opens the one it made last time.
/// </summary>
public sealed class MinerCpuLimit : IDisposable
{
    /// <summary>Session-wide so it survives the agent, but private to this machine.</summary>
    private const string JobName = @"Local\xmrig-fleet-miner-cpu";

    private readonly ILogger<MinerCpuLimit> _log;
    private readonly object _gate = new();

    /// <summary>Nothing has been applied by *this* instance yet; the job may still carry a limit.</summary>
    private const int Unknown = -1;

    private IntPtr _job = IntPtr.Zero;
    private int _assignedPid;
    private int _appliedLevel = Unknown;

    public MinerCpuLimit(ILogger<MinerCpuLimit> log) => _log = log;

    /// <summary>
    /// The level this instance last applied, or <see cref="Unknown"/> before it has applied any.
    ///
    /// Deliberately not "100 until told otherwise". The job object outlives the agent - that is
    /// the whole point of naming it - so a restarted agent inherits whatever limit the previous
    /// one left behind. Starting the count at 100 would let a caller skip the call that lifts a
    /// 25% cap it does not know about, and the node would mine at a quarter speed with nothing
    /// anywhere saying why.
    /// </summary>
    public int AppliedLevel { get { lock (_gate) return _appliedLevel; } }

    /// <summary>
    /// Holds <paramref name="pid"/> to <paramref name="level"/> percent of the machine's CPU.
    /// A level of 100 lifts the cap. Returns false with a reason when the limit could not be set.
    /// </summary>
    public bool Apply(int pid, int level, out string detail)
    {
        level = Math.Clamp(level, 1, 100);

        if (!OperatingSystem.IsWindows())
        {
            detail = "CPU limiting is implemented for Windows only";
            return false;
        }

        lock (_gate)
        {
            try
            {
                if (!EnsureJob(out detail)) return false;
                if (!EnsureAssigned(pid, out detail)) return false;

                var info = level >= 100
                    // Everything zero disables rate control. A rate left in the struct alongside
                    // cleared flags is asking the kernel to validate a field it is being told to
                    // ignore, which is not a bet worth taking on the call that lifts a limit.
                    ? new JobCpuRateControlInformation()
                    : new JobCpuRateControlInformation
                    {
                        ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
                        // Expressed in hundredths of a percent, so 25% is 2500.
                        CpuRate = (uint)(level * 100),
                    };

                if (!SetInformationJobObject(_job, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<JobCpuRateControlInformation>()))
                {
                    detail = $"could not set the CPU limit (error {Marshal.GetLastWin32Error()})";
                    return false;
                }

                _appliedLevel = level;
                detail = level >= 100 ? "limit lifted" : $"capped at {level}%";
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                detail = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Called when the miner stops, so the next start is assigned and capped afresh.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _assignedPid = 0;
            _appliedLevel = Unknown;
        }
    }

    private bool EnsureJob(out string detail)
    {
        detail = "";
        if (_job != IntPtr.Zero) return true;

        // Re-open before creating: after a restart the job this agent made earlier is still
        // holding the miner, and it is the only handle that can lift its own cap.
        _job = OpenJobObject(JobObjectAllAccess, false, JobName);
        if (_job != IntPtr.Zero) return true;

        // No limit flags are ever set on this job beyond the CPU rate. In particular
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is left off, which is the default: closing the last
        // handle must not take the miner with it, and this agent closes its handle on every
        // self-update. Mining outliving the agent is a rule the fleet already has.
        _job = CreateJobObjectW(IntPtr.Zero, JobName);
        if (_job == IntPtr.Zero)
        {
            detail = $"could not create the job object (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        return true;
    }

    private bool EnsureAssigned(int pid, out string detail)
    {
        detail = "";
        if (_assignedPid == pid) return true;

        var handle = OpenProcess(ProcessSetQuota | ProcessTerminate | ProcessQueryInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            detail = $"could not open the miner process (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            if (!AssignProcessToJobObject(_job, handle))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorAccessDenied)
                {
                    detail = $"could not put the miner in the job object (error {error})";
                    return false;
                }

                // Access denied here has two quite different meanings: the process is already in
                // this job - the ordinary case after an agent restart - or it is in somebody
                // else's job that will not accept nesting. Guessing the first would leave the
                // service setting limits on a job the miner is not in, reporting a cap that does
                // nothing. Ask instead.
                if (!IsProcessInJob(handle, _job, out var belongs) || !belongs)
                {
                    detail = "the miner belongs to another job object and cannot be limited";
                    return false;
                }

                _log.LogDebug("Miner pid {Pid} already belonged to the throttle job", pid);
            }

            _assignedPid = pid;
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // The job outlives this handle because the miner is still in it, which is exactly
            // what lets the next agent re-open it by name.
            if (_job != IntPtr.Zero) CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private const uint JobObjectAllAccess = 0x1F001F;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryInformation = 0x0400;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenJobObject(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobCpuRateControlInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
