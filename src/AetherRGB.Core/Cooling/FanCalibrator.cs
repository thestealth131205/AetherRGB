using AetherRGB.Core.Config;

namespace AetherRGB.Core.Cooling;

public enum CalibrationPhase { MinSignal, ThermalResponse, Done }

public sealed record CalibrationProgress(CalibrationPhase Phase, string Text, double FractionDone);

/// <summary>Smart-Kalibrierung: misst für jeden steuerbaren Lüfter die niedrigste Ansteuerung mit
/// Drehzahlsignal, stellt danach alle auf dieses Minimum (mind. 50 %) und beobachtet, wie sich CPU- und
/// Gehäuse-/Mainboard-Temperatur über die Zeit dabei verhalten — daraus entsteht je ein „Effizient“- und ein
/// „Leise“-Profil. Ohne echte Last (ein Kalibrierungslauf startet keine CPU-Last absichtlich) ist die
/// Temperaturänderung oft klein; sie fließt trotzdem als zusätzlicher Hinweis in die erzeugte Kurve ein, die
/// Stützpunkte selbst sind ein vorsichtiges, sicherheitsorientiertes Schema (siehe <see cref="FanCurve"/>).</summary>
public sealed class FanCalibrator
{
    /// <summary>Ansteuerungsstufen beim Hochfahren, um das Minimum mit Drehzahlsignal zu finden.</summary>
    private static readonly int[] RampSteps = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

    private readonly IFanControlSource _source;
    private readonly Func<double> _readCpuTemp;
    private readonly Func<double> _readBoardTemp;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Wartezeit nach jeder Ansteuerungsstufe, bis sich die Drehzahl eingeschwungen hat.</summary>
    public TimeSpan StepSettleTime { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Wie lange nach dem Umschalten auf das Minimum gewartet wird, bevor die Temperatur-Beobachtung beginnt.</summary>
    public TimeSpan BaselineSettleTime { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Beobachtungsfenster für die Temperaturänderung.</summary>
    public TimeSpan ThermalWindow { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Sichere Obergrenze für CPU-Temperatur — oberhalb davon soll jede Kurve auf 100 % stehen.</summary>
    public double CpuSafeCeilingC { get; set; } = 80;
    public double BoardSafeCeilingC { get; set; } = 60;

    public FanCalibrator(IFanControlSource source, Func<double> readCpuTemp, Func<double> readBoardTemp,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source;
        _readCpuTemp = readCpuTemp;
        _readBoardTemp = readBoardTemp;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public async Task<List<FanProfileConfig>> RunAsync(IReadOnlyList<FanProfileConfig> existing,
        IProgress<CalibrationProgress>? progress, CancellationToken ct)
    {
        var fans = _source.Detect().Where(f => f.Controllable).ToList();
        var minPercent = new Dictionary<string, int?>();
        var hasSignal = new Dictionary<string, bool>();
        foreach (var fan in fans) { minPercent[fan.Id] = null; hasSignal[fan.Id] = false; }

        // Phase 1: alle Lüfter gemeinsam hochfahren, pro Stufe schauen, welche schon ein Signal liefern.
        for (int i = 0; i < RampSteps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            int step = RampSteps[i];
            foreach (var fan in fans)
                if (minPercent[fan.Id] is null)
                    _source.SetPercent(fan.Id, step);
            progress?.Report(new CalibrationProgress(CalibrationPhase.MinSignal,
                $"Teste Ansteuerung {step} % ({fans.Count(f => minPercent[f.Id] is null)} Lüfter noch ohne Signal) …",
                (double)i / RampSteps.Length * 0.4));
            await _delay(StepSettleTime, ct);

            var readings = _source.Detect().ToDictionary(f => f.Id);
            foreach (var fan in fans)
            {
                if (minPercent[fan.Id] is not null) continue;
                if (readings.TryGetValue(fan.Id, out var r) && r.Rpm is { } rpm && rpm > 0)
                {
                    minPercent[fan.Id] = step;
                    hasSignal[fan.Id] = true;
                }
            }
            if (minPercent.Values.All(v => v is not null)) break;
        }
        // Lüfter, die bis 100 % kein Signal zeigten, bleiben in minPercent/hasSignal auf null/false.

        // Phase 2: alle (mit Signal) auf ihr Minimum, mind. 50 % — dabei CPU-/Board-Temperatur über die Zeit beobachten.
        progress?.Report(new CalibrationProgress(CalibrationPhase.ThermalResponse,
            "Stelle Lüfter auf das gemessene Minimum und beobachte die Temperatur …", 0.4));
        foreach (var fan in fans)
        {
            int target = Math.Max(50, minPercent[fan.Id] ?? 50);
            _source.SetPercent(fan.Id, target);
        }
        ct.ThrowIfCancellationRequested();
        await _delay(BaselineSettleTime, ct);

        double cpuBefore = SafeRead(_readCpuTemp);
        double boardBefore = SafeRead(_readBoardTemp);
        var window = ThermalWindow;
        var waited = TimeSpan.Zero;
        var step2 = TimeSpan.FromSeconds(5);
        while (waited < window)
        {
            ct.ThrowIfCancellationRequested();
            var slice = step2 < window - waited ? step2 : window - waited;
            await _delay(slice, ct);
            waited += slice;
            progress?.Report(new CalibrationProgress(CalibrationPhase.ThermalResponse,
                $"Beobachte Temperatur bei fester Drehzahl ({waited.TotalSeconds:0}/{window.TotalSeconds:0} s) …",
                0.4 + 0.5 * (waited / window)));
        }
        double cpuAfter = SafeRead(_readCpuTemp);
        double boardAfter = SafeRead(_readBoardTemp);
        double cpuDriftPerMin = (cpuAfter - cpuBefore) / Math.Max(1, window.TotalMinutes);
        // Steigt die Temperatur trotz fester Mindestdrehzahl deutlich, ist wenig Spielraum zum Senken der
        // Obergrenze — die Kurve soll dann früher hochfahren (niedrigere Decke in GenerateEfficient/Quiet).
        double ceilingAdjust = Math.Clamp(cpuDriftPerMin * 2, -5, 10);
        double cpuCeiling = CpuSafeCeilingC - ceilingAdjust;

        var result = new List<FanProfileConfig>();
        foreach (var fan in fans)
        {
            var profile = existing.FirstOrDefault(f => f.FanId == fan.Id) ?? new FanProfileConfig { FanId = fan.Id, DisplayName = fan.Name };
            profile.DisplayName = fan.Name;
            profile.Calibrated = true;
            profile.LastCalibrationUtc = DateTime.UtcNow;
            profile.HasRpmSignal = hasSignal[fan.Id];
            profile.MeasuredMinPercent = minPercent[fan.Id];

            if (!hasSignal[fan.Id])
            {
                // kein Drehzahlsignal -> kann nicht kalibriert werden, bleibt beim Grundprofil.
                profile.Mode = CoolingFanMode.Curve;
                if (profile.Curve.Count == 0) profile.Curve = FanCurve.Default();
            }
            else
            {
                int floor = Math.Max(10, minPercent[fan.Id]!.Value);
                profile.Mode = CoolingFanMode.SmartEfficient;
                profile.Curve = FanCurve.GenerateEfficient(floor, cpuCeiling);
            }
            result.Add(profile);
        }

        // Lüfter wieder auf ihre Kurve bei der aktuellen Temperatur bringen (statt auf dem Test-Minimum stehenzulassen).
        foreach (var fan in fans)
        {
            var profile = result.FirstOrDefault(p => p.FanId == fan.Id);
            if (profile is null) continue;
            int pct = FanCurve.Evaluate(profile.Curve, SafeRead(_readCpuTemp));
            try { _source.SetPercent(fan.Id, pct); } catch { /* im nächsten regulären Takt erneut versucht */ }
        }

        progress?.Report(new CalibrationProgress(CalibrationPhase.Done,
            $"Kalibrierung fertig — Mindestansteuerung: {string.Join(", ", result.Select(r => r.DisplayName + " " + (r.MeasuredMinPercent?.ToString() + " %" ?? "kein Signal")))}.", 1.0));
        return result;
    }

    /// <summary>Erzeugt zusätzlich eine „Leise“-Variante der zuletzt erzeugten Kurven (ohne erneute Messung).</summary>
    public static List<FanProfileConfig> ToQuiet(IReadOnlyList<FanProfileConfig> efficientResult, double cpuSafeCeilingC)
    {
        var quiet = new List<FanProfileConfig>();
        foreach (var p in efficientResult)
        {
            var copy = new FanProfileConfig
            {
                FanId = p.FanId,
                DisplayName = p.DisplayName,
                Mode = p.HasRpmSignal ? CoolingFanMode.SmartQuiet : CoolingFanMode.Curve,
                MeasuredMinPercent = p.MeasuredMinPercent,
                HasRpmSignal = p.HasRpmSignal,
                Calibrated = p.Calibrated,
                LastCalibrationUtc = p.LastCalibrationUtc,
                Curve = p.HasRpmSignal ? FanCurve.GenerateQuiet(Math.Max(10, p.MeasuredMinPercent ?? 50), cpuSafeCeilingC) : FanCurve.Default(),
            };
            quiet.Add(copy);
        }
        return quiet;
    }

    private static double SafeRead(Func<double> read)
    {
        try { return read(); } catch { return double.NaN; }
    }
}
