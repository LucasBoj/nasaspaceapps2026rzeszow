using System.Net.Http.Json;
using System.Text.Json;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>Lazy, cached access to the precomputed illumination data in <c>wwwroot/data/illumination/</c>.</summary>
public sealed class IlluminationRepository(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Task<IlluminationSeries>> _series = [];
    private Task<IlluminationIndex>? _index;

    /// <summary>index.json (data range and site list), loaded once.</summary>
    public Task<IlluminationIndex> GetIndexAsync() =>
        _index ??= LoadAsync<IlluminationIndex>("data/illumination/index.json");

    /// <summary>One site's hourly series, loaded on first use and cached.</summary>
    public Task<IlluminationSeries> GetSeries(string siteId)
    {
        if (!_series.TryGetValue(siteId, out var task))
        {
            task = LoadAsync<IlluminationSeries>($"data/illumination/{Uri.EscapeDataString(siteId)}.json");
            _series[siteId] = task;
        }
        return task;
    }

    /// <summary>Hourly sample index for a UTC time (same grid for every site), or −1 outside the data.</summary>
    public async Task<int> IndexOf(DateTime utc)
    {
        var index = await GetIndexAsync();
        if (TimeUtil.AsUtc(utc) > TimeUtil.AsUtc(index.End).AddHours(index.StepHours)) return -1;
        var hours = (TimeUtil.AsUtc(utc) - TimeUtil.AsUtc(index.Start)).TotalHours;
        return hours < 0 ? -1 : (int)Math.Floor(hours / index.StepHours + 1e-9);
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
