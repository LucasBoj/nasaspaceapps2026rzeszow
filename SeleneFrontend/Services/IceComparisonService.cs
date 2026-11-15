using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// Keeps <see cref="IceComparison"/> up to date with the shown mission: after each analysis (once the all-regions run has
/// finished, so it never competes with it) the same mission is simulated at the region's ice-first site, and that site's
/// best date in the window is found. Read-only use of AnalysisService / MissionConfigState / IlluminationRepository.
/// </summary>
public sealed class IceComparisonService : IDisposable
{
    private readonly AnalysisService _analysis;
    private readonly MissionConfigState _state;
    private readonly IlluminationRepository _repo;
    private int _version = -1;
    private CancellationTokenSource? _cts;

    public IceComparisonService(AnalysisService analysis, MissionConfigState state, IlluminationRepository repo)
    {
        (_analysis, _state, _repo) = (analysis, state, repo);
        _analysis.OnChanged += HandleAnalysisChanged;
        _state.OnStateChanged += HandleAnalysisChanged;      // the PSR file may arrive after the first analysis
    }

    public event Action? OnChanged;
    public IceComparisonResult? Result { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>The result belongs to the mission now shown.</summary>
    public bool IsCurrent => Result is not null && _version == _analysis.MissionVersion;

    private void HandleAnalysisChanged()
    {
        if (_analysis.IsRunning || _analysis.Mission is null || _analysis.MissionVersion == _version) return;
        if (_state.Psr?.RegionFor(_analysis.MissionSiteId)?.IceSite is null)
        {
            if (Result is not null) { Result = null; OnChanged?.Invoke(); }
            return;
        }
        _version = _analysis.MissionVersion;
        _ = ComputeAsync(_version);
    }

    private async Task ComputeAsync(int version)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Result = null;
        Busy = true;
        OnChanged?.Invoke();
        try
        {
            await Task.Yield();
            var mission = _analysis.Mission;
            var region = _state.Psr?.RegionFor(_analysis.MissionSiteId);
            var lander = _analysis.BuildLanderInputs();
            if (mission is null || region?.IceSite is not { } ice || lander is null) return;
            var series = await _repo.GetSeries(ice.SiteId);
            if (version != _analysis.MissionVersion) return;
            var thresholds = new MissionThresholds(_state.MinBatteryPercent, _state.MinCommsPercent);
            var same = IceComparison.SameDate(series, lander, mission, thresholds);
            var best = await IceComparison.BestInWindowAsync(new OptimizerSite(ice.SiteId, ice.Name, series), lander,
                _state.WindowStart, _state.WindowEnd, mission.DurationDays, thresholds, ct);
            if (version != _analysis.MissionVersion || ct.IsCancellationRequested) return;
            Result = new IceComparisonResult(_analysis.MissionSiteName, mission, ice, same, best, region.Site,
                region.Candidates.FirstOrDefault(c => c.CandidateId == ice.CandidateId));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[IceComparison] {ex.Message}");
        }
        finally
        {
            Busy = false;
            OnChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _analysis.OnChanged -= HandleAnalysisChanged;
        _state.OnStateChanged -= HandleAnalysisChanged;
        _cts?.Cancel();
    }
}
