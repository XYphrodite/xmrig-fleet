using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XmrigFleet.Agent;

/// <summary>
/// Creates or removes a scheduled task that keeps Task Manager running, minimised, in the
/// node's interactive session.
///
/// This is a remedy, not a diagnosis, and the code says so on purpose so nobody deletes it as
/// superstition. Measured on an i7-12700KF, miner untouched between readings:
///
///     nothing watching            4 380 H/s      15.7 GB in use
///     Task Manager open           7 092 H/s       7.8 GB in use
///     Resource Monitor open       7 097 H/s       ~8 GB in use
///
/// Eleven explanations were tested and discarded on that node, each by a controlled A/B: huge
/// pages, free memory, CPU frequency, competing processes, xmrig's priority, the High
/// Performance power plan, a 1 ms timer resolution, opting out of EcoQoS power throttling,
/// polling the same counters from this service, the scheduler's quantum policy
/// (Win32PrioritySeparation), and simply having a window open (Notepad changes nothing). What
/// survives is that a monitor window in a logged-on session is worth 62%, and that the effect
/// tracks a ~7 GB swing in memory in use.
///
/// The cost of the workaround is real and belongs in the operator's decision: the task fires on
/// logon, so a node that reboots without an automatic logon quietly loses the benefit. That is
/// why this is opt-in per node rather than on by default.
/// </summary>
public sealed class SessionMonitorService
{
    private const string TaskName = "xmrig-fleet-keep-fast";

    // Task Manager honours "hide when minimised" and drops to the notification area, so this
    // starts it out of the way rather than in the operator's face.
    private const string Command = "powershell -WindowStyle Hidden -Command \"Start-Process taskmgr -WindowStyle Minimized\"";

    private readonly ILogger<SessionMonitorService> _log;

    public SessionMonitorService(ILogger<SessionMonitorService> log) => _log = log;

    /// <summary>Applies the node's setting. Returns what happened, for the console to show.</summary>
    public string Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return "Session monitor is a Windows workaround; nothing to do on this platform.";

        return enabled ? Create() : Remove();
    }

    public bool IsInstalled() =>
        OperatingSystem.IsWindows() && Run("schtasks", $"/query /tn \"{TaskName}\"").ExitCode == 0;

    private string Create()
    {
        // The task has to run as the person who is logged on: a task owned by SYSTEM would start
        // Task Manager in session 0, where no window exists and the effect does not happen.
        var user = InteractiveUser();
        if (user is null)
            return "Nobody is logged on to this node, so the task has no session to run in. Log on once, or enable automatic logon, then push the setting again.";

        var result = Run("schtasks",
            $"/create /tn \"{TaskName}\" /tr \"{Command}\" /sc onlogon /ru \"{user}\" /it /rl highest /f");

        if (result.ExitCode != 0)
            return $"Could not create the task: {Describe(result)}";

        // /sc onlogon only fires at the next logon; start it now so the node benefits immediately.
        var started = Run("schtasks", $"/run /tn \"{TaskName}\"");
        var note = started.ExitCode == 0 ? " and started now" : " (starts at the next logon)";

        _log.LogInformation("Session monitor task created for {User}{Note}", user, note);
        return $"Task Manager will be kept open in {user}'s session{note}.";
    }

    private string Remove()
    {
        if (!IsInstalled()) return "No session monitor task was installed.";

        var result = Run("schtasks", $"/delete /tn \"{TaskName}\" /f");
        if (result.ExitCode != 0)
            return $"Could not remove the task: {Describe(result)}";

        // Leaving the window behind would make "off" look like it did nothing.
        foreach (var process in Process.GetProcessesByName("Taskmgr"))
        {
            try { process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }

        _log.LogInformation("Session monitor task removed");
        return "Session monitor task removed and Task Manager closed.";
    }

    /// <summary>The console-session user, e.g. MKS68I7RTX\local, or null when nobody is logged on.</summary>
    private string? InteractiveUser()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return null;   // no console session attached

            var user = QuerySession(session, WtsUserName);
            if (string.IsNullOrWhiteSpace(user)) return null;

            var domain = QuerySession(session, WtsDomainName);
            return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogDebug(ex, "Could not read the interactive user");
            return null;
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

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

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static (int ExitCode, string Output) Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return (-1, "could not start " + file);

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    private static string Describe((int ExitCode, string Output) result) =>
        string.IsNullOrWhiteSpace(result.Output) ? $"schtasks exited {result.ExitCode}" : result.Output;
}
