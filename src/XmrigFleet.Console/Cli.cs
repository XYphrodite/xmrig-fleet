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
            .AddColumn(new TableColumn("Temp").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(state.Hashrate > 0 ? Economics.FormatHashrate(state.Hashrate) : "[grey]-[/]"),
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
        var priceTask = market.GetPriceAsync(ct);
        await Task.WhenAll(pollTask, networkTask, priceTask);

        var economics = Economics.Calculate(pollTask.Result, config, networkTask.Result, priceTask.Result);
        var currency = economics.Currency;

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Hashrate", Economics.FormatHashrate(economics.TotalHashrate));
        table.AddRow("Power draw", $"{economics.TotalWatts:0} W");
        table.AddRow("Electricity per day", UiHelpers.Money(economics.CostPerDay, currency));
        table.AddRow("Income per day", economics.XmrPerDay is { } x ? $"{x:0.00000} XMR" : "-");
        table.AddRow($"Income per day, {currency}", UiHelpers.Money(economics.RevenuePerDay, currency));
        table.AddRow("Profit per day", UiHelpers.Signed(economics.ProfitPerDay, currency));
        table.AddRow("Cost per XMR", UiHelpers.Money(economics.CostPerXmr, currency));
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
            ? UiHelpers.Money(d2 * p, config.Electricity.Currency) : "-");
        table.AddRow("Unconfirmed", wallet.UnconfirmedBalanceXmr is { } u ? $"{u:0.000000} XMR" : "-");
        table.AddRow("Payout threshold", wallet.PayoutThresholdXmr is { } t ? $"{t:0.000000} XMR" : "-");
        table.AddRow("Paid out", wallet.TotalPaidXmr is { } paid ? $"{paid:0.000000} XMR" : "-");
        table.AddRow("Network hashrate", network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-");
        table.AddRow("XMR price", price is null ? "-" : $"{price:N2} {config.Electricity.Currency}");
        AnsiConsole.Write(table);
    }
}
