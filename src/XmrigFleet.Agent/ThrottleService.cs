using XmrigFleet.Contracts;

namespace XmrigFleet.Agent;

/// <summary>
/// Keeps the miner out of the way of whoever is using the machine.
///
/// Two mechanisms, because one rung needs something the others do not:
///
///   25-100%  a CPU cap on the miner process (<see cref="MinerCpuLimit"/>). Instant, and the
///            RandomX dataset and huge pages stay exactly where they are.
///   0%       the miner is stopped outright. A capped miner still holds ~2.3 GB for its dataset,
///            and on a 16 GB node that memory is the thing that makes the machine feel slow -
///            measured three times on the Xeon, where freeing about 2 GB moved the hashrate 3.3x.
///            Nothing short of stopping gives it back.
///
/// Off unless a node is told otherwise. Throttling a rig nobody sits at only loses money.
/// </summary>
public sealed class ThrottleService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly MinerConfigStore _config;
    private readonly MinerService _miner;
    private readonly MinerCpuLimit _limit;
    private readonly ThrottleLog _decisions;
    private readonly ILogger<ThrottleService> _log;
    private readonly SystemLoadReader _loadReader = new();
    private readonly ThrottleLadder _ladder = new();
    private readonly object _gate = new();

    private SystemLoad _lastLoad;
    private bool _wasEnabled;

    public ThrottleService(
        MinerConfigStore config,
        MinerService miner,
        MinerCpuLimit limit,
        ThrottleLog decisions,
        ILogger<ThrottleService> log)
    {
        _config = config;
        _miner = miner;
        _limit = limit;
        _decisions = decisions;
        _log = log;
    }

    /// <summary>What the console shows: the rung, the cause, and the readings behind it.</summary>
    public ThrottleStatusDto Status()
    {
        lock (_gate)
        {
            var settings = _config.Current.Throttle;
            return new ThrottleStatusDto
            {
                Enabled = settings?.Enabled == true,
                Level = _ladder.Current,
                Reason = _ladder.Reason,
                Manual = settings?.ManualLevel is not null,
                OtherCpuPercent = _wasEnabled ? _lastLoad.OtherCpuPercent : null,
                MemoryUsedPercent = _wasEnabled ? _lastLoad.MemoryUsedPercent : null,
                SecondsAtLevel = (DateTimeOffset.UtcNow - _ladder.ChangedAt).TotalSeconds,
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Same rule as the session monitor: this service exists to make a machine pleasant
                // to use. It must never be the reason a node stops answering the console.
                _log.LogDebug(ex, "Throttle tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _config.Current;
        var settings = config.Throttle;

        if (settings?.Enabled != true)
        {
            if (_wasEnabled) await StandDownAsync(ct);
            return;
        }

        if (!_wasEnabled)
        {
            // Percentages are differences between two samples; a stale one from before the
            // feature was switched on would read as a load spike that never happened.
            _loadReader.Reset();
            _wasEnabled = true;
        }

        var load = _loadReader.Read();
        lock (_gate) _lastLoad = load;

        var previous = _ladder.Current;

        if (settings.ManualLevel is { } pinned)
        {
            _ladder.Force(Math.Clamp(pinned, 0, 100), "pinned by the operator; automation is standing down", DateTimeOffset.UtcNow);
        }
        else
        {
            _ladder.Update(
                load.OtherCpuPercent,
                settings.Steps is { Count: > 0 } steps ? steps : ThrottleSettingsDto.DefaultSteps,
                Math.Clamp(settings.FloorLevel ?? 0, 0, 100),
                Math.Max(1, settings.RampUpSeconds ?? 120),
                DateTimeOffset.UtcNow);
        }

        await ApplyAsync(_ladder.Current, ct);

        if (_ladder.Current != previous)
        {
            _decisions.Record(previous, _ladder.Current, _ladder.Reason, load);
            _log.LogInformation("Throttle {From}% -> {To}% ({Reason})", previous, _ladder.Current, _ladder.Reason);
        }
    }

    private async Task ApplyAsync(int level, CancellationToken ct)
    {
        var stoppedByUs = _config.Current.MinerStoppedByThrottle == true;
        var pid = _miner.RunningPid();

        if (level <= 0)
        {
            if (pid is null) return;

            _log.LogWarning("Throttle: stopping the miner to release its memory ({Reason})", _ladder.Reason);
            var result = await _miner.StopAsync(ct);
            _limit.Forget();
            // Recorded before checking success: a stop that half-worked still means this service,
            // not the operator, is why the miner is down.
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = true });
            if (!result.Ok) _log.LogWarning("Throttle could not stop the miner: {Message}", result.Message);
            return;
        }

        if (pid is null)
        {
            // Only a miner this service stopped is started again. An operator who stopped mining
            // deliberately must not find it running because the machine went quiet.
            if (!stoppedByUs) return;

            _log.LogInformation("Throttle: starting the miner again ({Reason})", _ladder.Reason);
            var result = await _miner.StartAsync(ct);
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = false });
            if (!result.Ok)
            {
                _log.LogWarning("Throttle could not start the miner: {Message}", result.Message);
                return;
            }

            pid = _miner.RunningPid();
            _loadReader.Reset();
            if (pid is null) return;
        }
        else if (stoppedByUs)
        {
            // Somebody started it by hand while the throttle had it down. Their call now.
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = false });
        }

        if (_limit.AppliedLevel == level) return;

        if (!_limit.Apply(pid.Value, level, out var detail))
            _log.LogWarning("Throttle could not hold the miner at {Level}%: {Detail}", level, detail);
    }

    /// <summary>Undoes everything this service did, so switching it off really switches it off.</summary>
    private async Task StandDownAsync(CancellationToken ct)
    {
        _wasEnabled = false;

        if (_miner.RunningPid() is { } pid) _limit.Apply(pid, 100, out _);
        _ladder.Force(100, "throttling is off", DateTimeOffset.UtcNow);

        if (_config.Current.MinerStoppedByThrottle == true)
        {
            _log.LogInformation("Throttling switched off; starting the miner it had stopped");
            var result = await _miner.StartAsync(ct);
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = false });
            if (!result.Ok) _log.LogWarning("Could not start the miner again: {Message}", result.Message);
        }
    }
}
