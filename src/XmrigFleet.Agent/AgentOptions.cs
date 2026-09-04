using System.Text.Json;
using XmrigFleet.Contracts;

namespace XmrigFleet.Agent;

public sealed class AgentOptions
{
    /// <summary>Shared secret the console must send in the X-Fleet-Token header. Empty disables auth (not recommended).</summary>
    public string Token { get; set; } = "";

    /// <summary>Where Kestrel listens. Tailscale traffic arrives on the 100.x address, so bind all interfaces and rely on tailnet ACLs + the token.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:47800";

    /// <summary>Loopback port xmrig's own HTTP API is started on.</summary>
    public int XmrigApiPort { get; set; } = 47801;

    /// <summary>
    /// Start the miner as soon as the agent starts.
    ///
    /// This is only the installed default now. Once an operator has set autostart from the
    /// console the node's own miner.json holds the answer and this is not consulted - see
    /// <see cref="MinerConfigStore.ShouldAutoStart"/>.
    /// </summary>
    public bool AutoStartMiner { get; set; }

    /// <summary>
    /// Keep Windows' performance-counter subsystem polled.
    ///
    /// This buys nothing. It was tried as a way to reproduce the hashrate an open Task Manager
    /// gives - see <see cref="PerformanceCounterPump"/> - and measured no effect at all. The +62%
    /// belongs to the window, not to the polling, and the workaround that actually works is
    /// <see cref="SessionMonitorService"/>. Kept on so the negative result stays reproducible;
    /// turning it off is safe.
    /// </summary>
    public bool PollPerformanceCounters { get; set; } = true;

    /// <summary>How often the counter query is collected. One second matches what Task Manager does.</summary>
    public int PerformanceCounterIntervalMs { get; set; } = 1000;
}

/// <summary>
/// Persists the per-node miner settings the console pushes, so they survive an agent restart.
/// Stored next to the agent binary as miner.json.
/// </summary>
public sealed class MinerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _apiTokenPath;
    private readonly object _gate = new();
    private MinerConfigDto _current;

    public MinerConfigStore(string basePath)
    {
        _path = Path.Combine(basePath, "miner.json");
        _apiTokenPath = Path.Combine(basePath, "xmrig-api.token");
        _current = Load();
    }

    public MinerConfigDto Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// The bearer token the agent starts XMRig with, kept across agent restarts.
    ///
    /// It used to be generated per process, so restarting the agent — a service restart, an
    /// update, a crash — left it unable to read the miner it had started itself, and the node
    /// reported "mining (no api)" with no hashrate until someone restarted the miner. The
    /// token lives beside the binary, which only administrators can read, and grants nothing
    /// beyond the local miner API.
    /// </summary>
    public string GetOrCreateApiToken()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_apiTokenPath))
                {
                    var existing = File.ReadAllText(_apiTokenPath).Trim();
                    if (existing.Length >= 16) return existing;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable token file is not fatal: fall through and mint a new one.
            }

            var token = Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(_apiTokenPath, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without persistence the agent still works, it just goes blind after a restart.
            }
            return token;
        }
    }

    public MinerConfigDto Update(MinerConfigDto patch)
    {
        lock (_gate)
        {
            _current = new MinerConfigDto
            {
                ExecutablePath = patch.ExecutablePath ?? _current.ExecutablePath,
                ConfigPath = patch.ConfigPath ?? _current.ConfigPath,
                PoolUrl = patch.PoolUrl ?? _current.PoolUrl,
                Wallet = patch.Wallet ?? _current.Wallet,
                WorkerName = patch.WorkerName ?? _current.WorkerName,
                Password = patch.Password ?? _current.Password,
                ExtraArgs = patch.ExtraArgs ?? _current.ExtraArgs,
                PowerFallbackWatts = patch.PowerFallbackWatts ?? _current.PowerFallbackWatts,
                KeepMonitorOpen = patch.KeepMonitorOpen ?? _current.KeepMonitorOpen,
                AutoStartMiner = patch.AutoStartMiner ?? _current.AutoStartMiner,
                Throttle = MergeThrottle(_current.Throttle, patch.Throttle),
                MinerStoppedByThrottle = patch.MinerStoppedByThrottle ?? _current.MinerStoppedByThrottle,
            };
            Save(_current);
            return _current;
        }
    }

    /// <summary>
    /// Whether the agent should put the miner to work the moment it starts.
    ///
    /// The node's own answer wins, and <paramref name="installedDefault"/> - the flag
    /// <c>install-agent.ps1</c> wrote into appsettings.json - only applies until an operator
    /// has said otherwise from the console. A node the throttle stopped is left alone either
    /// way: autostart exists so a rig that rebooted returns to work, not so a machine somebody
    /// is using starts mining under them again.
    /// </summary>
    public bool ShouldAutoStart(bool installedDefault) => ShouldAutoStart(Current, installedDefault);

    /// <inheritdoc cref="ShouldAutoStart(bool)"/>
    public static bool ShouldAutoStart(MinerConfigDto config, bool installedDefault) =>
        (config.AutoStartMiner ?? installedDefault) && config.MinerStoppedByThrottle != true;

    /// <summary>
    /// Folds a throttle patch into what the node already has, field by field.
    ///
    /// Whole-object replacement would be wrong here: the console pushes <c>{ enabled: true }</c>
    /// when an operator flips the switch and would silently take the tuned ladder with it. The one
    /// field that clears rather than merges is the pinned level, and it needs its own flag to say
    /// so - null already means "leave alone" everywhere else in this contract.
    /// </summary>
    private static ThrottleSettingsDto? MergeThrottle(ThrottleSettingsDto? current, ThrottleSettingsDto? patch)
    {
        if (patch is null) return current;
        if (current is null) return patch with { ClearManualLevel = null };

        return new ThrottleSettingsDto
        {
            Enabled = patch.Enabled ?? current.Enabled,
            Steps = patch.Steps is { Count: > 0 } ? patch.Steps : current.Steps,
            FloorLevel = patch.FloorLevel ?? current.FloorLevel,
            RampUpSeconds = patch.RampUpSeconds ?? current.RampUpSeconds,
            ManualLevel = patch.ClearManualLevel == true ? null : patch.ManualLevel ?? current.ManualLevel,
        };
    }

    private MinerConfigDto Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<MinerConfigDto>(File.ReadAllText(_path)) ?? new MinerConfigDto();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file must not stop the agent from coming up.
        }
        return new MinerConfigDto();
    }

    private void Save(MinerConfigDto value)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(value, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
