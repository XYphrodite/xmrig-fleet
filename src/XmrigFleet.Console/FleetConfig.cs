using System.Text.Json;
using System.Text.Json.Serialization;

namespace XmrigFleet.Console;

/// <summary>
/// The whole fleet definition, persisted as fleet.json next to the console binary
/// (override with the XMRIG_FLEET_CONFIG environment variable).
/// </summary>
public sealed class FleetConfig
{
    /// <summary>Default X-Fleet-Token, used for every node that does not carry its own.</summary>
    public string Token { get; set; } = "";

    /// <summary>Port newly added nodes default to.</summary>
    public int AgentPort { get; set; } = 47800;

    public int PollIntervalSeconds { get; set; } = 5;

    public ElectricityConfig Electricity { get; set; } = new();
    public PoolConfig Pool { get; set; } = new();

    /// <summary>
    /// Where to read the XMR spot price when the pool does not publish the configured
    /// currency. Must return CoinGecko-shaped JSON. `{currency}` is replaced with the
    /// lower-cased currency code, so the feed is asked for the currency actually in use.
    /// </summary>
    public string PriceApiUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=monero&vs_currencies={currency}";

    public List<NodeConfig> Nodes { get; set; } = [];

    [JsonIgnore]
    public string Path { get; private set; } = "";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("XMRIG_FLEET_CONFIG")
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "fleet.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static FleetConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        FleetConfig config;
        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(path), JsonOptions) ?? new FleetConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
            }
        }
        else
        {
            config = new FleetConfig();
        }

        config.Path = path;
        return config;
    }

    public void Save()
    {
        var path = string.IsNullOrEmpty(Path) ? DefaultPath : Path;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        Path = path;
    }

    public NodeConfig? FindNode(string name) =>
        Nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string TokenFor(NodeConfig node) => string.IsNullOrWhiteSpace(node.Token) ? Token : node.Token;

    /// <summary>The tariff that actually applies to a node: its own, else the fleet default.</summary>
    public double PricePerKwhFor(NodeConfig node) => node.PricePerKwh ?? Electricity.PricePerKwh;
}

public sealed class NodeConfig
{
    public string Name { get; set; } = "";

    /// <summary>Tailscale IP or MagicDNS name.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 47800;

    /// <summary>Per-node override of the fleet token. Null falls back to <see cref="FleetConfig.Token"/>.</summary>
    public string? Token { get; set; }

    /// <summary>Excluded nodes are kept in the file but never polled or controlled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory xmrig lives in on that node, used to prefill the install prompt.</summary>
    public string? MinerPath { get; set; }

    /// <summary>Watts assumed when the node reports no power sensor.</summary>
    public double? PowerFallbackWatts { get; set; }

    /// <summary>
    /// Electricity tariff at this machine, when it differs from the fleet default — rigs
    /// often sit in different flats, regions or tariff bands. Null uses
    /// <see cref="ElectricityConfig.PricePerKwh"/>. The currency stays fleet-wide, because
    /// totals across nodes are only meaningful in one currency.
    /// </summary>
    public double? PricePerKwh { get; set; }

    public string Endpoint => $"http://{Host}:{Port}";

    public override string ToString() => $"{Name} ({Host}:{Port})";
}

public sealed class ElectricityConfig
{
    public double PricePerKwh { get; set; } = 5.0;
    public string Currency { get; set; } = "RUB";
}

public sealed class PoolConfig
{
    /// <summary>Hashvault REST base for the coin you mine.</summary>
    public string ApiBase { get; set; } = "https://api.hashvault.pro/v3/monero";

    /// <summary>Stratum URL the agents are pointed at.</summary>
    public string Url { get; set; } = "pool.hashvault.pro:443";

    /// <summary>Wallet address, used both for the miner user and for the pool/balance lookups.</summary>
    public string Wallet { get; set; } = "";

    public string? Password { get; set; }
}
