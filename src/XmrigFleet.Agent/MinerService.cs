using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XmrigFleet.Contracts;

namespace XmrigFleet.Agent;

/// <summary>
/// Owns the xmrig process on this node: start, stop, and reading live stats off the
/// loopback HTTP API. The agent always launches xmrig with that API enabled so hashrate
/// and share counters are available without parsing stdout.
/// </summary>
public sealed class MinerService : IDisposable
{
    private const string ProcessName = "xmrig";

    private readonly MinerConfigStore _config;
    private readonly AgentOptions _options;
    private readonly ILogger<MinerService> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _apiToken = Guid.NewGuid().ToString("N");
    private readonly LinkedList<string> _recentOutput = new();

    private Process? _process;

    public MinerService(MinerConfigStore config, IOptions<AgentOptions> options, ILogger<MinerService> log)
    {
        _config = config;
        _options = options.Value;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public IReadOnlyList<string> RecentOutput
    {
        get { lock (_recentOutput) return _recentOutput.ToList(); }
    }

    /// <summary>Accepts either the exe path itself or the directory it was installed into.</summary>
    public string? ResolveExecutable()
    {
        var configured = _config.Current.ExecutablePath;
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (File.Exists(configured)) return configured;
        if (Directory.Exists(configured))
        {
            var name = OperatingSystem.IsWindows() ? "xmrig.exe" : "xmrig";
            var candidate = Path.Combine(configured, name);
            if (File.Exists(candidate)) return candidate;
            return Directory.EnumerateFiles(configured, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        return null;
    }

    public async Task<CommandResultDto> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (FindRunning() is not null)
                return CommandResultDto.Failure("xmrig is already running.");

            var exe = ResolveExecutable();
            if (exe is null)
                return CommandResultDto.Failure("xmrig executable not found. Set the miner path or run an install first.");

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in BuildArguments(_config.Current)) psi.ArgumentList.Add(a);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);

            if (!process.Start())
                return CommandResultDto.Failure("Failed to start the xmrig process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _log.LogInformation("Started xmrig (pid {Pid}) from {Exe}", process.Id, exe);

            // Give xmrig a moment so an immediate crash (bad pool, bad wallet) is reported here
            // rather than silently showing up as "not running" on the next poll.
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
            if (process.HasExited)
            {
                var tail = string.Join(" | ", RecentOutput.TakeLast(3));
                return CommandResultDto.Failure($"xmrig exited immediately (code {process.ExitCode}). {tail}");
            }

            return CommandResultDto.Success($"xmrig started, pid {process.Id}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start failed");
            return CommandResultDto.Failure($"Start failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResultDto> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var processes = AllMinerProcesses();
            if (processes.Count == 0)
            {
                _process = null;
                return CommandResultDto.Success("xmrig was not running.");
            }

            var stopped = 0;
            foreach (var p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    _log.LogWarning(ex, "Could not stop pid {Pid}", p.Id);
                }
                finally
                {
                    p.Dispose();
                }
            }

            _process = null;
            return stopped > 0
                ? CommandResultDto.Success($"Stopped {stopped} xmrig process(es).")
                : CommandResultDto.Failure("Found xmrig but could not stop it. Try running the agent elevated.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResultDto> RestartAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        return await StartAsync(ct);
    }

    public async Task<MinerStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var running = FindRunning();

        // A miner started before this agent (or by hand) still counts as installed: take the
        // path off the live process so the console shows where it actually runs from.
        var exe = ResolveExecutable() ?? (running is null ? null : TryGetImagePath(running));

        var status = new MinerStatusDto
        {
            Installed = exe is not null,
            Running = running is not null,
            Pid = running?.Id,
            ExecutablePath = exe,
            PoolUrl = cfg.PoolUrl,
            Wallet = cfg.Wallet,
            WorkerName = cfg.WorkerName,
        };

        if (running is null) return status;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_options.XmrigApiPort}/2/summary");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return status with { ApiError = $"xmrig API returned {(int)response.StatusCode}." };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Merge(status, doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Process is up but its API is not answering yet, or it was started outside this agent.
            return status with { ApiError = ex.Message };
        }
    }

    private static MinerStatusDto Merge(MinerStatusDto status, JsonElement root)
    {
        static double? Hash(JsonElement array, int index) =>
            array.ValueKind == JsonValueKind.Array
            && array.GetArrayLength() > index
            && array[index].ValueKind == JsonValueKind.Number
                ? array[index].GetDouble()
                : null;

        var hashrateTotal = default(JsonElement);
        var highest = default(JsonElement);
        if (root.TryGetProperty("hashrate", out var hr))
        {
            hr.TryGetProperty("total", out hashrateTotal);
            hr.TryGetProperty("highest", out highest);
        }
        root.TryGetProperty("connection", out var conn);

        return status with
        {
            Version = root.TryGetProperty("version", out var v) ? v.GetString() : null,
            Algorithm = ReadString(conn, "algo"),
            UptimeSeconds = root.TryGetProperty("uptime", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetDouble() : 0,
            Hashrate10s = Hash(hashrateTotal, 0),
            Hashrate60s = Hash(hashrateTotal, 1),
            Hashrate15m = Hash(hashrateTotal, 2),
            HashrateHighest = highest.ValueKind == JsonValueKind.Number ? highest.GetDouble() : null,
            SharesGood = ReadLong(conn, "accepted"),
            SharesTotal = ReadLong(conn, "accepted") + ReadLong(conn, "rejected"),
            PoolDifficulty = ReadLong(conn, "diff"),
            PingMs = conn.ValueKind == JsonValueKind.Object && conn.TryGetProperty("ping", out var ping) && ping.ValueKind == JsonValueKind.Number
                ? ping.GetDouble()
                : null,
            PoolUrl = ReadString(conn, "pool") ?? status.PoolUrl,
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private List<string> BuildArguments(MinerConfigDto cfg)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(cfg.ConfigPath))
        {
            args.Add("--config");
            args.Add(cfg.ConfigPath);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(cfg.PoolUrl))
            {
                args.Add("--url");
                args.Add(cfg.PoolUrl);
            }
            if (!string.IsNullOrWhiteSpace(cfg.Wallet))
            {
                args.Add("--user");
                args.Add(string.IsNullOrWhiteSpace(cfg.WorkerName) ? cfg.Wallet : $"{cfg.Wallet}.{cfg.WorkerName}");
            }
            args.Add("--pass");
            args.Add(string.IsNullOrWhiteSpace(cfg.Password) ? Environment.MachineName : cfg.Password);
        }

        // The loopback API is how this agent reads hashrate. The random token keeps other
        // local processes from driving the miner.
        args.Add("--http-enabled");
        args.Add("--http-host");
        args.Add("127.0.0.1");
        args.Add("--http-port");
        args.Add(_options.XmrigApiPort.ToString());
        args.Add("--http-access-token");
        args.Add(_apiToken);
        args.Add("--http-no-restricted");

        if (cfg.ExtraArgs is { Length: > 0 }) args.AddRange(cfg.ExtraArgs);
        return args;
    }

    private static string? TryGetImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Reading another session's module list needs privileges the agent may not have.
            return null;
        }
    }

    private Process? FindRunning()
    {
        if (_process is { HasExited: false }) return _process;
        _process = null;
        return AllMinerProcesses().FirstOrDefault();
    }

    private static List<Process> AllMinerProcesses()
    {
        try
        {
            return Process.GetProcessesByName(ProcessName).Where(p => !p.HasExited).ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_recentOutput)
        {
            _recentOutput.AddLast(line);
            while (_recentOutput.Count > 200) _recentOutput.RemoveFirst();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        _process?.Dispose();
    }
}
