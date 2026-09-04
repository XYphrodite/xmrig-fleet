namespace XmrigFleet.Agent;

/// <summary>
/// Decides whether the GPU miner should be standing down, and when it may come back.
///
/// Kept free of sockets, processes and timers for the same reason <see cref="ThrottleLadder"/> is:
/// everything it needs arrives as arguments, including the clock, so the rule can be tested
/// without a machine to run it on. The parts that read the TCP table and kill the miner live in
/// <see cref="GpuPauseService"/>.
///
/// Standing down is immediate and coming back waits, which is the same asymmetry the CPU throttle
/// uses and it is here for a sharper reason: the card is being taken because a person is waiting
/// on it. A local model answered at 19 tok/s while the card mined and 53 tok/s once it stopped, so
/// a second of hesitation is a second of somebody watching a cursor blink.
/// </summary>
public sealed class GpuPauseRule
{
    private DateTimeOffset? _quietSince;

    /// <summary>True while the miner should be stopped. Starts false: mining is the default state.</summary>
    public bool Paused { get; private set; }

    /// <summary>Why <see cref="Paused"/> is what it is, in the words the console shows.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>When the answer last changed, for "paused for 40 seconds".</summary>
    public DateTimeOffset ChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Feeds one observation in. <paramref name="busy"/> is whatever the rule watches — an open
    /// connection, a running process — already reduced to a yes or no by the caller.
    ///
    /// Returns true when the answer changed and the caller must act on it.
    /// </summary>
    public bool Update(bool busy, string busyDescription, int quietSeconds, DateTimeOffset now)
    {
        if (busy)
        {
            _quietSince = null;
            if (Paused)
            {
                Reason = busyDescription;
                return false;
            }

            Set(true, busyDescription, now);
            return true;
        }

        if (!Paused)
        {
            Reason = "mining";
            return false;
        }

        // Measured from the first quiet observation rather than reset on each one, so a burst of
        // requests with gaps between them does not restart the miner in every gap.
        _quietSince ??= now;

        var waited = (now - _quietSince.Value).TotalSeconds;
        if (waited < quietSeconds)
        {
            Reason = $"paused, {quietSeconds - waited:0}s of quiet still needed";
            return false;
        }

        _quietSince = null;
        Set(false, $"resumed after {quietSeconds}s of quiet", now);
        return true;
    }

    /// <summary>
    /// Stands the rule down entirely, for a node whose pause rule was switched off or removed. The
    /// miner is left running, because a rule that no longer exists must not keep holding the card.
    /// </summary>
    public void Clear(DateTimeOffset now)
    {
        _quietSince = null;
        if (Paused) Set(false, "no pause rule", now);
        else Reason = "no pause rule";
    }

    private void Set(bool paused, string reason, DateTimeOffset now)
    {
        Paused = paused;
        Reason = reason;
        ChangedAt = now;
    }
}
