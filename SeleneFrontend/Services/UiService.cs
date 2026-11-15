using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

public enum BootStatus { Pending, Ok, Failed }

public sealed record BootLine(string Text, BootStatus Status, string Detail = "");

/// <summary>What the 3D viewer needs to play the landing animation.</summary>
/// <param name="Panels">Fixed solar facets to draw (null for ideal tracking / what-if IDEAL).</param>
/// <param name="MissionVersion"><see cref="AnalysisService.MissionVersion"/> before this landing's analysis started:
/// only missions newer than that may set the lander's heading.</param>
public sealed record LandingRequest(string SiteId, double Lat, double Lon, string SiteName, string LanderId, string LanderClass, string LanderName,
    bool ReducedMotion, LanderModelSpec? Model, IReadOnlyList<PanelFacet>? Panels, int MissionVersion);

/// <summary>
/// First impression and presentation state: boot sequence, welcome card, recommended
/// scenario, QUICK DEMO, help dialog, compass time, candidate overlay, compare selection.
/// </summary>
public sealed class UiService(IJSRuntime js, MissionConfigState state, IlluminationRepository repo, AnalysisService analysis, IConfiguration config)
{
    private const string BootSeenKey = "selene.bootSeen.v1";
    private const string LandingAnimKey = "selene.landingAnimation.v1";
    private const string WelcomeDismissedKey = "selene.welcomeDismissed.v1";
    private const string AnalystModeKey = "selene.analystMode.v1";
    private const string EnhanceKey = "selene.enhanceView.v1";
    public const string RecommendedLanderId = "nova_c";
    public const double RecommendedMissionDays = 7;
    public static readonly TimeSpan BootMax = TimeSpan.FromMilliseconds(1500);   // Moon + welcome card within 3 s of opening the link (all 3 engines)

    public event Action? OnChanged;

    /// <summary>Timeline hover moved (only the compass listens; avoids re-rendering every panel).</summary>
    public event Action? HoverTimeChanged;

    /// <summary>The 3D viewer should start (or skip to the end of) its intro flight.</summary>
    public event Action? IntroRequested;

    /// <summary>The 3D viewer should play the landing animation (MoonPanel answers with NotifyLandingFinished).</summary>
    public event Action<LandingRequest>? LandingRequested;

    /// <summary>NEW LANDING: the viewer removes the landed model and unlocks site picking.</summary>
    public event Action? NewLandingRequested;

    // ------------------------------------------------------------------ presentation mode

    /// <summary>SIMPLE (default) shows the Moon, the three steps and one verdict card; ANALYST adds every detail.</summary>
    public bool AnalystMode { get; private set; }

    /// <summary>Visual-only brightening of the polar scene (ambient + exposure); the Sun's direction is unchanged.</summary>
    public bool EnhanceView { get; private set; } = true;

    /// <summary>A lander stands on the surface: site picking on the Moon is locked until NEW LANDING.</summary>
    public bool Landed { get; private set; }
    private string? _landedLanderId;

    /// <summary>The user (or QUICK DEMO) asked for a result; until then SIMPLE shows the welcome card, not a verdict.</summary>
    public bool VerdictRequested { get; private set; }
    public bool VerdictDismissed { get; private set; }

    /// <summary>Shown once after QUICK DEMO in SIMPLE mode: "See the full analysis →".</summary>
    public bool FullAnalysisHint { get; private set; }

    public bool CreditsOpen { get; private set; }

    /// <summary>Step the setup panel should open (1 site, 2 lander, 3 run); bumped by NEW LANDING and the steps themselves.</summary>
    public int OpenStep { get; private set; } = 3;
    public int OpenStepRevision { get; private set; }

    /// <summary>Feature flag <c>features:landingAnimation</c> (appsettings.json), overridable in the HUD settings.</summary>
    public bool LandingAnimationEnabled { get; private set; } = true;
    public bool LandingInProgress { get; private set; }
    public string? ContactText { get; private set; }
    private TaskCompletionSource<string>? _landingDone;

    private IJSObjectReference? _ui;

    public bool ReducedMotion { get; private set; }
    public bool BootVisible { get; private set; }
    public List<BootLine> BootLines { get; } = [];
    public bool WelcomeVisible { get; private set; }
    public bool HelpOpen { get; private set; }
    public bool CandidatesOn { get; private set; }
    public bool IntroDone { get; private set; }

    /// <summary>"2D MOSAIC MODE" / "3D TERRAIN" once the viewer is up.</summary>
    public string? ViewMode { get; private set; }
    public bool ViewerReady { get; private set; }

    /// <summary>Time shown by the Sun/Earth compass (timeline hover), otherwise the mission landing time.</summary>
    public DateTime? HoverTimeUtc { get; private set; }

    public string? RecommendedSiteId { get; private set; }

    public List<string> CompareSiteIds { get; } = [];
    public string ResultsTab { get; private set; } = "mission";

    private readonly TaskCompletionSource _viewerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _tiles = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    /// <summary>Waits for the deferred CesiumJS script; false if it could not be loaded.</summary>
    public async Task<bool> WhenCesiumReadyAsync() => await (await Ui()).InvokeAsync<bool>("whenCesiumReady", 30000);

    private async Task<IJSObjectReference> Ui() => _ui ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ui.js");

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Loads the catalog, shows the boot lines as the real fetches finish (first visit only),
    /// pre-selects the recommended scenario and runs it, so the first screen is never empty.
    /// </summary>
    public async Task StartupAsync()
    {
        if (_started) return;
        _started = true;

        var ui = await Ui();
        ReducedMotion = await ui.InvokeAsync<bool>("prefersReducedMotion");
        LandingAnimationEnabled = !bool.TryParse(config["features:landingAnimation"], out var flag) || flag;
        MissionReplayEnabled = !bool.TryParse(config["features:missionReplay"], out var replayFlag) || replayFlag;
        if (LandingAnimationEnabled && await ui.InvokeAsync<string?>("getItem", LandingAnimKey) == "0") LandingAnimationEnabled = false;
        BootVisible = await ui.InvokeAsync<string?>("getItem", BootSeenKey) is null;
        AnalystMode = await ui.InvokeAsync<string?>("getItem", AnalystModeKey) == "1";
        EnhanceView = await ui.InvokeAsync<string?>("getItem", EnhanceKey) != "0";
        state.OnStateChanged += OnSelectionChanged;
        WelcomeVisible = await ui.InvokeAsync<string?>("getItem", WelcomeDismissedKey) is null;
        if (BootVisible) await ui.InvokeVoidAsync("setItem", BootSeenKey, "1");
        Notify();

        var bootClock = System.Diagnostics.Stopwatch.StartNew();
        var bootTimeout = Task.Delay(BootMax);

        // The loading work runs on its own; the boot screen closes when it is done or at BootMax,
        // whichever comes first, so the Moon and the welcome card are up within ~3 s of opening the link.
        Task? analysisTask = null;
        var work = LoadAndPrepareAsync(ui, bootTimeout, t => analysisTask = t);
        await Task.WhenAny(work, bootTimeout);

        if (BootVisible && work.IsCompleted)
        {
            var remaining = BootMax - bootClock.Elapsed;
            var linger = TimeSpan.FromMilliseconds(ReducedMotion ? 150 : 350);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining < linger ? remaining : linger);
        }
        CloseBoot();
        await work;
        if (analysisTask is not null) await analysisTask;
    }

    private async Task LoadAndPrepareAsync(IJSObjectReference ui, Task bootTimeout, Action<Task> analysisStarted)
    {
        // 1) Catalog
        var catalog = AddLine("LOADING SITE & LANDER CATALOG");
        await state.LoadData();
        SetLine(catalog, state.LoadError is null ? BootStatus.Ok : BootStatus.Failed,
            state.LoadError is null ? $"{state.AvailableSites.Count} ARTEMIS REGIONS · {state.AllLanders.Count} LANDERS" : "");

        // 2) Recommended scenario + its precomputed series (LOLA horizons + JPL DE421 tracks live in that file)
        var terrain = AddLine("LOADING LOLA TERRAIN HORIZONS");
        var ephem = AddLine("LOADING JPL EPHEMERIS (DE421, PRECOMPUTED)");
        try
        {
            RecommendedSiteId = await PickRecommendedSiteAsync();
            if (RecommendedSiteId is not null) await repo.GetSeries(RecommendedSiteId);
            SetLine(terrain, RecommendedSiteId is null ? BootStatus.Failed : BootStatus.Ok);
            SetLine(ephem, RecommendedSiteId is null ? BootStatus.Failed : BootStatus.Ok);

            // Pre-select the recommended scenario and compute it in the background (no camera fly —
            // the intro flight handles the camera). The verdict card shows only when the user asks.
            if (RecommendedSiteId is not null && state.SelectedLander is null)
            {
                await ApplyRecommendedScenarioAsync(fly: false);
                analysisStarted(analysis.RunAsync(expand: false));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Startup] illumination data: {ex.Message}");
            SetLine(terrain, BootStatus.Failed);
            SetLine(ephem, BootStatus.Failed);
        }

        // 3) Imagery: wait for the viewer and its first tiles (bounded by the boot budget).
        //    Returning visitors see no boot screen, so they do not wait for tiles at all.
        if (BootVisible)
        {
            var mosaic = AddLine("LOADING LRO WAC MOSAIC");
            var tilesTask = WaitViewerAndTilesAsync();
            var done = await Task.WhenAny(tilesTask, bootTimeout);
            SetLine(mosaic, done == tilesTask && tilesTask.Result ? BootStatus.Ok : BootStatus.Pending,
                done == tilesTask ? "" : "STREAMING");
        }
    }

    private async Task<bool> WaitViewerAndTilesAsync()
    {
        await _viewerReady.Task;
        return await _tiles.Task;
    }

    public void SkipBoot() => CloseBoot();

    private void CloseBoot()
    {
        var wasOpen = BootVisible;
        BootVisible = false;
        if (wasOpen || !IntroDone) RequestIntro();
        Notify();
    }

    private void RequestIntro()
    {
        if (IntroDone) return;
        IntroDone = true;
        IntroRequested?.Invoke();
    }

    /// <summary>Called by the viewer once Cesium is up.</summary>
    public void SetViewerReady(bool terrain3D)
    {
        ViewMode = terrain3D ? "3D TERRAIN" : "2D MOSAIC MODE";
        ViewerReady = true;
        _viewerReady.TrySetResult();
        // Returning visitors have no boot screen: start the intro as soon as the viewer exists.
        if (!BootVisible && _started) RequestIntro();
        Notify();
    }

    public void SetTilesLoaded(bool ok) => _tiles.TrySetResult(ok);

    private int AddLine(string text)
    {
        BootLines.Add(new BootLine(text, BootStatus.Pending));
        Notify();
        return BootLines.Count - 1;
    }

    private void SetLine(int i, BootStatus status, string detail = "")
    {
        BootLines[i] = BootLines[i] with { Status = status, Detail = detail };
        Notify();
    }

    // ------------------------------------------------------------------ scenario

    /// <summary>Region with the highest share of sunlit hours in index.json (ties: more Earth visibility).</summary>
    public async Task<string?> PickRecommendedSiteAsync()
    {
        var index = await repo.GetIndexAsync();
        return index.Sites
            .Where(s => state.AvailableSites.Any(a => a.Id == s.SiteId))
            .OrderByDescending(s => s.PctSunlit)
            .ThenByDescending(s => s.PctComms)
            .Select(s => s.SiteId)
            .FirstOrDefault();
    }

    private async Task ApplyRecommendedScenarioAsync(bool fly)
    {
        if (RecommendedSiteId is null) return;
        if (state.SelectedLanderClass != "clps") await state.SelectLanderClass("clps");
        await state.SelectSite(RecommendedSiteId, fly: fly);
        await state.SelectLander(RecommendedLanderId);
        // A 7-day surface stay (IM-1 operated ~7 days). With fixed panels the Sun leaves the panels' view after
        // ~7–8 days at the pole for Blue Ghost and Griffin; Nova-C (494 W, derived from the FCC filing since
        // 2026-11-15) also manages its 14-day design life in the best window of every region.
        await state.UpdateMissionDuration(RecommendedMissionDays);
        state.ResetWhatIf();
    }

    /// <summary>
    /// ▶ QUICK DEMO: a real decision, not "always 100 %" — Blue Ghost at Nobile Rim 1 on a date where the mission works
    /// but one 11 h darkness drains the battery to ~41 % (<see cref="DemoScenario"/>) → landing → verdict → replay.
    /// The summary then offers the counter-example (same site 4 weeks later: MISSION LOST) and the best landing window.
    /// </summary>
    public async Task QuickDemoAsync()
    {
        DismissWelcome();
        state.Reset();
        if (!await ApplyDemoScenarioAsync())
        {
            // Demo data missing: fall back to the recommended (best-lit) scenario and its best window.
            RecommendedSiteId ??= await PickRecommendedSiteAsync();
            if (RecommendedSiteId is null) return;
            await ApplyRecommendedScenarioAsync(fly: false);   // the landing (or its fallback fly-to) moves the camera
        }
        ResultsTab = "mission";
        Notify();
        // Clicked right after opening the page: wait for the 3D view (≤ 15 s) so the demo shows the landing
        // and the mission replay instead of the fly-to fallback.
        if (LandingAnimationEnabled && !ViewerReady) await Task.WhenAny(_viewerReady.Task, Task.Delay(15000));
        await ConfirmLandingAsync(highlightBest: true);
        if (!AnalystMode && VerdictRequested) { FullAnalysisHint = true; Notify(); }
        // The demo ends with the mission played back (can be skipped from its HUD), after the verdict
        // has been on screen for a moment. Anything the user does meanwhile (new landing, another
        // lander, Play mission) wins.
        if (Landed && CanPlayMission)
        {
            await Task.Delay(ReducedMotion ? 0 : 2500);
            if (Landed && CanPlayMission && !ReplayPlaying && ReplayOutcome is null) await PlayMissionAsync();
        }
    }

    private async Task<bool> ApplyDemoScenarioAsync()
    {
        if (!state.AvailableSites.Any(s => s.Id == DemoScenario.SiteId) || !state.AllLanders.Any(l => l.Id == DemoScenario.LanderId)) return false;
        if (state.SelectedLanderClass != "clps") await state.SelectLanderClass("clps");
        await state.SelectSite(DemoScenario.SiteId, fly: false);
        await state.SelectLander(DemoScenario.LanderId);
        await state.UpdateMissionDuration(DemoScenario.MissionDays);
        await state.SetWindow(DemoScenario.WindowStart, DemoScenario.WindowEnd);
        state.ResetWhatIf();
        analysis.PinLanding(DemoScenario.LandingUtc);
        return true;
    }

    /// <summary>The shown result is the QUICK DEMO landing (its summary offers the counter-example).</summary>
    public bool IsDemoResult => IsPinnedResult(DemoScenario.LandingUtc);

    /// <summary>The shown result is the counter-example.</summary>
    public bool IsCounterResult => IsPinnedResult(DemoScenario.CounterLandingUtc);

    private bool IsPinnedResult(DateTime utc) =>
        analysis.PinnedLandingUtc == utc && analysis.Mission?.LandingUtc == utc && analysis.MissionSiteId == DemoScenario.SiteId
        && !analysis.IsOutdated;

    /// <summary>"The same site 4 weeks later would fail →": recompute for that date and play it (the lander stays landed).</summary>
    public async Task ShowCounterExampleAsync()
    {
        CloseReplay();
        analysis.PinLanding(DemoScenario.CounterLandingUtc);
        await analysis.RunAsync(expand: false);
        if (Landed && CanPlayMission) await PlayMissionAsync();
    }

    /// <summary>"Show the best landing window": drop the pinned date — the optimizer picks the day again.</summary>
    public async Task ShowBestWindowAsync()
    {
        CloseReplay();
        analysis.PinLanding(null);
        await analysis.RunAsync(expand: false, highlightBest: true);
        Notify();
    }

    // ------------------------------------------------------------------ mission replay

    /// <summary>Feature flag <c>features:missionReplay</c> (appsettings.json).</summary>
    public bool MissionReplayEnabled { get; private set; } = true;

    public bool ReplayPlaying { get; private set; }

    /// <summary>"lost", "complete-clps", "complete-hls" after a replay (or the static summary); null = none shown.</summary>
    public string? ReplayOutcome { get; private set; }

    /// <summary>The summary card is non-modal: HUD use, camera moves or Esc fold it into a small "Mission summary" button.</summary>
    public bool SummaryCollapsed { get; private set; }

    /// <summary>The full summary card is on screen (not folded).</summary>
    public bool SummaryOpen => ReplayOutcome is not null && !SummaryCollapsed;

    public void CollapseSummary()
    {
        if (!SummaryOpen) return;
        SummaryCollapsed = true;
        Notify();
    }

    public void ExpandSummary()
    {
        if (ReplayOutcome is null || !SummaryCollapsed) return;
        SummaryCollapsed = false;
        Notify();
    }

    /// <summary>
    /// Steps 1–2 show ✓ only after the user picked or confirmed the site / lander (or landed); the start-up
    /// pre-selection is shown as "suggested".
    /// </summary>
    public bool SiteConfirmed { get; private set; }
    public bool LanderConfirmed { get; private set; }

    public void ConfirmSite() { if (SiteConfirmed) return; SiteConfirmed = true; Notify(); }
    public void ConfirmLander() { if (LanderConfirmed) return; LanderConfirmed = true; Notify(); }

    /// <summary>A site row under the mouse / focus in the panel (the viewer lights up its pin); null clears.</summary>
    public event Action<string?>? SiteHoverChanged;
    public void HoverSite(string? siteId) => SiteHoverChanged?.Invoke(siteId);

    /// <summary>"Change lander" from the Result step: open step 2 (picking another lander ends the landing).</summary>
    public void ChangeLander()
    {
        CloseReplay();
        SetOpenStep(2);
        Notify();
    }

    /// <summary>Mission step under the replay cursor (−1 = no replay); separate event so only the timeline re-renders.</summary>
    public int ReplayStep { get; private set; } = -1;
    public event Action? ReplayStepChanged;

    /// <summary>Mission time under the replay cursor (null = no replay): the Sun/Earth compass follows it.</summary>
    public DateTime? ReplayTimeUtc => ReplayStep >= 0 && analysis.Mission is { } m ? m.LandingUtc.AddHours(ReplayStep * m.StepHours) : null;

    /// <summary>The viewer should play (true) or stop and reset (false) the mission replay.</summary>
    public event Action<bool>? ReplayRequested;

    public bool CanPlayMission =>
        MissionReplayEnabled && Landed && !LandingInProgress && analysis.Mission is not null && !analysis.IsOutdated
        && analysis.MissionSiteId == state.SelectedSite?.Id;

    /// <summary>▶ Play mission: the result replayed in ~25 s; with reduced motion only the summary card is shown.</summary>
    public async Task PlayMissionAsync()
    {
        if (!CanPlayMission) return;
        ReplayOutcome = null;
        SummaryCollapsed = false;
        if (ReducedMotion)
        {
            ReplayOutcome = OutcomeOf();
            Notify();
            return;
        }
        ReplayPlaying = true;
        Notify();
        // The button may sit in the results panel below the Moon (Analyst): bring the lander into view first.
        try { await (await Ui()).InvokeVoidAsync("revealMoon"); }
        catch (JSDisconnectedException) { }
        ReplayRequested?.Invoke(true);
    }

    /// <summary>How the replay ends for the current result and lander class.</summary>
    public string OutcomeOf() =>
        analysis.Mission is { DepletedAtStep: not null } ? "lost"
        : state.SelectedLander?.Class == "hls" ? "complete-hls" : "complete-clps";

    public void SetReplayStep(int step)
    {
        if (ReplayStep == step) return;
        ReplayStep = step;
        ReplayStepChanged?.Invoke();
    }

    /// <summary>Called by the viewer when the replay reaches its end (or is skipped).</summary>
    public void ReplayEnded(string outcome)
    {
        ReplayPlaying = false;
        ReplayOutcome = outcome;
        SummaryCollapsed = false;
        Notify();
    }

    /// <summary>Close the summary (and stop a running replay): the scene goes back to the landing moment.</summary>
    public void CloseReplay()
    {
        var wasActive = ReplayPlaying || ReplayOutcome is not null;
        ReplayPlaying = false;
        ReplayOutcome = null;
        SummaryCollapsed = false;
        SetReplayStep(-1);
        if (wasActive) ReplayRequested?.Invoke(false);
        Notify();
    }

    /// <summary>"Try another date": Analyst mode, scrolled to the landing-window heatmap.</summary>
    public async Task ShowHeatmapAsync()
    {
        CloseReplay();
        SetAnalystMode(true);
        ResultsTab = "mission";
        analysis.RequestExpand();
        Notify();
        await Task.Delay(80);
        await ScrollToAsync("results-heatmap");
    }

    // ------------------------------------------------------------------ landing

    public bool CanConfirmLanding =>
        !LandingInProgress && state.SelectedLander is not null
        && analysis.CanRun;   // one of the 9 regions, or a checked custom point in 80–90°S

    /// <summary>
    /// CONFIRM LANDING SITE: the analysis starts immediately and runs in parallel with the landing
    /// animation; the results open right after touchdown. With the feature off (or no 3D view)
    /// the camera simply flies to the site.
    /// </summary>
    public async Task ConfirmLandingAsync(bool highlightBest = false)
    {
        if (!CanConfirmLanding) return;
        SiteConfirmed = LanderConfirmed = true;   // landing there confirms both choices
        var site = state.SelectedSite!;
        var lander = state.SelectedLander!;
        ContactText = null;
        VerdictRequested = true;
        VerdictDismissed = false;
        FullAnalysisHint = false;
        var missionVersion = analysis.MissionVersion;
        var panels = state.HasFixedPanels && !state.IdealTracking ? lander.Panels!.Facets : null;
        var analysisTask = analysis.RunAsync(expand: false, highlightBest: highlightBest);

        if (LandingAnimationEnabled && ViewerReady && LandingRequested is not null)
        {
            LandingInProgress = true;
            _landingDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Notify();
            LandingRequested.Invoke(new LandingRequest(site.Id, site.Lat, site.Lon, site.Name, lander.Id, lander.Class, lander.Name,
                ReducedMotion, lander.Model, panels, missionVersion));
            var status = await _landingDone.Task;
            LandingInProgress = false;
            if (status == "cancelled") { Notify(); return; }   // site or lander changed mid-descent
            if (status is "landed" or "skipped" or "reduced")
            {
                Landed = true;
                _landedLanderId = lander.Id;
                OpenStep = 3;
            }
        }
        else
        {
            state.RequestFlyTo();
        }

        // Results right after touchdown: wait for the quick phase of the analysis if it is not done yet.
        while (!analysisTask.IsCompleted && analysis.MissionSiteId != site.Id)
        {
            await Task.WhenAny(analysisTask, Task.Delay(50));
        }
        var date = analysis.MissionSiteId == site.Id && analysis.Mission is { } m ? m.LandingUtc.ToString("yyyy-MM-dd") : "date pending";
        ContactText = $"CONTACT CONFIRMED · {site.Name} · {date}";
        analysis.RequestExpand();
        Notify();
        await analysisTask;
    }

    /// <summary>Called by the viewer when the animation ends: landed, skipped, reduced, cancelled or unavailable.</summary>
    public void NotifyLandingFinished(string status) => _landingDone?.TrySetResult(status);

    public void ClearContact()
    {
        if (ContactText is null) return;
        ContactText = null;
        Notify();
    }

    public void SetLandingAnimation(bool enabled)
    {
        LandingAnimationEnabled = enabled;
        _ = PersistValueAsync(LandingAnimKey, enabled ? "1" : "0");
        Notify();
    }

    private async Task PersistValueAsync(string key, string value)
    {
        try { await (await Ui()).InvokeVoidAsync("setItem", key, value); }
        catch (JSDisconnectedException) { }
    }

    // ------------------------------------------------------------------ modes and the landed state

    public void SetAnalystMode(bool on)
    {
        if (AnalystMode == on) return;
        AnalystMode = on;
        if (on) FullAnalysisHint = false;
        _ = PersistValueAsync(AnalystModeKey, on ? "1" : "0");
        Notify();
    }

    public void SetEnhanceView(bool on)
    {
        EnhanceView = on;
        _ = PersistValueAsync(EnhanceKey, on ? "1" : "0");
        Notify();
    }

    /// <summary>"See the full analysis" / "Show on timeline": ANALYST mode with the results panel open at the timeline.</summary>
    public async Task ShowFullAnalysisAsync()
    {
        SetAnalystMode(true);
        ResultsTab = "mission";
        analysis.RequestExpand();
        Notify();
        await Task.Delay(60);   // let the results panel render before scrolling to it
        await ScrollToAsync("results-timeline");
    }

    /// <summary>NEW LANDING: remove the landed model, unlock site picking and go back to step 1.</summary>
    public void NewLanding()
    {
        CloseReplay();
        analysis.PinLanding(null);
        Landed = false;
        _landedLanderId = null;
        ContactText = null;
        VerdictDismissed = true;
        FullAnalysisHint = false;
        SetOpenStep(1);
        NewLandingRequested?.Invoke();
        Notify();
    }

    /// <summary>
    /// Changing the lander works like NEW LANDING (the model on the surface no longer matches). The verdict card
    /// stays, marked out of date, so the user sees why the main button now says "Update results".
    /// </summary>
    private void OnSelectionChanged()
    {
        if (Landed && !LandingInProgress && state.SelectedLander?.Id != _landedLanderId)
        {
            Landed = false;
            _landedLanderId = null;
            CloseReplay();
            Notify();
        }
    }

    // ------------------------------------------------------------------ why an action is unavailable

    /// <summary>One missing piece before "Land &amp; analyze" can run; Step 1/2 links to that step, 0 = no link.</summary>
    public sealed record Blocker(int Step, string Text, bool NearestRegion = false);

    public static bool IsCustom(Site? s) => s is not null && s.Id.StartsWith("CUSTOM", StringComparison.Ordinal);

    /// <summary>Everything that keeps the main button disabled, in plain language (empty = it can run).</summary>
    public IReadOnlyList<Blocker> LandBlockers()
    {
        var list = new List<Blocker>();
        if (!state.IsLoaded) { list.Add(new(0, "Loading the site and lander data…")); return list; }
        if (LandingInProgress) { list.Add(new(0, "Landing in progress — wait for touchdown")); return list; }
        var site = state.SelectedSite;
        if (site is null) list.Add(new(1, "Choose one of the 9 landing regions"));
        else if (site is { IsCustom: true, CustomUnavailable: { } why }) list.Add(new(1, why, NearestRegion: true));
        else if (site is { IsCustom: true, CustomChecked: false }) list.Add(new(0, "Checking the terrain at this point…"));
        else if (!site.IsCustom && !state.AvailableSites.Any(a => a.Id == site.Id)) list.Add(new(1, "Choose one of the 9 landing regions"));
        if (state.SelectedLander is null) list.Add(new(2, "Choose a lander"));
        return list;
    }

    /// <summary>The closest of the 9 regions to a point (great-circle distance on the Moon).</summary>
    public Site? NearestRegion(Site? point)
    {
        if (point is null || state.AvailableSites.Count == 0) return null;
        static double Rad(double d) => d * Math.PI / 180;
        double Dist(Site s)
        {
            var dLat = Rad(s.Lat - point.Lat);
            var dLon = Rad(s.Lon - point.Lon);
            var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(point.Lat)) * Math.Cos(Rad(s.Lat)) * Math.Pow(Math.Sin(dLon / 2), 2);
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
        return state.AvailableSites.OrderBy(Dist).First();
    }

    /// <summary>"Nearest region": replace a custom point with the closest analysable region.</summary>
    public async Task SelectNearestRegionAsync()
    {
        if (NearestRegion(state.SelectedSite) is { } near) await state.SelectSite(near.Id);
    }

    public void SetOpenStep(int step)
    {
        OpenStep = step;
        OpenStepRevision++;
        Notify();
    }

    public void DismissFullAnalysisHint() { FullAnalysisHint = false; Notify(); }

    /// <summary>RUN ANALYSIS without a landing (Analyst mode): the verdict card shows the new result.</summary>
    public Task RunAnalysisAsync()
    {
        VerdictRequested = true;
        VerdictDismissed = false;
        Notify();
        return analysis.RunAsync();
    }

    public void SetCredits(bool open) { CreditsOpen = open; Notify(); }

    /// <summary>"?" in the top bar: show the welcome card again.</summary>
    public void ShowWelcome() { WelcomeVisible = true; Notify(); }

    // ------------------------------------------------------------------ small UI state

    public void DismissWelcome()
    {
        if (!WelcomeVisible) return;
        WelcomeVisible = false;
        _ = PersistAsync(WelcomeDismissedKey);
        Notify();
    }

    private async Task PersistAsync(string key)
    {
        try { await (await Ui()).InvokeVoidAsync("setItem", key, "1"); }
        catch (JSDisconnectedException) { }
    }

    public void SetHelp(bool open) { HelpOpen = open; Notify(); }

    public void ToggleCandidates() { CandidatesOn = !CandidatesOn; Notify(); }

    public void SetHoverTime(DateTime? utc)
    {
        if (HoverTimeUtc == utc) return;
        HoverTimeUtc = utc;
        HoverTimeChanged?.Invoke();
    }

    public void SetResultsTab(string tab)
    {
        ResultsTab = tab;
        if (tab == "compare" && CompareSiteIds.Count == 0) CompareSiteIds.AddRange(analysis.TopSites(3));
        Notify();
    }

    /// <summary>Toggle a site in the comparison (max 3; adding a 4th drops the oldest).</summary>
    public void ToggleCompare(string siteId)
    {
        if (!CompareSiteIds.Remove(siteId))
        {
            if (CompareSiteIds.Count >= 3) CompareSiteIds.RemoveAt(0);
            CompareSiteIds.Add(siteId);
        }
        Notify();
    }

    public async Task ScrollToAsync(string id)
    {
        try { await (await Ui()).InvokeVoidAsync("scrollToId", id); }
        catch (JSDisconnectedException) { }
    }

    public async Task CountUpAsync(Microsoft.AspNetCore.Components.ElementReference el, double to, int decimals)
    {
        try { await (await Ui()).InvokeVoidAsync("countUp", el, to, decimals, ReducedMotion ? 0 : 700); }
        catch (JSDisconnectedException) { }
    }

    private void Notify() => OnChanged?.Invoke();
}
