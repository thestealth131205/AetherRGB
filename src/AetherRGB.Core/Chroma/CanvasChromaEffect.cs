using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Chroma;

/// <summary>Zeigt, was ein Spiel per Razer Chroma schickt (<see cref="ChromaHub.Active"/>). Ohne verbundenes
/// Spiel läuft die Regenbogen-Welle, damit man sieht, dass das Effekt-Fenster aktiv ist.</summary>
public sealed class CanvasChromaEffect : ICanvasEffect
{
    public enum Mode
    {
        /// <summary>Das Bild der Tastatur (6 × 22 Tasten) über die ganze Fläche gelegt — Geräte zeigen den
        /// Ausschnitt, der an ihrer Stelle liegt. Ohne Tastatur-Effekt das nächstbeste Gerät.</summary>
        Keyboard,
        /// <summary>Eine Mischfarbe für alle LEDs, bevorzugt aus Chroma Link (für Umgebungslicht gedacht).</summary>
        Ambient,
    }

    private static readonly ChromaDevice[] KeyboardOrder =
        { ChromaDevice.Keyboard, ChromaDevice.Keypad, ChromaDevice.Mouse, ChromaDevice.Mousepad, ChromaDevice.ChromaLink, ChromaDevice.Headset };
    private static readonly ChromaDevice[] AmbientOrder =
        { ChromaDevice.ChromaLink, ChromaDevice.Mousepad, ChromaDevice.Headset, ChromaDevice.Keyboard, ChromaDevice.Mouse, ChromaDevice.Keypad };

    private readonly ChromaHub _hub;
    private readonly Mode _mode;
    private readonly CanvasRainbowWaveEffect _idle = new();

    public CanvasChromaEffect(Mode mode, ChromaHub? hub = null)
    {
        _mode = mode;
        _hub = hub ?? ChromaHub.Shared;
    }

    public string Name => _mode == Mode.Keyboard ? "Spiel (Razer Chroma) – Tastaturbild" : "Spiel (Razer Chroma) – Mischfarbe";

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        var active = _hub.Active;
        if (active is null) return _idle.Sample(x, y, canvasWidth, canvasHeight, elapsed);

        foreach (var device in _mode == Mode.Keyboard ? KeyboardOrder : AmbientOrder)
        {
            if (!active.Frames.TryGetValue(device, out var frame)) continue;
            if (_mode == Mode.Ambient) return frame.Average(elapsed);
            double u = canvasWidth <= 0 ? 0 : x / canvasWidth, v = canvasHeight <= 0 ? 0 : y / canvasHeight;
            return frame.Sample(u, v, elapsed);
        }
        return RgbColor.Black; // Spiel verbunden, hat aber noch nichts gesetzt
    }
}
