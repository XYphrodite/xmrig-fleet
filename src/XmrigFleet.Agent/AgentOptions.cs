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

    /// <summary>Start the miner as soon as the agent starts.</summary>
    public bool AutoStartMiner { get; set; }
}

/// <summary>
/// Persists the per-node miner settings the console pushes, so they survive an agent restart.
/// Stored next to the agent binary as miner.json.
/// </summary>
public sealed class MinerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private MinerConfigDto _current;

    public MinerConfigStore(string basePath)
    {
        _path = Path.Combine(basePath, "miner.json");
        _current = Load();
    }

    public MinerConfigDto Current
    {
        get { lock (_gate) return _current; }
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
            };
            Save(_current);
            return _current;
        }
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
