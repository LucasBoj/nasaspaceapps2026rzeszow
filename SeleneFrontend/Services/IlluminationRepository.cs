using System.Net.Http.Json;
using System.Text.Json;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// Lazy, cached access to the precomputed illumination data in <c>wwwroot/data/illumination/</c>.
/// The data is split into one file per site and calendar year (2026-11 … 2035); only the years
/// covering the current analysis window are downloaded, then joined into one series per site.
/// </summary>
public sealed class IlluminationRepository(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Days after the window end that must be covered too: the mission and the 30-day max-stay cap.</summary>
    public const int TailDays = 62;

    private readonly Dictionary<string, Task<IlluminationSeries>> _years = [];    // "site|2027" → one yearly file
    private readonly Dictionary<string, Task<IlluminationSeries>> _joined = [];   // "site|2027|2029" → joined years
    private Task<IlluminationIndex>? _index;
    private DateTime _from = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private DateTime _to = new(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>index.json (data range, years and site list), loaded once.</summary>
    public Task<IlluminationIndex> GetIndexAsync() =>
        _index ??= LoadAsync<IlluminationIndex>("data/illumination/index.json");

    /// <summary>The landing window the next <see cref="GetSeries(string)"/> calls must cover (set by MissionConfigState).</summary>
    public void SetWindow(DateTime from, DateTime to)
    {
        _from = TimeUtil.AsUtc(from);
        _to = TimeUtil.AsUtc(to);
    }

    /// <summary>Years needed for a window: its first year … the year of (end + mission tail), within the data.</summary>
    public static (int First, int Last) YearsFor(DateTime from, DateTime to, IReadOnlyList<int> available)
    {
        var first = from.Year;
        var last = to.AddDays(TailDays).Year;
        if (available.Count > 0)
        {
            first = Math.Clamp(first, available.Min(), available.Max());
            last = Math.Clamp(last, first, available.Max());
        }
        return (first, last);
    }

    /// <summary>
    /// Series for CUSTOM site ids (any point in 80–90°S), computed in the browser by CustomSiteService:
    /// (siteId, first year, last year) → series in the same format as the precomputed files.
    /// </summary>
    public Func<string, int, int, Task<IlluminationSeries>>? CustomProvider { get; set; }

    public static bool IsCustom(string siteId) => siteId.StartsWith("CUSTOM", StringComparison.Ordinal);

    /// <summary>One site's hourly series covering the current window (joined yearly files, cached).</summary>
    public async Task<IlluminationSeries> GetSeries(string siteId)
    {
        var index = await GetIndexAsync();
        var (first, last) = YearsFor(_from, _to, index.Years);
        var key = $"{siteId}|{first}|{last}";
        if (!_joined.TryGetValue(key, out var task))
        {
            // Keep only the joined series of the current window (yearly files stay cached).
            foreach (var stale in _joined.Keys.Where(k => k.StartsWith(siteId + "|", StringComparison.Ordinal)).ToList())
                _joined.Remove(stale);
            task = IsCustom(siteId) && CustomProvider is { } custom ? custom(siteId, first, last) : JoinAsync(siteId, first, last);
            _joined[key] = task;
        }
        try { return await task; }
        catch
        {
            _joined.Remove(key);   // a failed download or computation may be retried
            throw;
        }
    }

    private async Task<IlluminationSeries> JoinAsync(string siteId, int first, int last)
    {
        var parts = await Task.WhenAll(Enumerable.Range(first, last - first + 1).Select(y => GetYear(siteId, y)));
        return IlluminationSeries.Concat(parts);
    }

    /// <summary>One yearly file, loaded on first use and cached.</summary>
    public Task<IlluminationSeries> GetYear(string siteId, int year)
    {
        var key = $"{siteId}|{year}";
        if (!_years.TryGetValue(key, out var task))
        {
            task = LoadAsync<IlluminationSeries>($"data/illumination/{Uri.EscapeDataString(siteId)}/{year}.json");
            _years[key] = task;
        }
        return task;
    }

    private readonly Dictionary<string, Task<LandingEllipseData?>> _ellipses = [];

    /// <summary>
    /// The landing-ellipse grid of a site (<c>{siteId}/ellipse.json</c>), cached; null when the file is missing or
    /// unreadable — it is optional, the app then shows no ellipse row.
    /// </summary>
    public Task<LandingEllipseData?> GetEllipse(string siteId)
    {
        // Custom points have no precomputed ellipse grid: no request (a 404 would show in the console).
        if (IsCustom(siteId)) return Task.FromResult<LandingEllipseData?>(null);
        if (!_ellipses.TryGetValue(siteId, out var task))
        {
            task = LoadOptionalAsync<LandingEllipseData>($"data/illumination/{Uri.EscapeDataString(siteId)}/ellipse.json");
            _ellipses[siteId] = task;
        }
        return task;
    }

    private async Task<T?> LoadOptionalAsync<T>(string url) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"[Data] optional {url} not loaded: {ex.Message}");
            return null;
        }
    }

    /// <summary>Data range: first sample … last sample (inclusive), UTC.</summary>
    public async Task<(DateTime Start, DateTime End)> GetRangeAsync()
    {
        var index = await GetIndexAsync();
        return (TimeUtil.AsUtc(index.Start), TimeUtil.AsUtc(index.End));
    }

    private async Task<T> LoadAsync<T>(string url)
    {
        var doc = await http.GetFromJsonAsync<T>(url, JsonOpts);
        return doc ?? throw new InvalidOperationException($"Empty or invalid JSON at {url}");
    }
}
