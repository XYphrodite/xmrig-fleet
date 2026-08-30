using System.Net.Http.Json;
using System.Text.Json;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console;

/// <summary>Talks to one node agent over the tailnet.</summary>
public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AgentClient(NodeConfig node, string token, TimeSpan? timeout = null)
    {
        Node = node;
        // Agents live on private tailnet addresses, so a system proxy has no business in the
        // middle. Without this, a machine running a local VPN/proxy client sends fleet traffic
        // into it and every node comes back as an HTTP error that looks like the agent's fault.
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{node.Endpoint}/api/v1/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(8),
        };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Add("X-Fleet-Token", token);
    }

    public NodeConfig Node { get; }

    public Task<NodeSnapshotDto?> GetStatusAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<NodeSnapshotDto>("status", JsonOptions, ct);

    public Task<AgentInfoDto?> GetInfoAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<AgentInfoDto>("info", JsonOptions, ct);

    public Task<MinerConfigDto?> GetConfigAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<MinerConfigDto>("config", JsonOptions, ct);

    public Task<LogTailDto?> GetLogsAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<LogTailDto>("logs", JsonOptions, ct);

    public Task<CommandResultDto?> StartAsync(CancellationToken ct) => PostAsync("miner/start", ct);
    public Task<CommandResultDto?> StopAsync(CancellationToken ct) => PostAsync("miner/stop", ct);
    public Task<CommandResultDto?> RestartAsync(CancellationToken ct) => PostAsync("miner/restart", ct);

    public async Task<MinerConfigDto?> PutConfigAsync(MinerConfigDto patch, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("config", patch, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MinerConfigDto>(JsonOptions, ct);
    }

    /// <summary>Downloads and unpacks xmrig on the node. Slow, so it gets its own long timeout.</summary>
    public async Task<InstallResultDto?> InstallAsync(InstallRequestDto request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "install")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(6));
        using var response = await _http.SendAsync(message, cts.Token);
        return await response.Content.ReadFromJsonAsync<InstallResultDto>(JsonOptions, cts.Token);
    }

    /// <summary>
    /// Updates the agent itself. The node answers before it restarts, so a normal timeout is
    /// enough; the connection may still drop mid-reply if the swap is fast, which the caller
    /// treats as success-pending rather than failure.
    /// </summary>
    public async Task<AgentUpdateResultDto?> UpdateAgentAsync(AgentUpdateRequestDto request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "agent/update")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(6));
        using var response = await _http.SendAsync(message, cts.Token);

        // An agent from before this feature has no such route and answers 404 with an empty
        // body. During a roll-out that is the normal case, not an error worth a stack trace,
        // so it has to read as a plain instruction rather than crash the run.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new AgentUpdateResultDto(
                false,
                "this agent is too old to update itself - install it once by hand, then the console can do the rest",
                null, null, false);

        if (!response.IsSuccessStatusCode)
            return new AgentUpdateResultDto(false, $"the agent returned HTTP {(int)response.StatusCode}", null, null, false);

        return await response.Content.ReadFromJsonAsync<AgentUpdateResultDto>(JsonOptions, cts.Token);
    }

    private async Task<CommandResultDto?> PostAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsync(path, content: null, ct);
        return await response.Content.ReadFromJsonAsync<CommandResultDto>(JsonOptions, ct);
    }

    public void Dispose() => _http.Dispose();
}
