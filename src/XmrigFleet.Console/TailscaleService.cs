using System.Diagnostics;
using System.Text.Json;

namespace XmrigFleet.Console;

public sealed record TailnetMachine(string Name, string Address, string Os, bool Online, string? LastSeen);

/// <summary>
/// Reads the local tailnet through the tailscale CLI so nodes can be added by picking
/// them from a list instead of typing 100.x addresses by hand.
/// </summary>
public static class TailscaleService
{
    public static async Task<IReadOnlyList<TailnetMachine>> ListAsync(CancellationToken ct)
    {
        var json = await RunAsync(ct);
        if (json is null) return [];

        using var doc = JsonDocument.Parse(json);
        var machines = new List<TailnetMachine>();

        if (doc.RootElement.TryGetProperty("Self", out var self))
            Add(self, machines);

        if (doc.RootElement.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
        {
            foreach (var peer in peers.EnumerateObject())
                Add(peer.Value, machines);
        }

        return machines.OrderByDescending(m => m.Online).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Add(JsonElement element, List<TailnetMachine> into)
    {
        var name = element.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
        if (name.Length == 0) return;

        var address = element.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
            ? ips.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => i is not null && i.Contains('.')) ?? ""
            : "";
        if (address.Length == 0) return;

        into.Add(new TailnetMachine(
            name,
            address,
            element.TryGetProperty("OS", out var os) ? os.GetString() ?? "" : "",
            element.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True,
            element.TryGetProperty("LastSeen", out var seen) ? seen.GetString() : null));
    }

    private static async Task<string?> RunAsync(CancellationToken ct)
    {
        foreach (var exe in CandidateExecutables())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exe, "status --json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) continue;

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0 && output.TrimStart().StartsWith('{')) return output;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
            {
                // Try the next candidate path.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateExecutables()
    {
        yield return "tailscale";
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Tailscale\tailscale.exe";
            yield return @"C:\Program Files (x86)\Tailscale\tailscale.exe";
        }
        else
        {
            yield return "/usr/bin/tailscale";
            yield return "/usr/local/bin/tailscale";
            yield return "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
        }
    }
}
