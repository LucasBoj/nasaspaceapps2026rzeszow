# SELENE OPS — Lunar Landing Window Planner

A Blazor WebAssembly application for planning lunar landing missions, part of the NASA Space Apps Challenge 2026 (Lunar Landing Site Navigator / CLPS Lunar Mission Browser challenge).

## Quick Start

### Prerequisites
- .NET 10 SDK (or .NET 8 as fallback)

### Running Locally

```bash
cd SeleneFrontend
dotnet watch
```

The application will start at `http://localhost:5000` (or similar). Check the terminal for the exact URL.

### Cesium Ion Configuration (Optional)

To display lunar terrain visualization, add a Cesium ion token:

1. Get a free token from https://ion.cesium.com
2. Add it to `wwwroot/appsettings.json`:
   ```json
   {
     "CesiumIonToken": "your_token_here"
   }
   ```

Without a token, the app will show the Moon as an ellipsoid with a notice.

## Architecture

- **Frontend**: Blazor WebAssembly (.NET 10)
- **3D Visualization**: CesiumJS 1.120
- **Styling**: Custom CSS (no MudBlazor/Radzen) with CSS variables for theming
- **State Management**: `MissionConfigState` service

### Directory Structure

```
SeleneFrontend/
├── Pages/
│   ├── Home.razor          # Main layout & composition
│   └── NotFound.razor
├── Components/
│   ├── TopBar.razor        # Status bar with UTC clock
│   ├── MoonPanel.razor     # Cesium 3D viewer integration
│   ├── ConfigPanel.razor   # Mission configuration UI
│   └── ResultsStrip.razor  # Expandable results panel
├── Services/
│   └── MissionConfigState.cs  # State management & business logic
├── Models/
│   ├── Site.cs             # Lunar landing site model
│   └── LanderPreset.cs     # Spacecraft configuration
├── wwwroot/
│   ├── css/
│   │   └── app.css         # Mission control themed styling
│   ├── js/
│   │   └── moonViewer.js   # Cesium viewer wrapper (ES module)
│   ├── data/
│   │   ├── sites.json      # Artemis candidate landing sites
│   │   └── landers.json    # CLPS & HLS spacecraft data
│   ├── appsettings.json    # Cesium ion token config
│   └── index.html
├── Layout/
│   └── MainLayout.razor
├── Program.cs
├── _Imports.razor
└── SeleneFrontend.csproj
```

## UI Layout

- **Top Bar** (48px): App name, UTC clock (updates every second), 3 status indicators
- **Left Panel** (~65% width): CesiumJS 3D Moon viewer with landing site pins
- **Splitter**: Draggable divider to resize panels
- **Right Panel** (~35% width): Mission configuration with 4 sections
  - 01 TARGET SITE: Select & navigate to landing site
  - 02 LANDER: Choose spacecraft class & model, enter specs
  - 03 MISSION WINDOW: Start date, end date, mission duration, time step
  - 04 CONSTRAINTS: Min comms %, min battery %
- **Bottom Strip** (56px, expandable): Results placeholder

All sizes adapt to 1920x1080; minimum width 1280px.

## Visual Theme

- **Palette**: Dark (#0a0e13), inspired by NASA Open MCT mission control consoles
- **Typography**: IBM Plex Sans (UI), IBM Plex Mono (numbers & HUD labels)
- **Colors**:
  - Text: #d7e2ec
  - Accent: #4fc3f7 (cyan)
  - Warning: #ffb020
  - Error: #ff4d4f
  - Borders: #223040 (1px)

## Cesium Integration

All Cesium calls flow through `wwwroot/js/moonViewer.js`, invoked from C# via:

```csharp
moonModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/moonViewer.js");
await moonModule.InvokeVoidAsync("initViewer", "moon-viewer-element", dotNetRef);
```

Exported functions:
- `initViewer(elementId, dotNetRef)` — Create viewer & load sites
- `loadSites(sites)` — Render landing site pins
- `flyToSite(siteId)` — Animate camera to site
- `selectSite(siteId)` — Highlight site pin
- `setSunDirection(azimuthDeg, elevationDeg)` — Update lighting (stub)
- `dispose()` — Clean up viewer

Callbacks to C#:
- `dotNetRef.invokeMethodAsync('OnSiteSelected', siteId)`
- `dotNetRef.invokeMethodAsync('OnCustomSiteCreated', lat, lon)`

## Data Files

### sites.json
9 Artemis candidate landing sites with approximate coordinates (South Pole region):
- Peak near Cabeus B, Haworth, Malapert Massif, Mons Mouton Plateau, Mons Mouton, Nobile Rim 1 & 2, de Gerlache Rim 2, Slater Plain

All marked `"approximate": true` with source "TODO" — **requires verification** before operational use.

### landers.json
- **CLPS (robotic)**: Nova-C, Blue Ghost, Griffin, + Custom
- **HLS (crewed)**: Starship HLS, Blue Moon Mk2, + Custom

Parameter fields (Power, Battery, Load, MaxDarkness) are empty — placeholders for user input or verified public specs.

## Next Steps (TODO)

1. **Cesium Ion Token**: Add valid token to `appsettings.json` to display LOLA terrain tiles
2. **Site Coordinates**: Verify & update approximate coordinates from public sources (LRO GIS, USGS)
3. **Lander Specs**: Populate default power, battery, load specs from:
   - Intuitive Machines mission docs
   - Firefly Blue Ghost datasheet
   - Astrobotic Griffin specs
   - SpaceX & Blue Origin HLS documentation
4. **Sun Direction**: Implement DirectionalLight in moonViewer.js for illumination simulation
5. **Analysis Engine**: Add backend computation for:
   - Illumination & eclipse windows per site & date
   - Earth communication windows & antenna visibility
   - Power budget & battery charge cycles
   - Mission feasibility scoring

## Not Affiliated with NASA

Built for NASA Space Apps Challenge 2026 · Data: NASA LRO/LOLA, JPL ephemerides · Not affiliated with or endorsed by NASA.

---

Questions? See [NASA Space Apps Challenge](https://www.spaceappschallenge.org/)
