using SeleneFrontend.Models;
using System.Text.Json;

namespace SeleneFrontend.Services;

public class MissionConfigState
{
    // JSON files use camelCase keys; Web defaults make property matching case-insensitive.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public event Action? OnStateChanged;
    public event Action<string>? OnFlyToRequested;

    public List<Site> AvailableSites { get; private set; } = [];
    public Site? SelectedSite { get; private set; }
    public bool CanFlyTo => SelectedSite is not null && !SelectedSite.Id.StartsWith("CUSTOM", StringComparison.Ordinal);
    
    public List<LanderPreset> AllLanders { get; private set; } = [];
    public List<LanderPreset> FilteredLanders => AllLanders.Where(l => l.Class == SelectedLanderClass).ToList();
    public LanderPreset? SelectedLander { get; private set; }
    public string SelectedLanderClass { get; private set; } = "clps";
    
    public DateTime WindowStart { get; private set; } = DateTime.UtcNow;
    public DateTime WindowEnd { get; private set; } = DateTime.UtcNow.AddYears(2);
    public double MissionDurationDays { get; private set; } = 7;
    public double TimeStepHours { get; private set; } = 1;
    
    public double MinCommsPercent { get; private set; } = 50;
    public double MinBatteryPercent { get; private set; } = 20;

    public MissionConfigState(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task LoadData()
    {
        try
        {
            var sitesJson = await _httpClient.GetStringAsync("data/sites.json");
            AvailableSites = JsonSerializer.Deserialize<List<Site>>(sitesJson, JsonOpts) ?? [];

            var landersJson = await _httpClient.GetStringAsync("data/landers.json");
            AllLanders = JsonSerializer.Deserialize<List<LanderPreset>>(landersJson, JsonOpts) ?? [];
            
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
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task SelectLander(string landerId)
    {
        SelectedLander = AllLanders.FirstOrDefault(l => l.Id == landerId);
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateLanderPower(double value)
    {
        if (SelectedLander != null)
        {
            SelectedLander.Power = value;
            OnStateChanged?.Invoke();
        }
        await Task.CompletedTask;
    }

    public async Task UpdateLanderBattery(double value)
    {
        if (SelectedLander != null)
        {
            SelectedLander.Battery = value;
            OnStateChanged?.Invoke();
        }
        await Task.CompletedTask;
    }

    public async Task UpdateLanderLoad(double value)
    {
        if (SelectedLander != null)
        {
            SelectedLander.Load = value;
            OnStateChanged?.Invoke();
        }
        await Task.CompletedTask;
    }

    public async Task UpdateLanderDarkness(double value)
    {
        if (SelectedLander != null)
        {
            SelectedLander.MaxDarkness = value;
            OnStateChanged?.Invoke();
        }
        await Task.CompletedTask;
    }

    public async Task UpdateLanderSourceUrl(string url)
    {
        if (SelectedLander != null)
        {
            SelectedLander.SourceUrl = url;
            OnStateChanged?.Invoke();
        }
        await Task.CompletedTask;
    }

    public async Task UpdateWindowStart(DateTime date)
    {
        WindowStart = date;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateWindowEnd(DateTime date)
    {
        WindowEnd = date;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
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
        WindowStart = DateTime.UtcNow;
        WindowEnd = DateTime.UtcNow.AddYears(2);
        MissionDurationDays = 7;
        TimeStepHours = 1;
        MinCommsPercent = 50;
        MinBatteryPercent = 20;
        SelectedLander = null;
        OnStateChanged?.Invoke();
    }
}
