using System.Globalization;
using System.Text;

namespace XmrigFleet.Agent;

/// <summary>
/// Records every change of rung with the readings that caused it, to throttle.log beside the agent.
///
/// The thresholds shipped with this feature are a guess. Tuning them from how the machine felt is
/// how we spent a day chasing a hashrate effect that eleven guesses could not explain, and the
/// only thing that moved it was a log of numbers. So the log carries the inputs, not just the
/// outcome: a week of it should say plainly which rung fired for nothing.
///
/// Bounded the same way <see cref="FileLogger"/> is - a node runs unattended for months, and no
/// diagnostic is worth filling its disk.
/// </summary>
public sealed class ThrottleLog
{
    private const long MaxBytes = 1 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public ThrottleLog(string basePath) => _path = Path.Combine(basePath, "throttle.log");

    public void Record(int from, int to, string reason, SystemLoad load)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}  {1,3} -> {2,-3}  cpu={3:0.#}% other={4:0.#}% miner={5:0.#}% mem={6:0}%  {7}",
            DateTime.Now, from, to, load.TotalCpuPercent, load.OtherCpuPercent, load.MinerCpuPercent, load.MemoryUsedPercent, reason);

        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A node that cannot write its diagnostic must still mine.
            }
        }
    }

    /// <summary>The tail of the log, newest last, for the console to show without an RDP session.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];
                return File.ReadLines(_path).TakeLast(Math.Max(1, lines)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private void Rotate()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes) return;

        var previous = _path + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(_path, previous);
    }
}
