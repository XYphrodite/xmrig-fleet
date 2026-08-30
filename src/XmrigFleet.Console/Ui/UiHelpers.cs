using Spectre.Console;

namespace XmrigFleet.Console.Ui;

public static class UiHelpers
{
    public static string Escape(string? value) => Markup.Escape(value ?? "");

    public static void Header(string title)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold aqua]{Escape(title)}[/]").LeftJustified());
        AnsiConsole.WriteLine();
    }

    public static void Pause(string message = "Enter to go back")
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]{Escape(message)}[/]");
        System.Console.ReadLine();
    }

    public static void Result(bool ok, string message)
    {
        var colour = ok ? "green" : "red";
        var mark = ok ? "OK" : "FAIL";
        AnsiConsole.MarkupLine($"[{colour}]{mark}[/] {Escape(message)}");
    }

    public static string Temperature(double? celsius) => celsius switch
    {
        null => "[grey]-[/]",
        >= 85 => $"[red]{celsius:0}C[/]",
        >= 75 => $"[yellow]{celsius:0}C[/]",
        _ => $"[green]{celsius:0}C[/]",
    };

    public static string StatusBadge(NodeState state) => state switch
    {
        { Online: false } => $"[red]offline[/] [grey]{Escape(state.Error)}[/]",
        // A miner this agent did not start keeps its own API token, so hashrate is unreadable
        // until it is restarted through the fleet. Say so instead of showing a blank rate.
        { Mining: true, Hashrate: 0, Snapshot.Miner.ApiError: not null } => "[yellow]mining (no api)[/]",
        { Mining: true } => "[green]mining[/]",
        { Snapshot.Miner.Installed: false } => "[yellow]no miner[/]",
        _ => "[grey]idle[/]",
    };

    public static string Money(double? amount, string currency) =>
        amount is null ? "[grey]-[/]" : $"{amount.Value:N2} {Escape(currency)}";

    public static string Signed(double? amount, string currency)
    {
        if (amount is null) return "[grey]-[/]";
        var colour = amount.Value >= 0 ? "green" : "red";
        return $"[{colour}]{amount.Value:N2} {Escape(currency)}[/]";
    }

    /// <summary>Picks one node, or null when the fleet is empty or the user backs out.</summary>
    public static NodeConfig? SelectNode(FleetConfig config, string prompt = "Node")
    {
        if (config.Nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes configured yet.[/]");
            Pause();
            return null;
        }

        var choices = config.Nodes.Select(n => n.ToString()).Append("< back").ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title(Escape(prompt))
            .PageSize(15)
            .AddChoices(choices));

        return picked == "< back" ? null : config.Nodes.FirstOrDefault(n => n.ToString() == picked);
    }

    /// <summary>Picks any number of nodes; an empty result means the user chose nothing.</summary>
    public static IReadOnlyList<NodeConfig> SelectNodes(FleetConfig config, string prompt)
    {
        var enabled = config.Nodes.Where(n => n.Enabled).ToList();
        if (enabled.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No enabled nodes.[/]");
            Pause();
            return [];
        }

        const string all = "* all enabled nodes";
        var picked = AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title(Escape(prompt))
            .PageSize(15)
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
            .AddChoices(enabled.Select(n => n.ToString()).Prepend(all)));

        if (picked.Contains(all)) return enabled;
        return enabled.Where(n => picked.Contains(n.ToString())).ToList();
    }
}
