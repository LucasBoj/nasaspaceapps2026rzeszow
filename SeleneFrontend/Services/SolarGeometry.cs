using System.Runtime.CompilerServices;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// A <see cref="PanelLayout"/> compiled for fast evaluation: per-facet constants and the normalisation K.
/// Instances are cached per layout object (<see cref="For"/>), so they can be used as cache keys.
/// </summary>
public sealed class PanelModel
{
    private static readonly ConditionalWeakTable<PanelLayout, PanelModel> Cache = new();
    private static readonly object Gate = new();

    internal readonly double[] CosE, SinE, OffsetRad, Share;   // Share normalised to sum 1

    /// <summary>K: the largest raw incidence sum over Sun directions az 0–359°, el 0–<see cref="SolarGeometry.ReferenceMaxElevDeg"/>°.</summary>
    public double Normalization { get; }

    public int FacetCount => Share.Length;

    private PanelModel(PanelLayout layout)
    {
        var facets = layout.Facets.Where(f => f.Share > 0).ToList();
        var total = facets.Sum(f => f.Share);
        CosE = facets.Select(f => Math.Cos(Rad(f.NormalElevDeg))).ToArray();
        SinE = facets.Select(f => Math.Sin(Rad(f.NormalElevDeg))).ToArray();
        OffsetRad = facets.Select(f => Rad(f.AzOffsetDeg)).ToArray();
        Share = facets.Select(f => f.Share / total).ToArray();
        Normalization = ComputeNormalization();
    }

    /// <summary>Compiled model for a fixed layout; null for ideal tracking (or no layout).</summary>
    public static PanelModel? For(PanelLayout? layout)
    {
        if (layout is not { IsFixed: true }) return null;
        lock (Gate) return Cache.GetValue(layout, l => new PanelModel(l));
    }

    /// <summary>Raw Lambert sum Σ shareᵢ·max(0, cos θᵢ) for a Sun direction and lander heading (radians).</summary>
    public double Raw(double cosEl, double sinEl, double cosAz, double sinAz, ReadOnlySpan<double> cosPhi, ReadOnlySpan<double> sinPhi)
    {
        double sum = 0;
        for (var i = 0; i < Share.Length; i++)
        {
            // cos θ = cos ε·cos el·cos(az − φ) + sin ε·sin el,  cos(az − φ) = cos az·cos φ + sin az·sin φ
            var c = CosE[i] * cosEl * (cosAz * cosPhi[i] + sinAz * sinPhi[i]) + SinE[i] * sinEl;
            if (c > 0) sum += Share[i] * c;
        }
        return sum;
    }

    /// <summary>Facet azimuths (absolute, clockwise from north) for a lander heading: cos φᵢ and sin φᵢ.</summary>
    public void FacetAzimuths(double headingDeg, Span<double> cosPhi, Span<double> sinPhi)
    {
        var h = Rad(headingDeg);
        for (var i = 0; i < Share.Length; i++)
        {
            cosPhi[i] = Math.Cos(h + OffsetRad[i]);
            sinPhi[i] = Math.Sin(h + OffsetRad[i]);
        }
    }

    private double ComputeNormalization()
    {
        Span<double> cp = stackalloc double[Share.Length];
        Span<double> sp = stackalloc double[Share.Length];
        FacetAzimuths(0, cp, sp);
        double best = 0;
        for (var el10 = 0; el10 <= SolarGeometry.ReferenceMaxElevDeg * 2; el10++)   // 0.5° steps
        {
            var el = Rad(el10 / 2.0);
            for (var az = 0; az < 360; az++)
            {
                var a = Rad(az);
                best = Math.Max(best, Raw(Math.Cos(el), Math.Sin(el), Math.Cos(a), Math.Sin(a), cp, sp));
            }
        }
        return best > 0 ? best : 1;
    }

    internal static double Rad(double deg) => deg * Math.PI / 180.0;
}

/// <summary>
/// Solar incidence on fixed lander panels (Lambert cosine). Pure logic, no UI.
/// <code>
///   Sun direction (local east-north-up):   s = (cos el·sin az, cos el·cos az, sin el)
///   facet i normal:  azimuth φᵢ = heading + azOffsetᵢ, elevation εᵢ (0 = vertical, 90 = deck)
///   cos θᵢ = cos εᵢ·cos el·cos(az − φᵢ) + sin εᵢ·sin el                 (Lambert, clamped at 0)
///   factor = Σ shareᵢ·max(0, cos θᵢ) / K
///   K      = max of the same sum over Sun directions az 0–359°, el 0–10° (the polar regime)
///   P(t)   = solarArrayW × factor × sunFraction/100
/// </code>
/// K makes the array power in landers.json the output at the best Sun direction for that layout, so a
/// perfectly aimed lander reproduces the ideal-tracking result and every other Sun direction costs power.
/// A published summed panel rating converts as rating × K (Nova-C: FCC "maximum of 788 W" × 0.626 = 494 W). The Sun's azimuth sweeps the whole horizon each lunar day
/// at the pole, so the result depends on the landing date and the lander's heading at touchdown.
/// </summary>
public static class SolarGeometry
{
    public const double ReferenceMaxElevDeg = 10;

    /// <summary>Heading grid of the AUTO search (degrees); a parabola through the best 3 refines it.</summary>
    public const int HeadingStepDeg = 10;

    private const int HeadingCount = 360 / HeadingStepDeg;

    private static readonly ConditionalWeakTable<IlluminationSeries, Dictionary<PanelModel, float[][]>> Tables = new();
    private static readonly object Gate = new();

    /// <summary>Normalised incidence factor for one Sun direction (degrees) — convenience for tests and UI.</summary>
    public static double Factor(PanelModel model, double elDeg, double azDeg, double headingDeg)
    {
        Span<double> cp = stackalloc double[model.FacetCount];
        Span<double> sp = stackalloc double[model.FacetCount];
        model.FacetAzimuths(headingDeg, cp, sp);
        var el = PanelModel.Rad(elDeg);
        var az = PanelModel.Rad(azDeg);
        return model.Raw(Math.Cos(el), Math.Sin(el), Math.Cos(az), Math.Sin(az), cp, sp) / model.Normalization;
    }

    /// <summary>
    /// Mission energy for a heading in "full-array hours": Σ sunFraction/100 × factor × stepHours.
    /// Multiply by solarArrayW for Wh.
    /// </summary>
    public static double MissionEnergy(IlluminationSeries series, int start, int steps, PanelModel model, double headingDeg)
    {
        var (cosEl, sinEl, cosAz, sinAz) = series.SunTrig();
        Span<double> cp = stackalloc double[model.FacetCount];
        Span<double> sp = stackalloc double[model.FacetCount];
        model.FacetAzimuths(headingDeg, cp, sp);
        double sum = 0;
        for (var t = start; t < start + steps; t++)
        {
            var sf = series.SunFraction[t];
            if (sf <= 0) continue;
            sum += sf / 100.0 * model.Raw(cosEl[t], sinEl[t], cosAz[t], sinAz[t], cp, sp);
        }
        return sum / model.Normalization * series.StepHours;
    }

    /// <summary>
    /// AUTO landing heading: the yaw at touchdown that maximises the mission's solar energy.
    /// Uses per-heading prefix sums (10° grid, built once per series and layout), then a parabola through
    /// the best grid heading and its neighbours. Returns degrees in [0, 360); 0 when the mission is all dark.
    /// </summary>
    public static double BestHeading(IlluminationSeries series, int start, int steps, PanelModel model)
    {
        var table = PrefixTable(series, model);
        var end = start + steps;
        Span<double> e = stackalloc double[HeadingCount];
        int best = 0;
        for (var k = 0; k < HeadingCount; k++)
        {
            e[k] = table[k][end] - table[k][start];
            if (e[k] > e[best] + 1e-9) best = k;
        }
        if (e[best] <= 1e-9) return 0;

        var em = e[(best + HeadingCount - 1) % HeadingCount];
        var ep = e[(best + 1) % HeadingCount];
        var denom = em - 2 * e[best] + ep;
        var delta = denom < -1e-12 ? Math.Clamp(0.5 * (em - ep) / denom, -0.5, 0.5) : 0;
        var heading = (best + delta) * HeadingStepDeg;
        return ((Math.Round(heading) % 360) + 360) % 360;
    }

    /// <summary>prefix[k][t] = Σ_{j&lt;t} sunFraction_j/100 × raw incidence at heading k·10° (un-normalised, per step).</summary>
    private static float[][] PrefixTable(IlluminationSeries series, PanelModel model)
    {
        if (TryGetTable(series, model) is { } cached) return cached;
        var (cosEl, sinEl, cosAz, sinAz) = series.SunTrig();
        var table = new float[HeadingCount][];
        for (var k = 0; k < HeadingCount; k++) table[k] = PrefixRow(series, model, k, cosEl, sinEl, cosAz, sinAz);
        return StoreTable(series, model, table);
    }

    /// <summary>
    /// Build the AUTO-heading table for <paramref name="series"/> in slices, yielding to the caller's scheduler
    /// between headings. On the single WebAssembly thread a one-shot build (~2 million incidence evaluations)
    /// blocks rendering for ~100 ms per site; sliced, each slice stays within a frame. Results are identical
    /// to the synchronous build; a no-op when the table already exists or the layout is not fixed.
    /// </summary>
    public static async Task WarmUpAsync(IlluminationSeries series, PanelModel? model, double budgetMs = 8,
                                         CancellationToken ct = default)
    {
        if (model is null || !series.HasSunGeometry || TryGetTable(series, model) is not null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (cosEl, sinEl, cosAz, sinAz) = series.SunTrig();
        var table = new float[HeadingCount][];
        for (var k = 0; k < HeadingCount; k++)
        {
            if (sw.Elapsed.TotalMilliseconds >= budgetMs)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                sw.Restart();
            }
            table[k] = PrefixRow(series, model, k, cosEl, sinEl, cosAz, sinAz);
        }
        StoreTable(series, model, table);
    }

    private static float[][]? TryGetTable(IlluminationSeries series, PanelModel model)
    {
        lock (Gate)
        {
            var perSeries = Tables.GetOrCreateValue(series);
            return perSeries.TryGetValue(model, out var cached) && cached[0].Length == series.Count + 1 ? cached : null;
        }
    }

    private static float[][] StoreTable(IlluminationSeries series, PanelModel model, float[][] table)
    {
        lock (Gate)
        {
            Tables.GetOrCreateValue(series)[model] = table;
            return table;
        }
    }

    private static float[] PrefixRow(IlluminationSeries series, PanelModel model, int k,
                                     double[] cosEl, double[] sinEl, double[] cosAz, double[] sinAz)
    {
        Span<double> cp = stackalloc double[model.FacetCount];
        Span<double> sp = stackalloc double[model.FacetCount];
        model.FacetAzimuths(k * HeadingStepDeg, cp, sp);
        var row = new float[series.Count + 1];
        double acc = 0;
        for (var t = 0; t < series.Count; t++)
        {
            var sf = series.SunFraction[t];
            if (sf > 0) acc += sf / 100.0 * model.Raw(cosEl[t], sinEl[t], cosAz[t], sinAz[t], cp, sp);
            row[t + 1] = (float)acc;
        }
        return row;
    }
}
