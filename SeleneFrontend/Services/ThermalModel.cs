namespace SeleneFrontend.Services;

/// <summary>
/// Warm-box thermal inputs of a lander (effective values). Null in <see cref="LanderInputs.Thermal"/> = no heater model
/// (the survival load alone, the pre-2026-11-15 behaviour).
/// </summary>
/// <param name="HeatLossW">Heat the warm box radiates at <paramref name="OperatingTempC"/> to a 0 K sky (radiators sized for the day load).</param>
/// <param name="HeatCapacityJPerK">Thermal mass of the warm box (J/K); 0 = heaters hold the set point from the first dark hour.</param>
/// <param name="OperatingTempC">Box temperature while lit, and at the start of every dark period.</param>
/// <param name="MinTempC">Heater set point: the box is never let below it.</param>
/// <param name="GroundViewFactor">Share of the radiating area that sees the ground (the rest sees the ~3 K sky).</param>
public sealed record ThermalInputs(double HeatLossW, double HeatCapacityJPerK, double OperatingTempC, double MinTempC,
                                   double GroundViewFactor);

/// <summary>
/// One-node thermal model of a lander's warm electronics box. Pure logic, no UI (linked into SeleneFrontend.Tests).
/// <list type="bullet">
/// <item>Lit (sunFraction ≥ 50): the day load and the Sun keep the box at the operating temperature; no heaters.</item>
/// <item>Dark: heat loss Q = G·(T⁴ − F·T_ground⁴), G = heatLoss / T_op⁴ (the day-sized radiators keep radiating, no
/// louvers). The survival electronics dissipate their power inside the box. The box cools (C·dT/dt = P_int − Q) until it
/// reaches the set point; from then on heaters supply max(0, Q(T_min) − P_int).</item>
/// <item>T_ground per hour from LRO Diviner at the site (<see cref="Models.DivinerPoint"/>); 0 K without Diviner data.</item>
/// </list>
/// </summary>
public static class ThermalModel
{
    public const double ZeroCelsiusK = 273.15;

    /// <summary>Euler sub-steps per simulation step while the box is still cooling (time constant ≳ 2 h).</summary>
    public const int SubSteps = 4;

    public static double ToKelvin(double celsius) => celsius + ZeroCelsiusK;

    private static double Pow4(double x) { var x2 = x * x; return x2 * x2; }

    /// <summary>Radiative conductance G (W/K⁴): heat loss at the operating temperature to a 0 K sink, divided by T_op⁴.</summary>
    public static double Conductance(ThermalInputs t) => t.HeatLossW / Pow4(ToKelvin(t.OperatingTempC));

    /// <summary>Heat the box loses at <paramref name="tempK"/> with the ground at <paramref name="groundK"/> (W).</summary>
    public static double LossW(ThermalInputs t, double tempK, double groundK) =>
        Conductance(t) * (Pow4(tempK) - t.GroundViewFactor * Pow4(groundK));

    /// <summary>Heater power that holds the box at its set point: max(0, Q(T_min) − internal dissipation) (W).</summary>
    public static double HoldPowerW(ThermalInputs t, double internalW, double groundK) =>
        Math.Max(0, LossW(t, ToKelvin(t.MinTempC), groundK) - internalW);

    /// <summary>Temperature at which the electronics alone balance the loss (K): the box never goes lower without heaters.</summary>
    public static double EquilibriumK(ThermalInputs t, double internalW, double groundK)
    {
        var g = Conductance(t);
        return g > 0 ? Math.Pow(internalW / g + t.GroundViewFactor * Pow4(groundK), 0.25) : double.PositiveInfinity;
    }

    /// <summary>The warm box during a simulation: call <see cref="Lit"/> or <see cref="Dark"/> once per step.</summary>
    public struct WarmBox
    {
        private readonly double _g, _f, _c, _opK, _minK;
        public double TempK { get; private set; }

        public WarmBox(ThermalInputs t)
        {
            _g = Conductance(t);
            _f = t.GroundViewFactor;
            _c = t.HeatCapacityJPerK;
            _opK = ToKelvin(t.OperatingTempC);
            _minK = ToKelvin(t.MinTempC);
            TempK = _opK;
        }

        /// <summary>A lit step: the box is held at the operating temperature.</summary>
        public void Lit() => TempK = _opK;

        /// <summary>One dark step of <paramref name="dtHours"/>: cools the box and returns the heater energy (Wh).</summary>
        public double Dark(double internalW, double groundK, double dtHours)
        {
            var sink = _f * Pow4(groundK);
            var hold = Math.Max(0, _g * (Pow4(_minK) - sink) - internalW);
            // On the thermostat (or no thermal mass): heaters hold the set point for the whole step.
            if (_c <= 0 || (TempK <= _minK + 1e-9 && hold > 0))
            {
                TempK = hold > 0 ? _minK : Math.Max(_minK, Math.Pow(internalW / _g + sink, 0.25));
                return hold * dtHours;
            }
            var h = dtHours / SubSteps;
            var seconds = h * 3600;
            double heaterWh = 0;
            for (var s = 0; s < SubSteps; s++)
            {
                var dT = (internalW - _g * (Pow4(TempK) - sink)) * seconds / _c;
                var next = TempK + dT;
                if (next < _minK && hold > 0)
                {
                    var cooling = dT < 0 ? Math.Clamp((TempK - _minK) / -dT, 0, 1) : 1;   // share of the sub-step before the set point
                    heaterWh += hold * h * (1 - cooling);
                    TempK = _minK;
                }
                else
                {
                    TempK = Math.Max(next, hold > 0 ? _minK : next);
                }
            }
            return heaterWh;
        }
    }

    /// <summary>
    /// Hours a battery carries the night in continuous darkness: <paramref name="usableWh"/> of stored energy drawn at
    /// 1/√η for the survival load plus heaters, ground at <paramref name="groundK"/>, box starting at the operating
    /// temperature. <paramref name="thermal"/> null = survival load only. Capped at <paramref name="capHours"/>.
    /// </summary>
    public static double DarkEnduranceHours(double usableWh, double survivalW, ThermalInputs? thermal, double groundK,
                                            double roundTripEff = 1, double capHours = 2000)
    {
        if (usableWh <= 0) return 0;
        var etaHalf = Math.Sqrt(roundTripEff is > 0 and <= 1 ? roundTripEff : 1);
        if (thermal is null) return survivalW > 0 ? Math.Min(capHours, usableWh * etaHalf / survivalW) : capHours;
        const double dt = 0.05;
        var box = new WarmBox(thermal);
        var minK = ToKelvin(thermal.MinTempC);
        double left = usableWh, t = 0;
        while (t < capHours)
        {
            var before = box.TempK;
            var need = (survivalW * dt + box.Dark(survivalW, groundK, dt)) / etaHalf;
            if (need >= left) return t + dt * left / need;
            left -= need;
            t += dt;
            // Steady from here (on the thermostat, or settled at the electronics-only equilibrium): constant draw.
            var onThermostat = box.TempK <= minK + 1e-9 && HoldPowerW(thermal, survivalW, groundK) > 0;
            if (onThermostat || Math.Abs(box.TempK - before) < 1e-7)
            {
                var rate = (survivalW + (onThermostat ? HoldPowerW(thermal, survivalW, groundK) : 0)) / etaHalf;
                return rate > 0 ? Math.Min(capHours, t + left / rate) : capHours;
            }
        }
        return capHours;
    }
}
