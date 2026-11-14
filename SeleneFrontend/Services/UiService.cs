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
    bool ReducedMotion, string? ModelUri, double? ModelHeightM, IReadOnlyList<PanelFacet>? Panels, int MissionVersion);

/// <summary>
/// First impression and presentation state: boot sequence, welcome card, recommended
/// scenario, QUICK DEMO, help dialog, compass time, candidate overlay, compare selection.
/// </summary>
public sealed class UiService(IJSRuntime js, MissionConfigState state, IlluminationRepository repo, AnalysisService analysis, IConfiguration config)
{
    private const string BootSeenKey = "selene.bootSeen.v1";
    private const string LandingAnimKey = "selene.landingAnimation.v1";
    private const string WelcomeDismissedKey = "selene.welcomeDismissed.v1";
    public const string RecommendedLanderId = "nova_c";
    public const double RecommendedMissionDays = 7;
    public static readonly TimeSpan BootMax = TimeSpan.FromMilliseconds(2500);

    public event Action? OnChanged;

    /// <summary>Timeline hover moved (only the compass listens; avoids re-rendering every panel).</summary>
    public event Action? HoverTimeChanged;

    /// <summary>The 3D viewer should start (or skip to the end of) its intro flight.</summary>
    public event Action? IntroRequested;

    /// <summary>The 3D viewer should play the landing animation (MoonPanel answers with NotifyLandingFinished).</summary>
    public event Action<LandingRequest>? LandingRequested;

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
        if (LandingAnimationEnabled && await ui.InvokeAsync<string?>("getItem", LandingAnimKey) == "0") LandingAnimationEnabled = false;
        BootVisible = await ui.InvokeAsync<string?>("getItem", BootSeenKey) is null;
        WelcomeVisible = await ui.InvokeAsync<string?>("getItem", WelcomeDismissedKey) is null;
        if (BootVisible) await ui.InvokeVoidAsync("setItem", BootSeenKey, "1");
        Notify();

        var bootClock = System.Diagnostics.Stopwatch.StartNew();
        var bootTimeout = Task.Delay(BootMax);

        // 1) Catalog
        var catalog = AddLine("LOADING SITE & LANDER CATALOG");
        await state.LoadData();
        SetLine(catalog, state.LoadError is null ? BootStatus.Ok : BootStatus.Failed,
            state.LoadError is null ? $"{state.AvailableSites.Count} ARTEMIS REGIONS · {state.AllLanders.Count} LANDERS" : "");

        // 2) Recommended scenario + its precomputed series (LOLA horizons + JPL DE421 tracks live in that file)
        var terrain = AddLine("LOADING LOLA TERRAIN HORIZONS");
        var ephem = AddLine("LOADING JPL EPHEMERIS (DE421, PRECOMPUTED)");
        Task? analysisTask = null;
        try
        {
            RecommendedSiteId = await PickRecommendedSiteAsync();
            if (RecommendedSiteId is not null) await repo.GetSeries(RecommendedSiteId);
            SetLine(terrain, RecommendedSiteId is null ? BootStatus.Failed : BootStatus.Ok);
            SetLine(ephem, RecommendedSiteId is null ? BootStatus.Failed : BootStatus.Ok);

            // Start the recommended analysis now, behind the boot screen, so the result is
            // ready when the screen closes (no camera fly — the intro flight handles the camera).
            if (RecommendedSiteId is not null && state.SelectedLander is null)
            {
                await ApplyRecommendedScenarioAsync(fly: false);
                var tall = await ui.InvokeAsync<int>("viewportHeight") >= 860;
                analysisTask = analysis.RunAsync(expand: tall);
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

        // Close the boot screen: shortly after the last line, never later than BootMax.
        if (BootVisible)
        {
            var remaining = BootMax - bootClock.Elapsed;
            var linger = TimeSpan.FromMilliseconds(ReducedMotion ? 150 : 350);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining < linger ? remaining : linger);
        }
        CloseBoot();
        if (analysisTask is not null) await analysisTask;
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
        // With fixed panels the Sun leaves the panels' view after ~7–8 days at the pole, so a 14-day
        // CLPS mission is never feasible there; the demo shows a feasible 7-day surface stay instead.
        await state.UpdateMissionDuration(RecommendedMissionDays);
        state.ResetWhatIf();
    }

    /// <summary>▶ QUICK DEMO: recommended scenario → landing (animation + analysis in parallel) → BEST WINDOW pulse.</summary>
    public async Task QuickDemoAsync()
    {
        DismissWelcome();
        RecommendedSiteId ??= await PickRecommendedSiteAsync();
        if (RecommendedSiteId is null) return;
        state.Reset();
        await ApplyRecommendedScenarioAsync(fly: false);   // the landing (or its fallback fly-to) moves the camera
        ResultsTab = "mission";
        Notify();
        await ConfirmLandingAsync(highlightBest: true);
    }

    // ------------------------------------------------------------------ landing

    public bool CanConfirmLanding =>
        !LandingInProgress && state.SelectedLander is not null
        && state.SelectedSite is { } s && state.AvailableSites.Any(a => a.Id == s.Id);

    /// <summary>
    /// CONFIRM LANDING SITE: the analysis starts immediately and runs in parallel with the landing
    /// animation; the results open right after touchdown. With the feature off (or no 3D view)
    /// the camera simply flies to the site.
    /// </summary>
    public async Task ConfirmLandingAsync(bool highlightBest = false)
    {
        if (!CanConfirmLanding) return;
        var site = state.SelectedSite!;
        var lander = state.SelectedLander!;
        ContactText = null;
        var missionVersion = analysis.MissionVersion;
        var panels = state.HasFixedPanels && !state.IdealTracking ? lander.Panels!.Facets : null;
        var analysisTask = analysis.RunAsync(expand: false, highlightBest: highlightBest);

        if (LandingAnimationEnabled && ViewerReady && LandingRequested is not null)
        {
            LandingInProgress = true;
            _landingDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Notify();
            LandingRequested.Invoke(new LandingRequest(site.Id, site.Lat, site.Lon, site.Name, lander.Id, lander.Class, lander.Name,
                ReducedMotion, lander.ModelUri, lander.ModelHeightM, panels, missionVersion));
            var status = await _landingDone.Task;
            LandingInProgress = false;
            if (status == "cancelled") { Notify(); return; }   // site or lander changed mid-descent
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
