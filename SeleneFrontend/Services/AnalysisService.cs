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

    /// <summary>Bumped every time <see cref="Mission"/> is replaced (tells the 3D lander a new heading is ready).</summary>
    public int MissionVersion { get; private set; }
    public string? MissionSiteId { get; private set; }
    public string MissionSiteName { get; private set; } = "";
    public double ThresholdBatteryPct { get; private set; }

    /// <summary>Bumped when the results strip should expand (explicit runs, quick demo).</summary>
    public int ExpandToken { get; private set; }

    /// <summary>Bumped when the heatmap should pulse its BEST WINDOW cell.</summary>
    public int HighlightBestToken { get; private set; }

    private string? _signature;

    // The all-regions heatmap depends on the inputs (Signature), not on the selected site. It is computed once per
    // signature and shared: QUICK DEMO or "Land & analyze" right after the startup run, or a click on another site,
    // reuse it (or join the computation still in flight) instead of recomputing ~6,000 missions while the landing
    // animation plays on the same single WebAssembly thread.
    private string? _heatmapKey;
    private Task<HeatmapResult>? _heatmapTask;
    private CancellationTokenSource? _heatmapCts;
    private CancellationTokenSource? _cts;
    private Task? _current;

    public bool HasResults => Mission is not null;

    /// <summary>Lander, what-if, duration, thresholds or window changed since the last run.</summary>
    public bool IsOutdated => HasResults && _signature != Signature();

    /// <summary>A new run may always start: it cancels a run that is still in progress.</summary>
    public bool CanRun => state.SelectedLander is not null && IsAnalyzableSite(state.SelectedSite);

    public string? CannotRunReason =>
        state.SelectedLander is null ? "select a lander"
        : state.SelectedSite is { IsCustom: true, CustomUnavailable: { } why } ? why.ToLowerInvariant()
        : state.SelectedSite is { IsCustom: true, CustomChecked: false } ? "wait until the custom point's terrain is checked"
        : !IsAnalyzableSite(state.SelectedSite) ? "select one of the 9 regions or a point in 80–90°S"
        : null;

    private bool IsAnalyzableSite(Site? site) =>
        site is not null && (state.AvailableSites.Any(s => s.Id == site.Id)
                             || site is { IsCustom: true, CustomChecked: true, CustomUnavailable: null });

    /// <summary>Effective lander inputs (what-if applied) and their weakest provenance.</summary>
    public LanderInputs? BuildLanderInputs()
    {
        var l = state.SelectedLander;
        var solar = state.Effective(MissionConfigState.WhatIfSolarArrayW);
        var battery = state.Effective(MissionConfigState.WhatIfBatteryWh);
        var survival = state.Effective(MissionConfigState.WhatIfSurvivalLoadW);
        if (l?.DayLoadW is null || solar is null || battery is null || survival is null) return null;

        var kind = state.HasWhatIf ? ParamKind.WhatIf
            : ParamKind.Weakest(l.SolarArrayW, l.Panels, l.BatteryWh, l.BatteryRoundTripEff, l.DayLoadW, l.SurvivalLoadW);
        return new LanderInputs(solar.Value, battery.Value, l.DayLoadW.Value, survival.Value, kind,
            l.Panels, state.HeadingOverrideDeg, state.IdealTracking, state.BatteryRoundTripEff);
    }

    /// <summary>
    /// A landing date chosen for the user (QUICK DEMO and its counter-example) instead of the optimizer's best day.
    /// It holds only for the site, lander, mission length and window it was pinned with: any change drops it.
    /// </summary>
    private (DateTime Utc, string SiteId, string LanderId, double Days, int WindowRevision)? _pin;

    public DateTime? PinnedLandingUtc =>
        _pin is { } p && p.SiteId == state.SelectedSite?.Id && p.LanderId == state.SelectedLander?.Id
        && p.Days == state.MissionDurationDays && p.WindowRevision == state.WindowRevision ? p.Utc : null;

    /// <summary>Pin the landing date for the current selection (null: back to the best landing window).</summary>
    public void PinLanding(DateTime? utc) =>
        _pin = utc is { } u && state.SelectedSite is { } site && state.SelectedLander is { } l
            ? (TimeUtil.AsUtc(u), site.Id, l.Id, state.MissionDurationDays, state.WindowRevision) : null;

    private MissionThresholds Thresholds => new(state.MinBatteryPercent, state.MinCommsPercent);

    /// <summary>Inputs of the result: the heatmap's inputs plus the pinned landing date (which only phase 1 uses).</summary>
    private string Signature() => HeatmapSignature() + "|" + (PinnedLandingUtc?.ToString("yyyy-MM-dd") ?? "best");

    private string HeatmapSignature()
    {
        var l = BuildLanderInputs();
        return string.Join("|", state.SelectedLander?.Id, l?.SolarArrayW, l?.BatteryWh, l?.DayLoadW, l?.SurvivalLoadW,
            l?.HeadingDeg?.ToString(CultureInfo.InvariantCulture) ?? "auto", l?.IdealTracking,
            l?.BatteryRoundTripEff.ToString(CultureInfo.InvariantCulture),
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

        var key = Signature();
        // A custom point adds its own heatmap row, so its heatmap is not shared with the regions-only one.
        var hkey = HeatmapSignature();
        var heatmapKey = state.SelectedSite is { IsCustom: true } cs ? $"{hkey}|{cs.Id}" : hkey;
        var heatmapReusable = heatmapKey == _heatmapKey && _heatmapTask is { IsFaulted: false, IsCanceled: false };
        IsRunning = true;
        HeatmapPending = !(heatmapReusable && _heatmapTask!.IsCompletedSuccessfully);
        Progress = 0;
        Error = null;
        if (!heatmapReusable) Heatmap = null;
        Notify();
        try
        {
            var selected = state.SelectedSite!;
            var duration = state.MissionDurationDays;

            // ---- Phase 1: selected site only → verdict + timeline in well under a second ----
            // With this signature's heatmap already computed, its row for the site is the same answer: no recompute.
            var cached = heatmapReusable && _heatmapTask!.IsCompletedSuccessfully ? _heatmapTask.Result : null;
            var cachedRow = cached?.Sites.ToList().FindIndex(x => x.Id == selected.Id) ?? -1;
            var quick = cachedRow >= 0 ? cached! : await WindowOptimizer.RunAsync(
                [new OptimizerSite(selected.Id, selected.Name, await repo.GetSeries(selected.Id))],
                lander, state.WindowStart, state.WindowEnd, duration, Thresholds, ct: ct);
            var row = Math.Max(0, cachedRow);
            var selectedSite = quick.Sites[row];
            if (quick.Days == 0)
            {
                Error = $"no landing day in the window leaves room for a {duration:0.#}-day mission ({state.DataRangeText})";
                return;
            }
            // Nothing feasible: show the landing that lasts longest, not simply the first day of the window.
            var quickDay = quick.BestDayFor(row);
            if (quickDay < 0) quickDay = Math.Max(0, quick.LongestSurvivalDayFor(row));
            if (PinnedLandingUtc is { } pinned)
            {
                var pinnedDay = (int)Math.Round((pinned.Date - quick.DayUtc(0).Date).TotalDays);
                if (pinnedDay >= 0 && pinnedDay < quick.Days) quickDay = pinnedDay;
            }
            SimulateSite(selectedSite, quick.DayUtc(quickDay), lander);
            _signature = key;
            Progress = 0.15;
            if (expand) ExpandToken++;
            Notify();
            await Task.Yield();

            // ---- Phase 2: every region → heatmap (shared per signature, see _heatmapTask) ----
            if (!heatmapReusable)
            {
                _heatmapCts?.Cancel();
                _heatmapCts = new CancellationTokenSource();
                _heatmapKey = heatmapKey;
                _heatmapTask = ComputeHeatmapAsync(selectedSite, lander, duration, _heatmapCts.Token);
            }
            // A newer run cancels only this wait; the computation goes on if that run has the same inputs.
            Heatmap = await _heatmapTask!.WaitAsync(ct);
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

    private async Task<HeatmapResult> ComputeHeatmapAsync(OptimizerSite selectedSite, LanderInputs lander, double duration, CancellationToken ct)
    {
        var sites = new List<OptimizerSite>();
        foreach (var s in state.AvailableSites)
        {
            ct.ThrowIfCancellationRequested();
            sites.Add(s.Id == selectedSite.Id ? selectedSite : new OptimizerSite(s.Id, s.Name, await repo.GetSeries(s.Id)));
            Progress = 0.15 + 0.15 * sites.Count / state.AvailableSites.Count;
            Notify();
        }
        // A custom point gets its own row next to the 9 Artemis regions (for comparison).
        if (!state.AvailableSites.Any(s => s.Id == selectedSite.Id)) sites.Add(selectedSite);

        // Re-render at most ~50 times per run: every notification re-renders the panels.
        var lastBucket = -1;
        var progress = new ActionProgress(p =>
        {
            Progress = 0.3 + 0.7 * p;
            var bucket = (int)(Progress * 50);
            if (bucket != lastBucket) { lastBucket = bucket; Notify(); }
        });
        return await WindowOptimizer.RunAsync(sites, lander, state.WindowStart, state.WindowEnd, duration, Thresholds, progress, ct);
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

    /// <summary>Cap for <see cref="MaxSurvivableDays"/> (about one lunar day plus the following night).</summary>
    public const double MaxSurvivableCapDays = 30;

    /// <summary>Longest stay the selected landing survives (each length with its own AUTO heading).</summary>
    public double? MaxSurvivableDays { get; private set; }

    /// <summary>Verdict at loads ±20% and the break-even load for the shown mission (null before the first run).</summary>
    public LoadSensitivity? LoadSensitivity { get; private set; }

    /// <summary>The shown mission re-run over the landing ellipse (null until loaded, or without ellipse data / accuracy).</summary>
    public EllipseResult? Ellipse { get; private set; }

    /// <summary>Landing accuracy of the selected lander (m) and its parameter (provenance shown in the row).</summary>
    public NumberParam? EllipseAccuracy { get; private set; }

    /// <summary>The shown mission with the panels at 1 m and 3 m instead of 2 m (null until loaded, or without heights data).</summary>
    public PanelHeightSensitivity? PanelHeight { get; private set; }

    private void SimulateSite(OptimizerSite site, DateTime landingUtc, LanderInputs lander)
    {
        Mission = MissionSimulator.Run(site.Series, lander, landingUtc, state.MissionDurationDays, Thresholds);
        MissionVersion++;
        Ellipse = null;
        PanelHeight = null;
        EllipseAccuracy = state.SelectedLander?.LandingAccuracyM;
        if (EllipseAccuracy is { Value: > 0 } accuracy)
            _ = UpdateEllipseAsync(MissionVersion, site, Mission, lander, Thresholds, accuracy.Value);
        _ = UpdatePanelHeightAsync(MissionVersion, site, Mission, lander, Thresholds);
        // Same heading as the shown mission, so "max stay" and the verdict describe the same landing.
        MaxSurvivableDays = MissionSimulator.MaxSurvivableDays(site.Series, lander with { HeadingDeg = Mission.HeadingDeg ?? lander.HeadingDeg },
            landingUtc, Thresholds, MaxSurvivableCapDays);
        LoadSensitivity = SensitivityAnalysis.ForLoad(site.Series, lander, Mission, Thresholds);
        MissionSiteId = site.Id;
        MissionSiteName = site.Name;
        ThresholdBatteryPct = state.MinBatteryPercent;
    }

    /// <summary>Load the site's ellipse grid (once) and re-run the mission over it; dropped if a newer mission replaced it.</summary>
    private async Task UpdateEllipseAsync(int version, OptimizerSite site, SimulationResult mission, LanderInputs lander,
                                          MissionThresholds thresholds, double radiusM)
    {
        try
        {
            var data = await repo.GetEllipse(site.Id);
            if (data is null || version != MissionVersion) return;
            await Task.Yield();          // let the verdict render first; the re-run takes a few ms
            if (version != MissionVersion) return;
            Ellipse = LandingEllipse.Analyze(site.Series, data, lander, mission, thresholds, radiusM);
            Notify();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Analysis] landing ellipse: {ex.Message}");
        }
    }

    /// <summary>Load the site's per-height horizons (once) and re-run the mission at 1 m and 3 m; dropped if a newer mission replaced it.</summary>
    private async Task UpdatePanelHeightAsync(int version, OptimizerSite site, SimulationResult mission, LanderInputs lander,
                                              MissionThresholds thresholds)
    {
        try
        {
            var data = await repo.GetHorizonHeights(site.Id);
            if (data is null || version != MissionVersion) return;
            await Task.Yield();
            if (version != MissionVersion) return;
            PanelHeight = PanelHeightAnalysis.For(site.Series, data, lander, mission, thresholds);
            Notify();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Analysis] panel height: {ex.Message}");
        }
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

    /// <summary>Expand the results strip now (e.g. right after the landing animation touches down).</summary>
    public void RequestExpand()
    {
        ExpandToken++;
        Notify();
    }

    private void Notify() => OnChanged?.Invoke();

    /// <summary>Reports synchronously; Progress&lt;T&gt; would post to the sync context and lag behind the work.</summary>
    private sealed class ActionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
