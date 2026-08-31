using Spectre.Console;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console.Ui;

/// <summary>Start/stop/restart mining, install or update xmrig, read the miner log tail.</summary>
public sealed class MinerScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;

    public MinerScreen(FleetConfig config, FleetService fleet)
    {
        _config = config;
        _fleet = fleet;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UiHelpers.Header("Miner control");

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Action")
                .AddChoices(
                    "Start mining",
                    "Stop mining",
                    "Restart mining",
                    "Install / update xmrig",
                    "Push pool settings to nodes",
                    "Session monitor (hashrate workaround)",
                    "View miner log",
                    "< back"));

            switch (choice)
            {
                case "Start mining": await RunAsync("Starting", (c, t) => c.StartAsync(t), ct); break;
                case "Stop mining": await RunAsync("Stopping", (c, t) => c.StopAsync(t), ct); break;
                case "Restart mining": await RunAsync("Restarting", (c, t) => c.RestartAsync(t), ct); break;
                case "Install / update xmrig": await InstallAsync(ct); break;
                case "Push pool settings to nodes": await PushAsync(ct); break;
                case "Session monitor (hashrate workaround)": await SessionMonitorAsync(ct); break;
                case "View miner log": await LogsAsync(ct); break;
                default: return;
            }
        }
    }

    private async Task RunAsync(string verb, Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action, CancellationToken ct)
    {
        var nodes = UiHelpers.SelectNodes(_config, $"{verb} on which nodes?");
        if (nodes.Count == 0) return;

        IReadOnlyList<(NodeConfig Node, CommandResultDto Result)> results = [];
        await AnsiConsole.Status().StartAsync($"{verb} on {nodes.Count} node(s)...", async _ =>
        {
            results = await _fleet.ForEachAsync(nodes, action, ct);
        });

        AnsiConsole.WriteLine();
        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        UiHelpers.Pause();
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        UiHelpers.Header("Install / update xmrig");

        var nodes = UiHelpers.SelectNodes(_config, "Install on which nodes?");
        if (nodes.Count == 0) return;

        var defaultPath = nodes.Select(n => n.MinerPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                          ?? (OperatingSystem.IsWindows() ? @"C:\mining\xmrig" : "/opt/xmrig");

        var targetPath = AnsiConsole.Prompt(UiHelpers.Text("Install directory on the nodes:").DefaultValue(defaultPath));
        var version = AnsiConsole.Prompt(UiHelpers.Text("Release tag (or 'latest'):").DefaultValue("latest"));
        var restart = AnsiConsole.Confirm("Restart the miner afterwards if it was running?", defaultValue: true);

        AnsiConsole.MarkupLine($"[grey]Each node downloads xmrig itself from GitHub, so it needs outbound internet.[/]");
        if (!AnsiConsole.Confirm($"Install to [bold]{UiHelpers.Escape(targetPath)}[/] on {nodes.Count} node(s)?", defaultValue: true))
            return;

        var request = new InstallRequestDto
        {
            TargetPath = targetPath,
            Version = version,
            RestartAfterInstall = restart,
        };

        var results = new List<(string Node, InstallResultDto? Result, string? Error)>();
        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new SpinnerColumn(), new ElapsedTimeColumn())
            .StartAsync(async progress =>
            {
                var tasks = nodes.Select(async node =>
                {
                    var task = progress.AddTask(UiHelpers.Escape(node.Name), maxValue: 1);
                    using var client = _fleet.CreateClient(node, TimeSpan.FromMinutes(6));
                    try
                    {
                        var result = await client.InstallAsync(request, ct);
                        lock (results) results.Add((node.Name, result, null));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        lock (results) results.Add((node.Name, null, ex.Message));
                    }
                    finally
                    {
                        task.Increment(1);
                        task.StopTask();
                    }
                });
                await Task.WhenAll(tasks);
            });

        AnsiConsole.WriteLine();
        foreach (var (name, result, error) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
        {
            if (error is not null)
            {
                UiHelpers.Result(false, $"{name}: {error}");
                continue;
            }
            UiHelpers.Result(result?.Ok == true, $"{name}: {result?.Message ?? "no response"}");
        }

        // Remember the path so the next install and the config push prefill correctly.
        foreach (var node in nodes) node.MinerPath = targetPath;
        _config.Save();

        UiHelpers.Pause();
    }

    /// <summary>
    /// Turns the Task Manager workaround on or off per node. Presented with its own measurement
    /// and its own catch, because an operator switching this on deserves to know both that it is
    /// worth 62% and that nobody can explain why.
    /// </summary>
    private async Task SessionMonitorAsync(CancellationToken ct)
    {
        UiHelpers.Header("Session monitor");

        AnsiConsole.MarkupLine(
            "Keeps Task Manager open, minimised, in the node's own logged-on session.");
        AnsiConsole.MarkupLine(
            "[grey]Measured on an i7-12700KF: 4,380 H/s with nothing watching, 7,092 H/s with[/]");
        AnsiConsole.MarkupLine(
            "[grey]Task Manager open. Eleven explanations were tested and none held, so this is[/]");
        AnsiConsole.MarkupLine(
            "[grey]a remedy without a diagnosis - it may stop working after a Windows update.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[yellow]The task runs at logon[/], so a node that reboots without an automatic logon loses it.");
        AnsiConsole.WriteLine();

        var nodes = UiHelpers.SelectNodes(_config, "Change which nodes?");
        if (nodes.Count == 0) return;

        var enable = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title("Session monitor should be").AddChoices("on", "off"))
            == "on";

        var results = new List<(string Node, bool Ok, string Message)>();
        await AnsiConsole.Status().StartAsync(enable ? "Enabling..." : "Disabling...", async _ =>
        {
            var tasks = nodes.Select(async node =>
            {
                using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(60));
                try
                {
                    var pushed = await client.PutConfigAsync(new MinerConfigDto { KeepMonitorOpen = enable }, ct);
                    lock (results) results.Add((node.Name, pushed is not null, enable ? "session monitor on" : "session monitor off"));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    lock (results) results.Add((node.Name, false, ex.Message));
                }
            });
            await Task.WhenAll(tasks);
        });

        foreach (var (name, ok, message) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(ok, $"{name}: {message}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Give it a minute, then check the dashboard: the change is not instant.[/]");
        UiHelpers.Pause();
    }

    private async Task PushAsync(CancellationToken ct)
    {
        UiHelpers.Header("Push pool settings");

        if (string.IsNullOrWhiteSpace(_config.Pool.Wallet))
        {
            AnsiConsole.MarkupLine("[red]No wallet configured.[/] Set it under Settings first.");
            UiHelpers.Pause();
            return;
        }

        var nodes = UiHelpers.SelectNodes(_config, "Push to which nodes?");
        if (nodes.Count == 0) return;

        AnsiConsole.MarkupLine($"[grey]pool[/] {UiHelpers.Escape(_config.Pool.Url)}");
        AnsiConsole.MarkupLine($"[grey]wallet[/] {UiHelpers.Escape(_config.Pool.Wallet)}");
        AnsiConsole.MarkupLine(string.IsNullOrWhiteSpace(_config.Pool.Password)
            ? "[grey]worker name[/] = node name (the pool password field)"
            : $"[grey]worker name[/] {UiHelpers.Escape(_config.Pool.Password)} [grey]on every node[/]");
        AnsiConsole.WriteLine();

        var results = new List<(string Node, bool Ok, string Message)>();
        await AnsiConsole.Status().StartAsync("Pushing...", async _ =>
        {
            var tasks = nodes.Select(async node =>
            {
                using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(20));
                try
                {
                    var pushed = await client.PutConfigAsync(new MinerConfigDto
                    {
                        ExecutablePath = node.MinerPath,
                        PoolUrl = _config.Pool.Url,
                        Wallet = _config.Pool.Wallet,
                        // Hashvault reads the worker name from the password field, not from
                        // an address suffix. Leaving the fleet password blank therefore gives
                        // every rig its own name in the pool's worker list; setting one merges
                        // them under it.
                        WorkerName = null,
                        Password = string.IsNullOrWhiteSpace(_config.Pool.Password) ? node.Name : _config.Pool.Password,
                        PowerFallbackWatts = node.PowerFallbackWatts,
                    }, ct);
                    lock (results) results.Add((node.Name, pushed is not null, pushed?.ExecutablePath ?? "config saved"));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    lock (results) results.Add((node.Name, false, ex.Message));
                }
            });
            await Task.WhenAll(tasks);
        });

        foreach (var (name, ok, message) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(ok, $"{name}: {message}");

        AnsiConsole.MarkupLine("[grey]Restart the miner for new pool settings to take effect.[/]");
        UiHelpers.Pause();
    }

    private async Task LogsAsync(CancellationToken ct)
    {
        var node = UiHelpers.SelectNode(_config, "Log from which node?");
        if (node is null) return;

        UiHelpers.Header($"{node.Name} - xmrig output");

        using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(15));
        try
        {
            var logs = await client.GetLogsAsync(ct);
            if (logs is null || logs.Lines.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No output captured. The agent only records output of miners it started itself.[/]");
            }
            else
            {
                foreach (var line in logs.Lines.TakeLast(40))
                    AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(line)}[/]");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UiHelpers.Result(false, ex.Message);
        }

        UiHelpers.Pause();
    }
}
