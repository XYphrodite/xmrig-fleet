namespace XmrigFleet.Contracts;

/// <summary>Contract version, sent by the agent so the console can warn about mismatches.</summary>
public static class ApiVersion
{
    public const string Current = "1";
}

public sealed record AgentInfoDto(
    string Hostname,
    string OperatingSystem,
    string AgentVersion,
    string ApiVersion,
    double AgentUptimeSeconds,
    bool IsElevated);

public sealed record MinerStatusDto
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public int? Pid { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public string? Algorithm { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public double UptimeSeconds { get; init; }
    /// <summary>Hashrate in H/s over the last 10s / 60s / 15m windows, as reported by the xmrig HTTP API.</summary>
    public double? Hashrate10s { get; init; }
    public double? Hashrate60s { get; init; }
    public double? Hashrate15m { get; init; }
    public double? HashrateHighest { get; init; }
    public long SharesGood { get; init; }
    public long SharesTotal { get; init; }
    public long PoolDifficulty { get; init; }
    public double? PingMs { get; init; }
    /// <summary>Set when the agent could not reach the xmrig HTTP API even though the process is alive.</summary>
    public string? ApiError { get; init; }

    // RandomX throughput is decided almost entirely by these, not by CPU model: a node that
    // fails to get its huge pages runs several times slower with no other symptom. Reporting
    // them turns "why is this node slow" from guesswork into a reading.

    /// <summary>Huge pages xmrig actually obtained for the RandomX dataset and caches.</summary>
    public int? HugePagesAllocated { get; init; }
    /// <summary>Huge pages xmrig asked for. Equal to <see cref="HugePagesAllocated"/> on a healthy node.</summary>
    public int? HugePagesTotal { get; init; }
    /// <summary>Threads the CPU backend is actually mining with (not the logical CPU count).</summary>
    public int? MiningThreads { get; init; }
    /// <summary>xmrig's MSR mod state, e.g. "intel", "ryzen_19h", or null when it could not be applied.</summary>
    public string? MsrMod { get; init; }
    /// <summary>Assembly optimisation xmrig selected, e.g. "intel", "ryzen".</summary>
    public string? Assembly { get; init; }

    /// <summary>Fraction of requested huge pages that were granted, or null when unknown.</summary>
    public double? HugePagesPercent =>
        HugePagesTotal is > 0 && HugePagesAllocated is { } got ? (double)got / HugePagesTotal.Value : null;
}

public sealed record SensorDto(string Component, string Name, string Kind, double Value, string Unit);

public sealed record GpuDto(string Name, double? TemperatureC, double? LoadPercent, double? PowerWatts, double? MemoryUsedMb, double? MemoryTotalMb);

public sealed record HardwareDto
{
    public string CpuName { get; init; } = "unknown";
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    public double? CpuTemperatureC { get; init; }
    public double? CpuLoadPercent { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double MemoryTotalGb { get; init; }
    public double MemoryUsedGb { get; init; }
    public string? MotherBoard { get; init; }
    public IReadOnlyList<GpuDto> Gpus { get; init; } = [];
    public IReadOnlyList<SensorDto> Sensors { get; init; } = [];
    /// <summary>Best-effort whole-machine draw. Falls back to the node's configured estimate when no sensor exists.</summary>
    public double? EstimatedPowerWatts { get; init; }
    public bool PowerIsMeasured { get; init; }

    /// <summary>Explains why sensors are missing when the agent can tell, e.g. a blocked ring0 driver.</summary>
    public string? SensorNotice { get; init; }
}

public sealed record NodeSnapshotDto(AgentInfoDto Agent, MinerStatusDto Miner, HardwareDto Hardware)
{
    /// <summary>Null on an agent too old to throttle, which is how the console tells them apart.</summary>
    public ThrottleStatusDto? Throttle { get; init; }

    /// <summary>
    /// What the session monitor last did, in the operator's terms — which window is open, or why
    /// none is. Present for the same reason as <see cref="HardwareDto.SensorNotice"/>: the console
    /// used to report "session monitor on" whatever had actually happened on the node, so a rig
    /// could sit at 60% of its hashrate with the setting reading on and nothing to say otherwise.
    /// </summary>
    public string? MonitorNotice { get; init; }
}

public sealed record CommandResultDto(bool Ok, string Message)
{
    public static CommandResultDto Success(string message) => new(true, message);
    public static CommandResultDto Failure(string message) => new(false, message);
}

public sealed record MinerConfigDto
{
    public string? ExecutablePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public string? Password { get; init; }
    public string[]? ExtraArgs { get; init; }
    /// <summary>Watts to assume for this machine when no power sensor is available.</summary>
    public double? PowerFallbackWatts { get; init; }

    /// <summary>
    /// Keep a monitor window - Resource Monitor by preference - running hidden in the node's
    /// interactive session.
    ///
    /// This is a remedy without a diagnosis, and it is worth saying so plainly: on an
    /// i7-12700KF the miner runs at 4,380 H/s with nothing watching and 7,092 H/s with Task
    /// Manager open - a 62% difference that survived eleven attempts to explain it away.
    /// Whatever Windows does differently, it needs a monitor window in a logged-on session;
    /// polling the same counters from the agent's own service achieved nothing.
    ///
    /// Null leaves the node's current setting alone, which is what a pool-settings push sends.
    /// </summary>
    public bool? KeepMonitorOpen { get; init; }

    /// <summary>
    /// Start mining as soon as the agent starts, so a node that came back on its own - after a
    /// mains failure, after a bugcheck, after a service restart - returns to work without
    /// anybody signing in to tell it to.
    ///
    /// Null leaves the node's current setting alone, which is what a pool-settings push sends.
    /// A node nobody has ever told falls back to <c>Agent:AutoStartMiner</c> in its own
    /// appsettings.json, so a fresh install still behaves the way it was installed.
    /// </summary>
    public bool? AutoStartMiner { get; init; }

    /// <summary>
    /// How hard the miner may run while the machine is in use. Null leaves the node alone.
    /// </summary>
    public ThrottleSettingsDto? Throttle { get; init; }

    /// <summary>
    /// True when the miner is stopped because the throttle took it to zero, rather than because
    /// an operator stopped it.
    ///
    /// Persisted because the agent restarts often - every self-update does - and the distinction
    /// cannot be recovered afterwards. Without it a node stopped by the throttle either stays
    /// down forever, or the agent starts mining that nobody asked for. Neither is acceptable.
    /// </summary>
    public bool? MinerStoppedByThrottle { get; init; }
}

/// <summary>
/// One rung of the throttle ladder: at or above <see cref="OtherCpuPercent"/> of CPU used by
/// everything except the miner, the miner is held to <see cref="Level"/> percent.
/// </summary>
public sealed record ThrottleStepDto(double OtherCpuPercent, int Level);

/// <summary>
/// How hard the miner may run while somebody is using the machine.
///
/// The ladder is deliberately coarse. A continuous curve tracks every twitch of background
/// activity and spends its life re-applying a limit nobody asked for; five rungs are enough to
/// keep a machine responsive and are legible in the console and in the decision log.
/// </summary>
public sealed record ThrottleSettingsDto
{
    /// <summary>Off by default: a node only throttles once an operator asks it to.</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// The ladder, lowest threshold first. Null keeps the node's current ladder; the agent
    /// falls back to <see cref="DefaultSteps"/> when it has never been given one.
    /// </summary>
    public IReadOnlyList<ThrottleStepDto>? Steps { get; init; }

    /// <summary>Never drop below this level, whatever the ladder says. 0 allows a full stop.</summary>
    public int? FloorLevel { get; init; }

    /// <summary>
    /// Seconds of quiet before the miner is allowed back up a rung. Coming down is immediate.
    ///
    /// The asymmetry is the whole point: interrupting somebody costs more than two minutes of
    /// hashing, and it also stops a single burst - opening a folder, a browser tab - from
    /// rocking the miner up and down.
    /// </summary>
    public int? RampUpSeconds { get; init; }

    /// <summary>
    /// A level the operator pinned by hand, which switches the automation off until cleared.
    /// Use <see cref="ClearManualLevel"/> to hand control back; null here means "leave as is".
    /// </summary>
    public int? ManualLevel { get; init; }

    /// <summary>Hands control back to the automation, clearing <see cref="ManualLevel"/>.</summary>
    public bool? ClearManualLevel { get; init; }

    /// <summary>
    /// Where the ladder starts before anybody tunes it. These thresholds are a guess and are
    /// meant to be corrected from the node's own decision log, not defended.
    /// </summary>
    public static IReadOnlyList<ThrottleStepDto> DefaultSteps =>
    [
        new(0, 100),
        new(10, 75),
        new(25, 50),
        new(45, 25),
        new(70, 0),
    ];
}

/// <summary>What the throttle is doing right now and why, so a slow node is never a mystery.</summary>
public sealed record ThrottleStatusDto
{
    public bool Enabled { get; init; }
    /// <summary>0-100. Below 100 the miner is capped; at 0 it is stopped and its memory released.</summary>
    public int Level { get; init; }
    /// <summary>Plain-language cause, e.g. "other processes at 52% CPU". Shown in the console.</summary>
    public string Reason { get; init; } = "";
    /// <summary>True when an operator pinned the level and the automation is standing down.</summary>
    public bool Manual { get; init; }
    /// <summary>CPU used by everything except the miner, which is what the ladder is read against.</summary>
    public double? OtherCpuPercent { get; init; }
    public double? MemoryUsedPercent { get; init; }
    /// <summary>Seconds the miner has been held at <see cref="Level"/>.</summary>
    public double SecondsAtLevel { get; init; }
}

/// <summary>Install or update xmrig. Either give an explicit <see cref="DownloadUrl"/> or a GitHub release tag.</summary>
public sealed record InstallRequestDto
{
    /// <summary>Directory xmrig should be installed into, e.g. C:\mining\xmrig.</summary>
    public string TargetPath { get; init; } = "";
    /// <summary>GitHub release tag such as "v6.22.2", or null/"latest" for the newest release.</summary>
    public string? Version { get; init; }
    /// <summary>Overrides <see cref="Version"/> when set: a direct .zip / .tar.gz URL.</summary>
    public string? DownloadUrl { get; init; }
    /// <summary>Stop a running miner, install, then start it again.</summary>
    public bool RestartAfterInstall { get; init; } = true;
}

public sealed record InstallResultDto(bool Ok, string Message, string? InstalledVersion, string? ExecutablePath);

/// <summary>Update the agent itself from a published xmrig-fleet release.</summary>
public sealed record AgentUpdateRequestDto
{
    /// <summary>Release tag such as "v1.4.0", or null/"latest" for the newest.</summary>
    public string? Version { get; init; }
    /// <summary>Overrides <see cref="Version"/> when set: a direct .zip URL.</summary>
    public string? DownloadUrl { get; init; }
    /// <summary>Reinstall even when the node already runs that version.</summary>
    public bool Force { get; init; }
}

public sealed record AgentUpdateResultDto(
    bool Ok,
    string Message,
    string? FromVersion,
    string? ToVersion,
    /// <summary>True when the agent swapped itself and is about to exit for the service manager to restart it.</summary>
    bool Restarting);

public sealed record LogTailDto(string Source, IReadOnlyList<string> Lines);
