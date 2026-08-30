using XmrigFleet.Contracts;

namespace XmrigFleet.Console;

/// <summary>What the console knows about one node right now.</summary>
public sealed record NodeState(NodeConfig Node, NodeSnapshotDto? Snapshot, string? Error, DateTimeOffset PolledAt)
{
    public bool Online => Snapshot is not null;
    public bool Mining => Snapshot?.Miner.Running == true;

    /// <summary>Preferred hashrate window: 60s is steady enough to read but still current.</summary>
    public double Hashrate => Snapshot?.Miner.Hashrate60s ?? Snapshot?.Miner.Hashrate10s ?? 0;

    /// <summary>
    /// A real sensor reading wins. Otherwise the operator-configured figure beats the
    /// agent's guess, because it usually comes from an actual wall meter.
    /// </summary>
    public double PowerWatts =>
        Snapshot?.Hardware is { PowerIsMeasured: true, EstimatedPowerWatts: { } measured }
            ? measured
            : Node.PowerFallbackWatts ?? Snapshot?.Hardware.EstimatedPowerWatts ?? 0;

    public bool PowerIsMeasured => Snapshot?.Hardware.PowerIsMeasured == true;
}

/// <summary>Fans requests out to every enabled node and keeps the latest answer for each.</summary>
public sealed class FleetService
{
    private readonly FleetConfig _config;

    public FleetService(FleetConfig config) => _config = config;

    public IReadOnlyList<NodeConfig> EnabledNodes => _config.Nodes.Where(n => n.Enabled).ToList();

    public AgentClient CreateClient(NodeConfig node, TimeSpan? timeout = null) =>
        new(node, _config.TokenFor(node), timeout);

    public async Task<IReadOnlyList<NodeState>> PollAsync(CancellationToken ct)
    {
        var nodes = EnabledNodes;
        if (nodes.Count == 0) return [];

        var tasks = nodes.Select(node => PollOneAsync(node, ct));
        return await Task.WhenAll(tasks);
    }

    private async Task<NodeState> PollOneAsync(NodeConfig node, CancellationToken ct)
    {
        using var client = CreateClient(node);
        try
        {
            var snapshot = await client.GetStatusAsync(ct);
            return new NodeState(node, snapshot, snapshot is null ? "empty response" : null, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new NodeState(node, null, Describe(ex), DateTimeOffset.Now);
        }
    }

    /// <summary>Runs one action against every enabled node in parallel and reports each result.</summary>
    public async Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> ForEachAsync(
        IEnumerable<NodeConfig> nodes,
        Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action,
        CancellationToken ct)
    {
        var tasks = nodes.Select(async node =>
        {
            using var client = CreateClient(node, TimeSpan.FromSeconds(30));
            try
            {
                var result = await action(client, ct) ?? CommandResultDto.Failure("empty response");
                return (node, result);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return (node, CommandResultDto.Failure(Describe(ex)));
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "unauthorized (check the token)",
        HttpRequestException http => http.StatusCode is { } code ? $"HTTP {(int)code}" : "unreachable",
        _ => ex.Message,
    };
}
