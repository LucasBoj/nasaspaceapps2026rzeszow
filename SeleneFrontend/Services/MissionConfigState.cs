using SeleneFrontend.Models;
using System.Text.Json;

namespace SeleneFrontend.Services;

public class MissionConfigState
{
    // JSON files use camelCase keys; Web defaults make property matching case-insensitive.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IlluminationRepository _illumination;

    public event Action? OnStateChanged;
    public event Action<string>? OnFlyToRequested;

    public List<Site> AvailableSites { get; private set; } = [];
    public Site? SelectedSite { get; private set; }
    public bool CanFlyTo => SelectedSite is not null && !SelectedSite.Id.StartsWith("CUSTOM", StringComparison.Ordinal);
    
    public List<LanderPreset> AllLanders { get; private set; } = [];
    public List<LanderPreset> FilteredLanders => AllLanders.Where(l => l.Class == SelectedLanderClass).ToList();
    public LanderPreset? SelectedLander { get; private set; }
    public string SelectedLanderClass { get; private set; } = "clps";

    // ---- What-if overrides (pitch comparisons). Data presets are never mutated. ----
    public const string WhatIfSolarArrayW = "solarArrayW";
    public const string WhatIfBatteryWh = "batteryWh";
    public const string WhatIfSurvivalLoadW = "survivalLoadW";

    private readonly Dictionary<string, double> _whatIf = [];

    public bool HasWhatIf => _whatIf.Count > 0;
    public bool IsWhatIf(string key) => _whatIf.ContainsKey(key);

    /// <summary>Effective value used by calculations: what-if override if set, otherwise the preset figure.</summary>
    public double? Effective(string key)
    {
        if (_whatIf.TryGetValue(key, out var v)) return v;
        return key switch
        {
            WhatIfSolarArrayW => SelectedLander?.SolarArrayW?.Value,
            WhatIfBatteryWh => SelectedLander?.BatteryWh?.Value,
            WhatIfSurvivalLoadW => SelectedLander?.SurvivalLoadW?.Value,
            _ => null,
        };
    }

    public void SetWhatIf(string key, double value)
    {
        _whatIf[key] = value;
        OnStateChanged?.Invoke();
    }

    public void ResetWhatIf()
    {
        if (_whatIf.Count == 0) return;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
    }
    
    // Landing-date window (UTC dates). Set to the precomputed data range once it is known.
    public DateTime WindowStart { get; private set; } = DateTime.UtcNow.Date;
    public DateTime WindowEnd { get; private set; } = DateTime.UtcNow.Date.AddYears(2);

    /// <summary>First / last day with precomputed illumination data (UTC).</summary>
    public DateTime? DataStart { get; private set; }
    public DateTime? DataEnd { get; private set; }

    /// <summary>Shown under the window fields when the user picked dates outside the data.</summary>
    public string? WindowNotice { get; private set; }

    /// <summary>
    /// Bumped on every window edit. Used as @key on the date inputs: when a typed date is
    /// clipped back to the same value, Blazor sees no attribute change and would leave the
    /// out-of-range text in the field.
    /// </summary>
    public int WindowRevision { get; private set; }

    public string DataRangeText => DataStart is { } a && DataEnd is { } b
        ? $"data available {a:yyyy-MM-dd} … {b:yyyy-MM-dd}" : "";
    public double MissionDurationDays { get; private set; } = 7;
    public double TimeStepHours { get; private set; } = 1;
    
    public double MinCommsPercent { get; private set; } = 50;
    public double MinBatteryPercent { get; private set; } = 20;

    public MissionConfigState(HttpClient httpClient, IlluminationRepository illumination)
    {
        _httpClient = httpClient;
        _illumination = illumination;
    }

    public async Task LoadData()
    {
        try
        {
            var sitesJson = await _httpClient.GetStringAsync("data/sites.json");
            AvailableSites = JsonSerializer.Deserialize<List<Site>>(sitesJson, JsonOpts) ?? [];

            var landersJson = await _httpClient.GetStringAsync("data/landers.json");
            AllLanders = JsonSerializer.Deserialize<List<LanderPreset>>(landersJson, JsonOpts) ?? [];
            
            try
            {
                var (dataStart, dataEnd) = await _illumination.GetRangeAsync();
                DataStart = dataStart.Date;
                DataEnd = dataEnd.Date;
                WindowStart = DataStart.Value;
                WindowEnd = DataEnd.Value;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MissionConfigState] Illumination index unavailable: {ex.Message}");
            }

            if (AvailableSites.Count > 0)
            {
                SelectedSite = AvailableSites[0];
            }
            
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading data: {ex.Message}");
        }
    }

    public async Task SelectSite(string siteId, Site? customSite = null)
    {
        if (customSite != null)
        {
            SelectedSite = customSite;
        }
        else
        {
            SelectedSite = AvailableSites.FirstOrDefault(s => s.Id == siteId);
        }
        OnStateChanged?.Invoke();
        // Every explicit site selection (map pin or list) flies the camera there.
        RequestFlyTo();
        await Task.CompletedTask;
    }

    public void RequestFlyTo()
    {
        if (CanFlyTo)
        {
            OnFlyToRequested?.Invoke(SelectedSite!.Id);
        }
    }

    public async Task SelectLanderClass(string landerClass)
    {
        SelectedLanderClass = landerClass;
        SelectedLander = null;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task SelectLander(string landerId)
    {
        SelectedLander = AllLanders.FirstOrDefault(l => l.Id == landerId);
        _whatIf.Clear();
        if (SelectedLander?.MissionDurationDays is { } duration)
        {
            MissionDurationDays = duration.Value;
        }
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateWindowStart(DateTime date)
    {
        WindowStart = Clip(TimeUtil.AsUtc(date).Date);
        if (WindowEnd < WindowStart) WindowEnd = WindowStart;
        WindowRevision++;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateWindowEnd(DateTime date)
    {
        WindowEnd = Clip(TimeUtil.AsUtc(date).Date);
        if (WindowStart > WindowEnd) WindowStart = WindowEnd;
        WindowRevision++;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>Clamp a date to the data range and remember a notice when clipping happened.</summary>
    private DateTime Clip(DateTime date)
    {
        WindowNotice = null;
        if (DataStart is { } a && date < a) { WindowNotice = DataRangeText; return a; }
        if (DataEnd is { } b && date > b) { WindowNotice = DataRangeText; return b; }
        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    public async Task UpdateMissionDuration(double days)
    {
        MissionDurationDays = days;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateTimeStep(double hours)
    {
        TimeStepHours = hours;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateMinComms(double percent)
    {
        MinCommsPercent = percent;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateMinBattery(double percent)
    {
        MinBatteryPercent = percent;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void Reset()
    {
        WindowStart = DataStart ?? DateTime.UtcNow.Date;
        WindowEnd = DataEnd ?? DateTime.UtcNow.Date.AddYears(2);
        WindowNotice = null;
        MissionDurationDays = 7;
        TimeStepHours = 1;
        MinCommsPercent = 50;
        MinBatteryPercent = 20;
        SelectedLander = null;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
    }
}
