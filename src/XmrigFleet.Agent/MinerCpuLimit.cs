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

    private IntPtr _job = IntPtr.Zero;
    private int _assignedPid;
    private int _appliedLevel = 100;

    public MinerCpuLimit(ILogger<MinerCpuLimit> log) => _log = log;

    /// <summary>The level last applied successfully, or 100 when the miner runs uncapped.</summary>
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

                var info = new JobCpuRateControlInformation
                {
                    ControlFlags = level >= 100
                        ? 0                                  // enable bit clear: no cap at all
                        : CpuRateControlEnable | CpuRateControlHardCap,
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

    /// <summary>Called when the miner stops, so the next start is assigned afresh.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _assignedPid = 0;
            _appliedLevel = 100;
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

        _job = CreateJobObjectW(IntPtr.Zero, JobName);
        if (_job == IntPtr.Zero)
        {
            detail = $"could not create the job object (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        // Without this the miner would be killed the moment the agent exits - which happens on
        // every self-update. Mining must outlive the agent; that is a rule the fleet already has.
        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0;
        SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>());

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
                // 5 is access denied, which on this call almost always means the process already
                // belongs to this job from before the agent restarted. That is the good case.
                if (error != ErrorAccessDenied)
                {
                    detail = $"could not put the miner in the job object (error {error})";
                    return false;
                }
                _log.LogDebug("Miner pid {Pid} appears to already belong to the throttle job", pid);
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

    private const int JobObjectExtendedLimitInformation = 9;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
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
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
