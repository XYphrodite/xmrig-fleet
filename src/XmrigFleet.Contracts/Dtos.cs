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

public sealed record NodeSnapshotDto(AgentInfoDto Agent, MinerStatusDto Miner, HardwareDto Hardware);

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

public sealed record LogTailDto(string Source, IReadOnlyList<string> Lines);
