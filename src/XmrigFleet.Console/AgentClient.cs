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
        _http = new HttpClient
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

    private async Task<CommandResultDto?> PostAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsync(path, content: null, ct);
        return await response.Content.ReadFromJsonAsync<CommandResultDto>(JsonOptions, ct);
    }

    public void Dispose() => _http.Dispose();
}
