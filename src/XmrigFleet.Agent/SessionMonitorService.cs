using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XmrigFleet.Agent;

/// <summary>
/// Keeps Task Manager running, hidden, in the node's logged-on session.
///
/// A remedy without a diagnosis, and the code says so deliberately so nobody deletes it as
/// superstition. Measured on an i7-12700KF, the miner untouched between readings:
///
///     nothing watching            4 380 H/s      15.7 GB in use
///     Task Manager open           7 092 H/s       7.8 GB in use
///     Resource Monitor open       7 097 H/s       ~8 GB in use
///
/// Eleven explanations were tested and discarded, each by a controlled A/B on that node: huge
/// pages, free memory, CPU frequency, competing processes, xmrig's priority, the High
/// Performance power plan, a 1 ms timer resolution, opting out of EcoQoS, polling the same
/// counters from this service, Win32PrioritySeparation, and simply having a window open -
/// Notepad changes nothing.
///
/// Why CreateProcessAsUser rather than Process.Start: the agent is a service, so it lives in
/// session 0, which has no desktop. A process started the ordinary way lands there too, and the
/// effect does not happen - the counter pump proved that from the same session and achieved
/// nothing. Reaching the interactive session takes the user's own token, which a service running
/// as SYSTEM may borrow.
///
/// The one limit no implementation removes: somebody has to be logged on. With no session there
/// is no desktop to put a window on.
/// </summary>
public sealed class SessionMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly MinerConfigStore _config;
    private readonly ILogger<SessionMonitorService> _log;
    private readonly object _gate = new();

    private int _startedPid;

    public SessionMonitorService(MinerConfigStore config, ILogger<SessionMonitorService> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>Acts on a freshly pushed setting. Returns what happened, for the console to show.</summary>
    public string Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return "Session monitor is a Windows workaround; nothing to do on this platform.";

        if (!enabled)
        {
            StopMonitor();
            return "Session monitor off.";
        }

        return EnsureRunning(out var detail) ? $"Session monitor on: {detail}" : $"Session monitor could not start: {detail}";
    }

    /// <summary>
    /// Re-launches the window if it was closed, and stops it when the setting is turned off.
    /// Also covers the case the scheduled-task version could not: a node that boots and logs on
    /// long after the agent started still gets its window, without waiting for a logon event.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.Current.KeepMonitorOpen == true) EnsureRunning(out _);
                else StopMonitor();
            }
            catch (Exception ex)
            {
                // This service only makes mining faster. It must never be the reason a node
                // stops answering the console.
                _log.LogDebug(ex, "Session monitor check failed");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool EnsureRunning(out string detail)
    {
        lock (_gate)
        {
            if (IsOurProcessAlive())
            {
                detail = $"already running as pid {_startedPid}";
                return true;
            }

            if (FindLoggedOnSession() is not { } session)
            {
                detail = "nobody is logged on to this node yet; the window will appear as soon as somebody is";
                return false;
            }

            var started = Launch(session, out detail);
            if (!started) _log.LogWarning("Session monitor could not start: {Detail}", detail);
            return started;
        }
    }

    private void StopMonitor()
    {
        lock (_gate)
        {
            if (_startedPid == 0) return;

            try
            {
                // Only the window this service started: an operator who opened their own
                // Task Manager should not have it closed from under them.
                using var process = Process.GetProcessById(_startedPid);
                process.Kill();
                _log.LogInformation("Session monitor stopped (pid {Pid})", _startedPid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            _startedPid = 0;
        }
    }

    private bool IsOurProcessAlive()
    {
        if (_startedPid == 0) return false;

        try
        {
            using var process = Process.GetProcessById(_startedPid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _startedPid = 0;
            return false;
        }
    }

    /// <summary>
    /// The session a person is actually sitting in, or null when nobody is.
    ///
    /// Deliberately not WTSGetActiveConsoleSessionId: that returns the physical console, which
    /// on a machine administered over RDP is a connected-but-empty session while the operator
    /// is somewhere else entirely. These rigs are all reached by RDP, so aiming at the console
    /// put the window in a session with nobody in it and the workaround did nothing at all.
    /// </summary>
    private uint? FindLoggedOnSession()
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out buffer, out var count)) return null;

            var size = Marshal.SizeOf<WtsSessionInfo>();
            uint? fallback = null;

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);
                if (info.SessionId == 0) continue;   // services

                var user = QuerySession(info.SessionId, WtsUserName);
                if (string.IsNullOrWhiteSpace(user)) continue;

                // An active session is the one being looked at; a merely connected one still has
                // a desktop and is better than nothing if that is all this node has.
                if (info.State == WtsActive) return info.SessionId;
                fallback ??= info.SessionId;
            }

            return fallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogDebug(ex, "Could not enumerate sessions");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static string? QuerySession(uint session, int infoClass)
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out buffer, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private const int WtsUserName = 5;
    private const int WtsActive = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private bool Launch(uint session, out string detail)
    {
        var userToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(session, out userToken))
            {
                detail = $"could not borrow the session token (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                detail = $"could not duplicate the session token (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            CreateEnvironmentBlock(out environment, primaryToken, false);

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                // Without the desktop the process starts with nowhere to draw and the effect is lost.
                lpDesktop = @"winsta0\default",
                dwFlags = StartfUseShowWindow,
                // Hidden rather than merely minimised: the operator should not have a Task
                // Manager button on their taskbar for the lifetime of the machine. Whether the
                // window has to be *visible* for the effect is unknown - a visible Notepad does
                // nothing and an invisible counter poll does nothing either, so visibility was
                // never isolated. If the hashrate drops back with this, that is itself a finding.
                wShowWindow = SwHide,
            };

            var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskmgr.exe");

            if (!CreateProcessAsUser(primaryToken, command, null, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, environment, null, ref startup, out var info))
            {
                detail = $"could not start Task Manager in session {session} (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);

            _startedPid = info.dwProcessId;
            _log.LogInformation("Session monitor started in session {Session} as pid {Pid}", session, _startedPid);
            detail = $"started in session {session} as pid {_startedPid}";
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

