using Spectre.Console;
using XmrigFleet.Console.Ui;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console;

/// <summary>
/// One-shot commands for scripts and scheduled tasks. Everything here renders once and
/// exits, so it works fine with redirected output where the interactive menu cannot run.
/// </summary>
public static class Cli
{
    public const string Usage = """
        xmrig-fleet [command] [node ...]

          (no command)   interactive console
          status         print the fleet table once
          start          start mining
          stop           stop mining
          restart        restart mining
          economics      cost, income and profit summary
          pool           pool and wallet balance
          update         download and install a newer xmrig-fleet
          upgrade-agents update the agent on the nodes themselves
          version        print the running version
          help           this text

        Commands that take nodes act on every enabled node when none are named.
        """;

    public static async Task<int> RunAsync(string[] args, FleetConfig config, FleetService fleet, MarketService market, CancellationToken ct)
    {
        var command = args[0].ToLowerInvariant().TrimStart('-');
        var names = args.Skip(1).ToArray();

        switch (command)
        {
            case "status":
                return await StatusAsync(config, fleet, ct);

            case "start":
                return await ControlAsync(config, fleet, names, (c, t) => c.StartAsync(t), ct);

            case "stop":
                return await ControlAsync(config, fleet, names, (c, t) => c.StopAsync(t), ct);

            case "restart":
                return await ControlAsync(config, fleet, names, (c, t) => c.RestartAsync(t), ct);

            case "economics":
                await new EconomicsScreenReport(config, fleet, market).WriteAsync(ct);
                return 0;

            case "pool":
                await new PoolReport(config, market).WriteAsync(ct);
                return 0;

            case "update":
                return await Updater.RunAsync(config, names.Contains("--check") || names.Contains("check"), ct);

            case "upgrade-agents" or "upgrade-agent":
                return await UpgradeAgentsAsync(config, fleet, names, ct);

            case "version" or "--version" or "v":
                AnsiConsole.WriteLine($"xmrig-fleet {UpdateService.CurrentVersion}");
                return 0;

            case "help" or "h" or "?":
                AnsiConsole.WriteLine(Usage);
                return 0;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
                AnsiConsole.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> StatusAsync(FleetConfig config, FleetService fleet, CancellationToken ct)
    {
        var states = await fleet.PollAsync(ct);
        if (states.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No enabled nodes configured.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Node").AddColumn("State")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Pages").RightAligned())
            .AddColumn(new TableColumn("Temp").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(state.Hashrate > 0 ? Economics.FormatHashrate(state.Hashrate) : "[grey]-[/]"),
                new Markup(UiHelpers.HugePages(state.Snapshot?.Miner)),
                new Markup(UiHelpers.Temperature(state.Snapshot?.Hardware.CpuTemperatureC ?? state.Snapshot?.Hardware.Gpus.FirstOrDefault()?.TemperatureC)),
                new Markup(state.PowerWatts > 0 ? $"{state.PowerWatts:0}" : "[grey]-[/]"),
                new Markup(state.Snapshot?.Miner is { Running: true } m ? Economics.FormatDuration(m.UptimeSeconds) : "[grey]-[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]{states.Count(s => s.Mining)} mining, {states.Count(s => s.Online)} online, {states.Count} total, " +
            $"{Economics.FormatHashrate(states.Where(s => s.Mining).Sum(s => s.Hashrate))} total[/]");

        // Non-zero exit when something is wrong, so a scheduled task can alert on it.
        return states.Any(s => !s.Online) ? 1 : 0;
    }

    /// <summary>
    /// Rolls the agent binary out to the nodes. Deliberately sequential and deliberately
    /// verify-first: a node that is already unreachable must be reported, not written to, and
    /// taking the fleet's agents down in one shot would leave nothing to diagnose from if the
    /// release turned out to be broken. Miners keep hashing throughout - the agent restart does
    /// not touch them.
    /// </summary>
    private static async Task<int> UpgradeAgentsAsync(FleetConfig config, FleetService fleet, string[] names, CancellationToken ct)
    {
        var version = names.FirstOrDefault(n => n.StartsWith("--version=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        var force = names.Contains("--force");
        var wanted = names.Where(n => !n.StartsWith("--", StringComparison.Ordinal)).ToArray();

        var targets = wanted.Length == 0
            ? fleet.EnabledNodes
            : wanted.Select(config.FindNode).OfType<NodeConfig>().ToList();

        foreach (var name in wanted.Where(n => config.FindNode(n) is null))
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        var failures = 0;

        foreach (var node in targets.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var client = fleet.CreateClient(node);

            AgentInfoDto? before;
            try
            {
                before = await client.GetInfoAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Never write to a node that is not answering: if the token is wrong or the
                // service is down, an update cannot fix it and may make the state harder to read.
                UiHelpers.Result(false, $"{node.Name}: unreachable, skipped ({ex.Message})");
                failures++;
                continue;
            }

            AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(node.Name)}[/] agent {UiHelpers.Escape(before?.AgentVersion ?? "?")} -> updating...");

            try
            {
                var result = await client.UpdateAgentAsync(new AgentUpdateRequestDto { Version = version, Force = force }, ct);
                if (result is null)
                {
                    UiHelpers.Result(false, $"{node.Name}: the agent returned no result");
                    failures++;
                    continue;
                }

                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");
                if (!result.Ok) { failures++; continue; }
                if (!result.Restarting) continue;

                if (await WaitForAgentAsync(fleet, node, before, ct) is { } after)
                    AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else
                {
                    AnsiConsole.MarkupLine("  [yellow]did not come back within 90s - check the service on the node[/]");
                    failures++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The agent can drop the connection while swapping itself; that is not a failure
                // on its own, so fall through to the same wait-and-verify path.
                AnsiConsole.MarkupLine($"  [grey]connection closed during the swap, waiting for the restart...[/]");
                if (await WaitForAgentAsync(fleet, node, before, ct) is { } after)
                    AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else
                {
                    UiHelpers.Result(false, $"{node.Name}: did not come back ({ex.Message})");
                    failures++;
                }
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Waits for the node to come back on the <em>new</em> build. An answer alone proves nothing:
    /// the outgoing process keeps serving for a couple of seconds after it has written the new
    /// files, so a naive wait reports the old version as "back up" and a successful roll-out looks
    /// like a failed one. A restarted agent gives itself away by its uptime resetting.
    /// </summary>
    private static async Task<AgentInfoDto?> WaitForAgentAsync(FleetService fleet, NodeConfig node, AgentInfoDto? before, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            try
            {
                using var client = fleet.CreateClient(node, TimeSpan.FromSeconds(5));
                if (await client.GetInfoAsync(ct) is not { } info) continue;

                var youngerThanThisWait = info.AgentUptimeSeconds < (DateTime.UtcNow - started).TotalSeconds + 5;
                var versionMoved = before is not null && info.AgentVersion != before.AgentVersion;
                if (youngerThanThisWait || versionMoved) return info;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Still restarting.
            }
        }
        return null;
    }

    private static async Task<int> ControlAsync(
        FleetConfig config,
        FleetService fleet,
        string[] names,
        Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action,
        CancellationToken ct)
    {
        var targets = names.Length == 0
            ? fleet.EnabledNodes
            : names.Select(config.FindNode).OfType<NodeConfig>().ToList();

        var missing = names.Where(n => config.FindNode(n) is null).ToList();
        foreach (var name in missing)
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        var results = await fleet.ForEachAsync(targets, action, ct);
        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        return results.All(r => r.Result.Ok) && missing.Count == 0 ? 0 : 1;
    }
}

/// <summary>Plain-output variant of the economics screen, without prompts.</summary>
internal sealed class EconomicsScreenReport(FleetConfig config, FleetService fleet, MarketService market)
{
    public async Task WriteAsync(CancellationToken ct)
    {
        var pollTask = fleet.PollAsync(ct);
        var networkTask = market.GetNetworkStatsAsync(ct);
        var walletTask = market.GetWalletStatsAsync(ct);
        var priceTask = market.GetPriceAsync(ct);
        await Task.WhenAll(pollTask, networkTask, walletTask, priceTask);

        var economics = Economics.Calculate(pollTask.Result, config, networkTask.Result, priceTask.Result);
        var currency = economics.Currency;
        var credited = walletTask.Result?.CreditedTodayXmr;
        var money = await market.GetMoneyFormatAsync(ct);

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Hashrate", Economics.FormatHashrate(economics.TotalHashrate));
        table.AddRow("Power draw", $"{economics.TotalWatts:0} W");
        table.AddRow("Electricity per day", money.Markup(economics.CostPerDay));
        table.AddRow("Income per day (estimate)", economics.XmrPerDay is { } x ? $"{x:0.000000} XMR" : "-");
        table.AddRow($"Income per day, {currency}", money.Markup(economics.RevenuePerDay));
        table.AddRow("Credited by the pool, 24h", credited is { } c ? $"{c:0.000000} XMR" : "-");
        table.AddRow("Actual vs estimate", economics.XmrPerDay is > 0 && credited is { } c2
            ? $"{c2 / economics.XmrPerDay.Value:P0}"
            : "-");
        table.AddRow("Profit per day", money.Signed(economics.ProfitPerDay));
        table.AddRow("Cost per XMR", money.Markup(economics.CostPerXmr));
        AnsiConsole.Write(table);
    }
}

/// <summary>Plain-output variant of the pool screen.</summary>
internal sealed class PoolReport(FleetConfig config, MarketService market)
{
    public async Task WriteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Pool.Wallet))
        {
            AnsiConsole.MarkupLine("[yellow]No wallet configured.[/]");
            return;
        }

        var walletTask = market.GetWalletStatsAsync(ct);
        var networkTask = market.GetNetworkStatsAsync(ct);
        var priceTask = market.GetPriceAsync(ct);
        await Task.WhenAll(walletTask, networkTask, priceTask);

        var wallet = walletTask.Result;
        var network = networkTask.Result;
        var price = priceTask.Result;
        var money = await market.GetMoneyFormatAsync(ct);

        if (wallet is null)
        {
            AnsiConsole.MarkupLine("[red]The pool API did not answer.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Pool hashrate", wallet.HashrateNow is { } h ? Economics.FormatHashrate(h) : "-");
        table.AddRow("Valid shares", wallet.ValidShares?.ToString("N0") ?? "-");
        table.AddRow("Confirmed balance", wallet.ConfirmedBalanceXmr is { } d ? $"{d:0.000000} XMR" : "-");
        table.AddRow($"Confirmed balance, {config.Electricity.Currency}", wallet.ConfirmedBalanceXmr is { } d2 && price is { } p
            ? money.Markup(d2 * p) : "-");
        table.AddRow("Unconfirmed", wallet.UnconfirmedBalanceXmr is { } u ? $"{u:0.000000} XMR" : "-");
        table.AddRow("Payout threshold", wallet.PayoutThresholdXmr is { } t ? $"{t:0.000000} XMR" : "-");
        table.AddRow("Paid out", wallet.TotalPaidXmr is { } paid ? $"{paid:0.000000} XMR" : "-");
        table.AddRow("Network hashrate", network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-");
        table.AddRow("XMR price", money.Format(price));
        AnsiConsole.Write(table);
    }
}
