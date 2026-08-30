using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using XmrigFleet.Contracts;

namespace XmrigFleet.Agent;

/// <summary>
/// Updates the agent itself from a published xmrig-fleet release, so a fleet-wide roll-out does
/// not need an RDP session per node.
///
/// Two rules make this safe to drive remotely:
///
/// 1. <see cref="ProtectedFiles"/> is never overwritten. The node's identity lives in those files
///    - the fleet token, the xmrig API token and the pushed miner config. Replacing the token
///    locks the console out of the very node it was updating, and only a visit to the machine
///    gets it back.
/// 2. The payload is verified to contain the agent executable before anything is moved. A wrong
///    or truncated archive must fail loudly while the node still works, never half-installed.
///
/// The running executable cannot be deleted, but it can be renamed, so each file is moved aside
/// to <c>.old</c> before the new one is copied over. The process then exits with a non-zero code:
/// install-agent.ps1 registers SCM failure actions (restart/5000/...), and the systemd unit sets
/// Restart=always, so both service managers bring the new binary straight back up. The miner is a
/// separate process and keeps hashing throughout.
/// </summary>
public sealed class AgentUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/XYphrodite/xmrig-fleet/releases";
    private const string BackupSuffix = ".old";

    /// <summary>Exit code the agent uses to ask its service manager for a restart.</summary>
    private const int RestartExitCode = 3;

    /// <summary>Node-specific state that survives every update. See the class remarks.</summary>
    private static readonly string[] ProtectedFiles =
    [
        "appsettings.json",
        "appsettings.Production.json",
        "xmrig-api.token",
        "miner.json",
    ];

    /// <summary>Exposed so a test can assert the list still covers everything that identifies a node.</summary>
    public static IReadOnlyList<string> ProtectedFileNames => ProtectedFiles;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AgentUpdateService> _log;

    public AgentUpdateService(IHttpClientFactory httpFactory, ILogger<AgentUpdateService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString();

    private static string InstallDirectory => AppContext.BaseDirectory;

    public async Task<AgentUpdateResultDto> UpdateAsync(AgentUpdateRequestDto request, CancellationToken ct)
    {
        var from = CurrentVersion;
        var staging = Path.Combine(Path.GetTempPath(), $"xmrig-fleet-agent-update-{Guid.NewGuid():N}");
        var archive = staging + ".zip";

        try
        {
            var http = _httpFactory.CreateClient("github");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("xmrig-fleet-agent");

            string url;
            string? tag = null;

            if (!string.IsNullOrWhiteSpace(request.DownloadUrl))
            {
                url = request.DownloadUrl!;
            }
            else
            {
                var asset = await ResolveAssetAsync(http, request.Version, ct);
                if (asset is null)
                    return new AgentUpdateResultDto(false, $"No release asset named {AssetName} was found.", from, null, false);

                (url, tag) = asset.Value;

                if (!request.Force && IsSameVersion(from, tag))
                    return new AgentUpdateResultDto(true, $"Already running {from}; nothing to do.", from, tag, false);
            }

            _log.LogInformation("Agent update: downloading {Url}", url);
            await DownloadAsync(http, url, archive, ct);

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);

            // Refuse to touch the installation unless the payload is really an agent build.
            var exeName = OperatingSystem.IsWindows() ? "xmrig-fleet-agent.exe" : "xmrig-fleet-agent";
            if (!File.Exists(Path.Combine(staging, exeName)))
                return new AgentUpdateResultDto(false, $"Downloaded payload does not contain {exeName}; installation left untouched.", from, tag, false);

            var written = SwapIntoPlace(staging, InstallDirectory);
            _log.LogWarning("Agent update: {Count} files replaced, restarting into {Version}", written, tag ?? "the new build");

            ScheduleRestart();

            return new AgentUpdateResultDto(
                true,
                $"Updated from {from} to {tag ?? "the downloaded build"} ({written} files). Restarting; the miner keeps running.",
                from,
                tag,
                true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _log.LogError(ex, "Agent update failed");
            return new AgentUpdateResultDto(false, ex.Message, from, null, false);
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Leaves long enough for the HTTP response to reach the console, then exits non-zero so the
    /// service manager restarts the process on the freshly written binary.
    ///
    /// Deliberately a hard exit rather than IHostApplicationLifetime.StopApplication(): a
    /// graceful shutdown returns exit code 0, which SCM reads as "this service was meant to
    /// stop" and leaves the node dead until somebody visits it. Only a non-zero code triggers
    /// the configured failure actions. Nothing here needs an orderly shutdown - the miner is a
    /// separate process and the config is written synchronously.
    /// </summary>
    private void ScheduleRestart() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        _log.LogWarning("Agent update: exiting with {Code} so the service manager restarts the new binary", RestartExitCode);
        Environment.Exit(RestartExitCode);
    });

    /// <summary>
    /// Copies the payload over the installation, renaming any file in use out of the way first.
    /// Files in <see cref="ProtectedFiles"/> are skipped: they carry this node's identity.
    /// </summary>
    private static int SwapIntoPlace(string source, string target)
    {
        var written = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            if (ProtectedFiles.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
                continue;

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                var backup = destination + BackupSuffix;
                TryDelete(backup);
                // The running executable cannot be deleted, but it can be renamed.
                File.Move(destination, backup);
            }

            File.Copy(file, destination, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>Removes files displaced by an earlier update. Safe to call on every start.</summary>
    public static void CleanUpPreviousUpdate()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(InstallDirectory, "*" + BackupSuffix, SearchOption.AllDirectories))
                TryDelete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers waste a few megabytes; never let cleanup stop the agent from starting.
        }
    }

    private static async Task<(string Url, string Tag)?> ResolveAssetAsync(HttpClient http, string? version, CancellationToken ct)
    {
        using var response = await http.GetAsync(ReleasesApi, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var wanted = string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? null
            : version;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null) continue;
            if (wanted is not null && !tag.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (wanted is null && release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                // Matched in full: a release also carries the console zip, and a fragment match
                // would happily unpack the console over this agent.
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;

                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url is not null) return (url, tag);
            }

            if (wanted is not null) return null;
        }

        return null;
    }

    /// <summary>The one asset this agent may install, e.g. xmrig-fleet-agent-win-x64.zip.</summary>
    public static string AssetName
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"xmrig-fleet-agent-{os}-{arch}.zip";
        }
    }

    /// <summary>Assembly versions carry four parts, release tags three: compare what both have.</summary>
    public static bool IsSameVersion(string assemblyVersion, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return Version.TryParse(assemblyVersion, out var mine)
            && Version.TryParse(tag.TrimStart('v', 'V'), out var theirs)
            && mine.Major == theirs.Major && mine.Minor == theirs.Minor && mine.Build == theirs.Build;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        await source.CopyToAsync(file, ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
