using System.Globalization;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>One site of a side-by-side comparison (its best landing day in the current heatmap).</summary>
public sealed record CompareEntry(string SiteId, string Name, int SiteIndex, int Day, double Score, SimulationResult? Mission);

/// <summary>
/// UI-facing orchestration of RUN ANALYSIS. Two phases so a result appears fast:
/// (1) the selected site only → verdict + timeline, (2) all regions → heatmap.
/// </summary>
public sealed class AnalysisService(MissionConfigState state, IlluminationRepository repo)
{
    public event Action? OnChanged;

    public bool IsRunning { get; private set; }
    public bool HeatmapPending { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }

    public HeatmapResult? Heatmap { get; private set; }
    public SimulationResult? Mission { get; private set; }
    public string? MissionSiteId { get; private set; }
    public string MissionSiteName { get; private set; } = "";
    public double ThresholdBatteryPct { get; private set; }

    /// <summary>Bumped when the results strip should expand (explicit runs, quick demo).</summary>
    public int ExpandToken { get; private set; }

    /// <summary>Bumped when the heatmap should pulse its BEST WINDOW cell.</summary>
    public int HighlightBestToken { get; private set; }

    private string? _signature;
    private CancellationTokenSource? _cts;
    private Task? _current;

    public bool HasResults => Mission is not null;

    /// <summary>Lander, what-if, duration, thresholds or window changed since the last run.</summary>
    public bool IsOutdated => HasResults && _signature != Signature();

    /// <summary>A new run may always start: it cancels a run that is still in progress.</summary>
    public bool CanRun => state.SelectedLander is not null && IsAnalyzableSite(state.SelectedSite);

    public string? CannotRunReason =>
        state.SelectedLander is null ? "select a lander"
        : !IsAnalyzableSite(state.SelectedSite) ? "select one of the 9 regions (custom points have no precomputed data)"
        : null;

    private bool IsAnalyzableSite(Site? site) =>
        site is not null && state.AvailableSites.Any(s => s.Id == site.Id);

    /// <summary>Effective lander inputs (what-if applied) and their weakest provenance.</summary>
    public LanderInputs? BuildLanderInputs()
    {
        var l = state.SelectedLander;
        var solar = state.Effective(MissionConfigState.WhatIfSolarArrayW);
        var battery = state.Effective(MissionConfigState.WhatIfBatteryWh);
        var survival = state.Effective(MissionConfigState.WhatIfSurvivalLoadW);
        if (l?.DayLoadW is null || solar is null || battery is null || survival is null) return null;

        var kind = state.HasWhatIf ? ParamKind.WhatIf
            : ParamKind.Weakest(l.SolarArrayW, l.BatteryWh, l.DayLoadW, l.SurvivalLoadW);
        return new LanderInputs(solar.Value, battery.Value, l.DayLoadW.Value, survival.Value, kind);
    }

    private MissionThresholds Thresholds => new(state.MinBatteryPercent, state.MinCommsPercent);

    private string Signature()
    {
        var l = BuildLanderInputs();
        return string.Join("|", state.SelectedLander?.Id, l?.SolarArrayW, l?.BatteryWh, l?.DayLoadW, l?.SurvivalLoadW,
            state.MissionDurationDays.ToString(CultureInfo.InvariantCulture), state.MinBatteryPercent, state.MinCommsPercent,
            state.WindowStart.ToString("yyyy-MM-dd"), state.WindowEnd.ToString("yyyy-MM-dd"));
    }

    /// <param name="expand">Expand the results strip when the first result is ready.</param>
    /// <param name="highlightBest">Pulse the BEST WINDOW cell once the heatmap is ready.</param>
    public async Task RunAsync(bool expand = true, bool highlightBest = false)
    {
        // A click during a running analysis (e.g. QUICK DEMO while the startup run is still
        // computing the heatmap) restarts it instead of being silently ignored.
        if (_current is { IsCompleted: false })
        {
            _cts?.Cancel();
            try { await _current; } catch (OperationCanceledException) { }
        }
        if (!CanRun) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _current = RunCoreAsync(expand, highlightBest, _cts.Token);
        await _current;
    }

    private async Task RunCoreAsync(bool expand, bool highlightBest, CancellationToken ct)
    {
        var lander = BuildLanderInputs();
        if (lander is null) { Error = "selected lander has incomplete power data"; Notify(); return; }

        IsRunning = true;
        HeatmapPending = true;
        Progress = 0;
        Error = null;
        Heatmap = null;
        Notify();
        try
        {
            var selected = state.SelectedSite!;
            var duration = state.MissionDurationDays;

            // ---- Phase 1: selected site only → verdict + timeline in well under a second ----
            var selectedSite = new OptimizerSite(selected.Id, selected.Name, await repo.GetSeries(selected.Id));
            var quick = await WindowOptimizer.RunAsync([selectedSite], lander, state.WindowStart, state.WindowEnd, duration, Thresholds, ct: ct);
            if (quick.Days == 0)
            {
                Error = $"no landing day in the window leaves room for a {duration:0.#}-day mission ({state.DataRangeText})";
                return;
            }
            var quickDay = quick.BestDayFor(0);
            SimulateSite(selectedSite, quick.DayUtc(quickDay < 0 ? 0 : quickDay), lander);
            _signature = Signature();
            Progress = 0.15;
            if (expand) ExpandToken++;
            Notify();
            await Task.Yield();

            // ---- Phase 2: every region → heatmap ----
            var sites = new List<OptimizerSite>();
            foreach (var s in state.AvailableSites)
            {
                ct.ThrowIfCancellationRequested();
                sites.Add(s.Id == selectedSite.Id ? selectedSite : new OptimizerSite(s.Id, s.Name, await repo.GetSeries(s.Id)));
                Progress = 0.15 + 0.15 * sites.Count / state.AvailableSites.Count;
                Notify();
            }

            // Re-render at most ~50 times per run: every notification re-renders the panels.
            var lastBucket = -1;
            var progress = new ActionProgress(p =>
            {
                Progress = 0.3 + 0.7 * p;
                var bucket = (int)(Progress * 50);
                if (bucket != lastBucket) { lastBucket = bucket; Notify(); }
            });
            Heatmap = await WindowOptimizer.RunAsync(sites, lander, state.WindowStart, state.WindowEnd, duration, Thresholds, progress, ct);
            if (highlightBest) HighlightBestToken++;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run; that run reports its own state.
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"[Analysis] data download failed: {ex.Message}");
            Error = "could not download illumination data — check the connection and press RUN ANALYSIS again";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Analysis] {ex}");
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
            HeatmapPending = false;
            Progress = 1;
            Notify();
        }
    }

    /// <summary>Heatmap click: select that site (camera flies there) and simulate that landing day.</summary>
    public async Task SelectCellAsync(int siteIndex, int dayIndex)
    {
        if (Heatmap is null || siteIndex < 0 || siteIndex >= Heatmap.Sites.Count || dayIndex < 0 || dayIndex >= Heatmap.Days) return;
        var lander = BuildLanderInputs();
        if (lander is null) return;
        await state.SelectSite(Heatmap.Sites[siteIndex].Id);
        SimulateSite(Heatmap.Sites[siteIndex], Heatmap.DayUtc(dayIndex), lander);
        Notify();
    }

    private void SimulateSite(OptimizerSite site, DateTime landingUtc, LanderInputs lander)
    {
        Mission = MissionSimulator.Run(site.Series, lander, landingUtc, state.MissionDurationDays, Thresholds);
        MissionSiteId = site.Id;
        MissionSiteName = site.Name;
        ThresholdBatteryPct = state.MinBatteryPercent;
    }

    public (int Site, int Day) SelectedCell =>
        Heatmap is null || Mission is null || MissionSiteId is null
            ? (-1, -1)
            : (Heatmap.Sites.ToList().FindIndex(s => s.Id == MissionSiteId), (int)(Mission.LandingUtc.Date - Heatmap.FirstDayUtc.Date).TotalDays);

    /// <summary>Sites ranked by their best score (for the default comparison).</summary>
    public IReadOnlyList<string> TopSites(int count)
    {
        if (Heatmap is null) return [];
        return Enumerable.Range(0, Heatmap.Sites.Count)
            .Select(s => (s, day: Heatmap.BestDayFor(s)))
            .OrderByDescending(x => x.day < 0 ? 0 : Heatmap.Scores[x.s, x.day])
            .Take(count)
            .Select(x => Heatmap.Sites[x.s].Id)
            .ToList();
    }

    /// <summary>
    /// Side-by-side comparison: each site at its own best landing day, simulated with the same
    /// lander inputs as the heatmap. Uses the cached series, so it is synchronous and cheap.
    /// </summary>
    public IReadOnlyList<CompareEntry> Compare(IEnumerable<string> siteIds)
    {
        if (Heatmap is null) return [];
        var lander = BuildLanderInputs();
        var result = new List<CompareEntry>();
        foreach (var id in siteIds)
        {
            var s = Heatmap.Sites.ToList().FindIndex(x => x.Id == id);
            if (s < 0) continue;
            var day = Heatmap.BestDayFor(s);
            SimulationResult? mission = null;
            if (day >= 0 && lander is not null)
            {
                mission = MissionSimulator.Run(Heatmap.Sites[s].Series, lander, Heatmap.DayUtc(day), state.MissionDurationDays, Thresholds);
            }
            result.Add(new CompareEntry(id, Heatmap.Sites[s].Name, s, day, day < 0 ? 0 : Heatmap.Scores[s, day], mission));
        }
        return result;
    }

    private void Notify() => OnChanged?.Invoke();

    /// <summary>Reports synchronously; Progress&lt;T&gt; would post to the sync context and lag behind the work.</summary>
    private sealed class ActionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
