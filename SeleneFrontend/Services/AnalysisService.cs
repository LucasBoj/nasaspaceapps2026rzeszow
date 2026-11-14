using System.Globalization;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// UI-facing orchestration of RUN ANALYSIS: loads the series, runs the optimizer over all
/// sites, simulates the chosen mission and tracks whether results are outdated.
/// </summary>
public sealed class AnalysisService(MissionConfigState state, IlluminationRepository repo)
{
    public event Action? OnChanged;

    public bool IsRunning { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }

    public HeatmapResult? Heatmap { get; private set; }
    public SimulationResult? Mission { get; private set; }
    public string? MissionSiteId { get; private set; }
    public string MissionSiteName { get; private set; } = "";
    public double ThresholdBatteryPct { get; private set; }

    /// <summary>Incremented whenever a new analysis finishes (the results strip auto-expands on change).</summary>
    public int RunCount { get; private set; }

    private string? _signature;

    public bool HasResults => Heatmap is not null;

    /// <summary>Lander, what-if, duration, thresholds or window changed since the last run.</summary>
    public bool IsOutdated => HasResults && _signature != Signature();

    public bool CanRun => !IsRunning && state.SelectedLander is not null && IsAnalyzableSite(state.SelectedSite);

    public string? CannotRunReason =>
        IsRunning ? "analysis running"
        : state.SelectedLander is null ? "select a lander"
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

    public async Task RunAsync()
    {
        if (!CanRun) return;
        var lander = BuildLanderInputs();
        if (lander is null) { Error = "selected lander has incomplete power data"; Notify(); return; }

        IsRunning = true;
        Progress = 0;
        Error = null;
        Notify();
        try
        {
            var sites = new List<OptimizerSite>();
            foreach (var s in state.AvailableSites)
            {
                sites.Add(new OptimizerSite(s.Id, s.Name, await repo.GetSeries(s.Id)));
                Progress = 0.1 * sites.Count / state.AvailableSites.Count;   // loading = first 10 %
                Notify();
                await Task.Yield();
            }

            // Re-render at most ~50 times per run: every notification re-renders the panels.
            var lastBucket = -1;
            var progress = new ActionProgress(p =>
            {
                Progress = 0.1 + 0.9 * p;
                var bucket = (int)(Progress * 50);
                if (bucket != lastBucket) { lastBucket = bucket; Notify(); }
            });
            var heatmap = await WindowOptimizer.RunAsync(sites, lander, state.WindowStart, state.WindowEnd,
                state.MissionDurationDays, Thresholds, progress);
            if (heatmap.Days == 0)
            {
                Error = $"no landing day in the window leaves room for a {state.MissionDurationDays:0.#}-day mission ({state.DataRangeText})";
                return;
            }

            Heatmap = heatmap;
            _signature = Signature();

            // Detailed mission: the selected site on its best landing day (fallback: first day).
            var siteIndex = sites.FindIndex(s => s.Id == state.SelectedSite!.Id);
            var day = heatmap.BestDayFor(siteIndex);
            SimulateCell(siteIndex, day < 0 ? 0 : day, lander);
            RunCount++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Analysis] {ex}");
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
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
        SimulateCell(siteIndex, dayIndex, lander);
        Notify();
    }

    private void SimulateCell(int siteIndex, int dayIndex, LanderInputs lander)
    {
        var site = Heatmap!.Sites[siteIndex];
        Mission = MissionSimulator.Run(site.Series, lander, Heatmap.DayUtc(dayIndex), state.MissionDurationDays, Thresholds);
        MissionSiteId = site.Id;
        MissionSiteName = site.Name;
        ThresholdBatteryPct = state.MinBatteryPercent;
    }

    public (int Site, int Day) SelectedCell =>
        Heatmap is null || Mission is null || MissionSiteId is null
            ? (-1, -1)
            : (Heatmap.Sites.ToList().FindIndex(s => s.Id == MissionSiteId), (int)(Mission.LandingUtc.Date - Heatmap.FirstDayUtc.Date).TotalDays);

    private void Notify() => OnChanged?.Invoke();

    /// <summary>Reports synchronously; Progress&lt;T&gt; would post to the sync context and lag behind the work.</summary>
    private sealed class ActionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
