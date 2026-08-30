using Spectre.Console;
using XmrigFleet.Console;
using XmrigFleet.Console.Ui;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Console.Title = "xmrig fleet";

FleetConfig config;
try
{
    config = FleetConfig.Load();
}
catch (InvalidOperationException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

// First run: leave a config on disk so the file can be edited by hand too.
if (!File.Exists(config.Path)) config.Save();

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var fleet = new FleetService(config);
using var market = new MarketService(config);

// One-shot mode for scripts and scheduled tasks.
if (args.Length > 0)
    return await Cli.RunAsync(args, config, fleet, market, cts.Token);

// The menu drives the cursor directly, which needs a real terminal.
if (System.Console.IsOutputRedirected || System.Console.IsInputRedirected)
{
    AnsiConsole.WriteLine("The interactive console needs a terminal. Use a command instead:");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(Cli.Usage);
    return 2;
}

var dashboard = new Dashboard(config, fleet, market);
var nodes = new NodesScreen(config, fleet);
var miner = new MinerScreen(config, fleet);
var hardware = new HardwareScreen(config, fleet);
var economics = new EconomicsScreen(config, fleet, market);
var pool = new PoolScreen(config, market);
var settings = new SettingsScreen(config);

try
{
    while (!cts.IsCancellationRequested)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("xmrig fleet").Color(Color.Aqua));
        AnsiConsole.MarkupLine(
            $"[grey]{config.Nodes.Count(n => n.Enabled)} enabled node(s)  |  pool {Markup.Escape(config.Pool.Url)}  |  {Markup.Escape(config.Path)}[/]");
        if (string.IsNullOrWhiteSpace(config.Token))
            AnsiConsole.MarkupLine("[yellow]No fleet token set - agents with a token will reject this console.[/]");
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Main menu")
            .PageSize(12)
            .AddChoices(
                "Dashboard (live)",
                "Miner control",
                "Nodes",
                "Hardware & sensors",
                "Economics",
                "Pool & wallet",
                "Settings",
                "Exit"));

        switch (choice)
        {
            case "Dashboard (live)": await dashboard.ShowAsync(cts.Token); break;
            case "Miner control": await miner.ShowAsync(cts.Token); break;
            case "Nodes": await nodes.ShowAsync(cts.Token); break;
            case "Hardware & sensors": await hardware.ShowAsync(cts.Token); break;
            case "Economics": await economics.ShowAsync(cts.Token); break;
            case "Pool & wallet": await pool.ShowAsync(cts.Token); break;
            case "Settings": await settings.ShowAsync(cts.Token); break;
            default: return 0;
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C on a screen that was waiting on the network.
}

return 0;
