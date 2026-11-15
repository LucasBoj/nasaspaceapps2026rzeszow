using System.Net.Http.Json;
using System.Text.Json;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// Analysis of any point in the south polar cap (80–90°S) computed in the browser: a horizon mask from the
/// 240 m LOLA DEM and hourly Sun/Earth geometry from the exported ephemeris, turned into an
/// <see cref="IlluminationSeries"/> with the pipeline's formulas (<see cref="PolarDem"/>, <see cref="CustomSiteMath"/>).
/// The repository asks this service for CUSTOM site ids, so the simulator, optimizer, timeline, heatmap and
/// replay work unchanged. Data is loaded on first use (first click on the terrain) and cached; the work is
/// done in slices so the UI keeps rendering, with progress in <see cref="Status"/>.
/// </summary>
public sealed class CustomSiteService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const int AzimuthSlice = 90;    // 4 slices: the browser paints between them
    private const int HourSlice = 8784;      // one year per slice

    private readonly HttpClient _http;
    private readonly MissionConfigState _state;
    private Task<PolarDem>? _dem;
    private Task<EphemIndex>? _ephemIndex;
    private readonly Dictionary<int, Task<MoonEphemerisYear>> _years = [];
    private readonly Dictionary<string, Task<double[]>> _horizons = [];
    private long _lastHorizonComputeMs;

    public CustomSiteService(HttpClient http, MissionConfigState state, IlluminationRepository repo)
    {
        _http = http;
        _state = state;
        repo.CustomProvider = BuildSeriesAsync;
    }

    /// <summary>Progress text while a custom site is being computed (null when idle).</summary>
    public string? Status { get; private set; }
    public double Progress { get; private set; }
    public event Action? OnChanged;

    /// <summary>Milliseconds of the last horizon mask and series (for the summary and tests).</summary>
    public (long HorizonMs, long SeriesMs) LastTimings { get; private set; }

    public Task<PolarDem> GetDemAsync() => _dem ??= LoadDemAsync();

    private async Task<PolarDem> LoadDemAsync()
    {
        Report("Loading 240 m terrain (once)…", 0.02);
        var meta = await _http.GetFromJsonAsync<DemMeta>("data/dem/south80_240m.json", Web)
                   ?? throw new InvalidOperationException("DEM metadata missing");
        var bytes = await _http.GetByteArrayAsync("data/dem/south80_240m.bin");
        await Task.Delay(1);
        var dem = PolarDem.FromBinary(meta, bytes);
        Report(null, 0);
        return dem;
    }

    /// <summary>Slope, height and coverage of a clicked point (loads the DEM on first use).</summary>
    public async Task<CustomPoint> DescribeAsync(double lat, double lon)
    {
        if (lat > PolarDem.CoverageLatDeg) return new CustomPoint(false, double.NaN, double.NaN);
        try
        {
            var dem = await GetDemAsync();
            var h = dem.HeightAt(lat, lon);
            return new CustomPoint(double.IsFinite(h), dem.SlopeAt(lat, lon), h);
        }
        catch (Exception ex)
        {
            _dem = null;   // allow a retry
            Console.Error.WriteLine($"[CustomSite] terrain unavailable: {ex.Message}");
            Report(null, 0);
            return new CustomPoint(false, double.NaN, double.NaN, "the terrain data could not be downloaded — check the connection");
        }
    }

    /// <summary>Repository hook: the series of a custom site for the given calendar years.</summary>
    public async Task<IlluminationSeries> BuildSeriesAsync(string siteId, int firstYear, int lastYear)
    {
        var site = _state.CustomSite is { } c && c.Id == siteId ? c
            : _state.SelectedSite is { } s && s.Id == siteId ? s
            : throw new InvalidOperationException($"unknown custom site {siteId}");
        try
        {
            var wall = System.Diagnostics.Stopwatch.StartNew();
            var horizon = await HorizonAsync(site);

            var index = await (_ephemIndex ??= _http.GetFromJsonAsync<EphemIndex>("data/ephem/index.json", Web)!)!;
            var years = Enumerable.Range(firstYear, lastYear - firstYear + 1)
                .Select(y => index.Years.FirstOrDefault(e => e.Year == y)).OfType<EphemYearMeta>().ToList();
            if (years.Count == 0) throw new InvalidOperationException("no ephemeris for the window");
            var eph = await Task.WhenAll(years.Select(LoadYear));
            var total = eph.Sum(e => e.Count);
            var series = CustomSiteMath.NewSeries(site.Id, site.Lat, site.Lon, horizon, eph[0].Start, total);
            var compute = new System.Diagnostics.Stopwatch();
            var at = 0;
            foreach (var y in eph)
            {
                for (var i = 0; i < y.Count; i += HourSlice)
                {
                    var n = Math.Min(HourSlice, y.Count - i);
                    compute.Start();
                    CustomSiteMath.Fill(series, y, i, at + i, at + i + n);
                    compute.Stop();
                    Report("Custom site: Sun and Earth hour by hour…", 0.5 + 0.5 * (at + i + n) / total);
                    await Task.Delay(1);   // let the browser paint
                }
                at += y.Count;
            }
            LastTimings = (_lastHorizonComputeMs, compute.ElapsedMilliseconds);
            Console.WriteLine($"[CustomSite] {site.Lat:F3}, {site.Lon:F3}: horizon {_lastHorizonComputeMs} ms compute, " +
                              $"series {total} h in {compute.ElapsedMilliseconds} ms compute; {wall.ElapsedMilliseconds} ms wall incl. downloads and UI frames");
            return series;
        }
        finally
        {
            Report(null, 0);
        }
    }

    /// <summary>Horizon mask (cached per point), computed in slices of 30 azimuths.</summary>
    private Task<double[]> HorizonAsync(Site site)
    {
        var key = $"{site.Lat:F6},{site.Lon:F6}";
        if (!_horizons.TryGetValue(key, out var task))
        {
            task = ComputeHorizonAsync(site);
            _horizons[key] = task;
        }
        return task;
    }

    private async Task<double[]> ComputeHorizonAsync(Site site)
    {
        var dem = await GetDemAsync();
        var profile = new double[PolarDem.Azimuths];
        var compute = new System.Diagnostics.Stopwatch();
        for (var a = 0; a < PolarDem.Azimuths; a += AzimuthSlice)
        {
            compute.Start();
            dem.HorizonProfile(site.Lat, site.Lon, profile, a, Math.Min(AzimuthSlice, PolarDem.Azimuths - a));
            compute.Stop();
            Report("Custom site: terrain horizon from LOLA 240 m…", 0.05 + 0.45 * (a + AzimuthSlice) / PolarDem.Azimuths);
            await Task.Delay(1);
        }
        _lastHorizonComputeMs = compute.ElapsedMilliseconds;
        return profile;
    }

    private Task<MoonEphemerisYear> LoadYear(EphemYearMeta y)
    {
        if (!_years.TryGetValue(y.Year, out var task))
        {
            task = Load();
            _years[y.Year] = task;
        }
        return task;
        async Task<MoonEphemerisYear> Load() =>
            MoonEphemerisYear.FromBinary(TimeUtil.AsUtc(y.Start), await _http.GetByteArrayAsync($"data/ephem/{y.File}"));
    }

    private void Report(string? status, double progress)
    {
        Status = status;
        Progress = progress;
        OnChanged?.Invoke();
    }

    private sealed class EphemIndex
    {
        public List<EphemYearMeta> Years { get; set; } = [];
    }

    private sealed class EphemYearMeta
    {
        public int Year { get; set; }
        public DateTime Start { get; set; }
        public int Count { get; set; }
        public string File { get; set; } = "";
    }
}

/// <summary>A clicked point: inside the 80–90°S coverage and on terrain, slope (deg) and height (m) from the 240 m DEM.</summary>
public sealed record CustomPoint(bool InCoverage, double SlopeDeg, double HeightM, string? Error = null);
