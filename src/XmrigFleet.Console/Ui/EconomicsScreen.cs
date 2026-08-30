using Spectre.Console;

namespace XmrigFleet.Console.Ui;

/// <summary>Electricity cost, projected income and the per-node breakdown.</summary>
public sealed class EconomicsScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;

    public EconomicsScreen(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Economics");

        IReadOnlyList<NodeState> states = [];
        PoolNetworkStats? network = null;
        double? price = null;

        await AnsiConsole.Status().StartAsync("Collecting fleet and market data...", async _ =>
        {
            var pollTask = _fleet.PollAsync(ct);
            var networkTask = _market.GetNetworkStatsAsync(ct);
            var priceTask = _market.GetPriceAsync(ct);
            await Task.WhenAll(pollTask, networkTask, priceTask);
            states = pollTask.Result;
            network = networkTask.Result;
            price = priceTask.Result;
        });

        var currency = _config.Electricity.Currency;
        var economics = Economics.Calculate(states, _config, network, price);

        var summary = new Grid().AddColumn().AddColumn();
        summary.AddRow("[grey]Mining nodes[/]", $"{states.Count(s => s.Mining)} of {states.Count}");
        summary.AddRow("[grey]Fleet hashrate[/]", $"[aqua]{Economics.FormatHashrate(economics.TotalHashrate)}[/]");
        summary.AddRow("[grey]Power draw[/]", $"{economics.TotalWatts:0} W");
        summary.AddRow("[grey]Electricity[/]", $"{_config.Electricity.PricePerKwh:N2} {UiHelpers.Escape(currency)} / kWh");
        summary.AddRow("[grey]XMR price[/]", price is null ? "[yellow]price feed unavailable[/]" : $"{price:N2} {UiHelpers.Escape(currency)}");
        summary.AddRow("[grey]Network hashrate[/]", network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "[yellow]pool API unavailable[/]");
        AnsiConsole.Write(new Panel(summary).Header("[bold]Inputs[/]").Border(BoxBorder.Rounded).Expand());

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per period[/]")
            .AddColumn("Period")
            .AddColumn(new TableColumn("Income XMR").RightAligned())
            .AddColumn(new TableColumn("Income").RightAligned())
            .AddColumn(new TableColumn("Electricity").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var (label, factor) in new[] { ("Hour", 1 / 24.0), ("Day", 1.0), ("Week", 7.0), ("Month (30d)", 30.0) })
        {
            table.AddRow(
                label,
                economics.XmrPerDay is { } xmr ? $"{xmr * factor:0.00000}" : "[grey]-[/]",
                UiHelpers.Money(economics.RevenuePerDay * factor, currency),
                UiHelpers.Money(economics.CostPerDay * factor, currency),
                UiHelpers.Signed(economics.ProfitPerDay * factor, currency));
        }

        AnsiConsole.Write(table);

        if (economics.CostPerXmr is { } costPerXmr)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Cost to mine 1 XMR:[/] {costPerXmr:N2} {UiHelpers.Escape(currency)}  " +
                $"[grey](break-even XMR price at current draw)[/]");
        }

        AnsiConsole.WriteLine();
        var breakdown = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per node, per day[/]")
            .AddColumn("Node")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Share").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn("Cost").RightAligned())
            .AddColumn(new TableColumn("Income").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var state in states.Where(s => s.Mining).OrderByDescending(s => s.Hashrate))
        {
            // Income is split by hashrate share, cost by actual draw: that is what makes
            // one node profitable and another not.
            var share = economics.TotalHashrate > 0 ? state.Hashrate / economics.TotalHashrate : 0;
            var cost = state.PowerWatts / 1000.0 * 24.0 * _config.Electricity.PricePerKwh;
            var income = economics.RevenuePerDay * share;

            breakdown.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(Economics.FormatHashrate(state.Hashrate)),
                new Markup($"{share:P1}"),
                new Markup($"{state.PowerWatts:0}"),
                new Markup(UiHelpers.Money(cost, currency)),
                new Markup(UiHelpers.Money(income, currency)),
                new Markup(UiHelpers.Signed(income - cost, currency)));
        }

        AnsiConsole.Write(breakdown);

        AnsiConsole.MarkupLine(
            "[grey]Income is an expectation from network difficulty, not a payout record. " +
            "Compare it against the pool balance under Pool & wallet.[/]");

        UiHelpers.Pause();
    }
}
