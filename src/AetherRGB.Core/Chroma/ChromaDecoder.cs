using System.Buffers.Binary;
using System.Text.Json;
using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Chroma;

/// <summary>Wandelt Chroma-Effekte in <see cref="ChromaFrame"/>s um — einmal die rohen Parameter-Structs aus
/// dem nativen SDK (über die Bridge-DLL), einmal das JSON der Chroma-REST-API. Effekttypen, die ohne
/// Tastendrücke nichts zeigen (CHROMA_REACTIVE), ergeben „aus“; unbekannte ergeben null (= ignorieren).</summary>
public static class ChromaDecoder
{
    private const uint KeyOverrideFlag = 0x01000000;

    // ===================== Natives SDK (RzChromaSDKTypes.h) =====================

    /// <summary>Rohes Parameter-Struct eines Create…Effect-Aufrufs. Die Effekttyp-Nummern sind je Gerät
    /// verschieden (wie im SDK), die Struct-Größen prüft schon die Bridge — hier noch einmal, weil die
    /// Pakete von jedem lokalen Programm kommen können.</summary>
    public static ChromaFrame? DecodeNative(ChromaDevice device, int type, ReadOnlySpan<byte> p)
    {
        try
        {
            return device switch
            {
                ChromaDevice.Keyboard => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => Breathing(p, 0),
                    2 => Grid(p, 0, 22, 6),
                    3 => ChromaFrame.Off,
                    4 => ChromaFrame.Solid(Color(p, 0)),
                    5 => ChromaFrame.Spectrum(),
                    6 => Wave(p, 0),
                    8 => KeyGrid(p),
                    9 => ExtendedKeyGrid(p),
                    _ => null,
                },
                ChromaDevice.Mouse => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => ChromaFrame.Blinking(Color(p, 4)),
                    2 => Breathing(p, 4),
                    3 => Grid(p, 0, 30, 1),
                    4 => ChromaFrame.Off,
                    5 => ChromaFrame.Spectrum(),
                    6 => ChromaFrame.Solid(Color(p, 4)),
                    7 => Wave(p, 0),
                    8 => Grid(p, 0, 7, 9),
                    _ => null,
                },
                ChromaDevice.Headset => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => ChromaFrame.Solid(Color(p, 0)),
                    2 => ChromaFrame.Breathing(Color(p, 0), RgbColor.Black),
                    3 => ChromaFrame.Spectrum(),
                    4 => Grid(p, 0, 5, 1),
                    _ => null,
                },
                ChromaDevice.Mousepad => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => Breathing(p, 0),
                    2 => Grid(p, 0, 15, 1),
                    3 => ChromaFrame.Spectrum(),
                    4 => ChromaFrame.Solid(Color(p, 0)),
                    5 => Wave(p, 0),
                    6 => Grid(p, 0, 20, 1),
                    _ => null,
                },
                ChromaDevice.Keypad => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => Breathing(p, 0),
                    2 => Grid(p, 0, 5, 4),
                    3 => ChromaFrame.Off,
                    4 => ChromaFrame.Spectrum(),
                    5 => ChromaFrame.Solid(Color(p, 0)),
                    6 => Wave(p, 0),
                    _ => null,
                },
                ChromaDevice.ChromaLink => type switch
                {
                    0 => ChromaFrame.Off,
                    1 => Grid(p, 0, 5, 1),
                    2 => ChromaFrame.Solid(Color(p, 0)),
                    _ => null,
                },
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // Paket zu kurz für den Effekttyp
        }
    }

    private static RgbColor Color(ReadOnlySpan<byte> p, int offset) =>
        ChromaFrame.FromColorRef(BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(offset, 4)));

    private static ChromaFrame Grid(ReadOnlySpan<byte> p, int offset, int width, int height)
    {
        var colors = new RgbColor[width * height];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color(p, offset + i * 4);
        return ChromaFrame.Grid(width, height, colors);
    }

    /// <summary>CUSTOM_KEY_EFFECT_TYPE { COLORREF Color[6][22]; COLORREF Key[6][22]; } — ein Key-Wert mit
    /// gesetztem Bit 0x01000000 überdeckt die Farbe dieser Taste.</summary>
    private static ChromaFrame KeyGrid(ReadOnlySpan<byte> p)
    {
        var colors = new RgbColor[6 * 22];
        for (int i = 0; i < colors.Length; i++)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice((132 + i) * 4, 4));
            colors[i] = (key & KeyOverrideFlag) != 0 ? ChromaFrame.FromColorRef(key) : Color(p, i * 4);
        }
        return ChromaFrame.Grid(22, 6, colors);
    }

    /// <summary>CUSTOM_EFFECT_TYPE2 { COLORREF Color[8][24]; COLORREF Key[6][22]; } — erweitertes Raster mit
    /// einer Zeile/Spalte Rand; die Tasten liegen darin ab Zeile 1, Spalte 1.</summary>
    private static ChromaFrame ExtendedKeyGrid(ReadOnlySpan<byte> p)
    {
        var colors = new RgbColor[8 * 24];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color(p, i * 4);
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 22; c++)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice((192 + r * 22 + c) * 4, 4));
                if ((key & KeyOverrideFlag) != 0) colors[(r + 1) * 24 + c + 1] = ChromaFrame.FromColorRef(key);
            }
        return ChromaFrame.Grid(24, 8, colors);
    }

    /// <summary>BREATHING_EFFECT_TYPE { Type; COLORREF Color1; COLORREF Color2; } (Maus: LED-ID davor).
    /// Typ „zufällige Farben“ zeigen wir als Farbkreis.</summary>
    private static ChromaFrame Breathing(ReadOnlySpan<byte> p, int offset)
    {
        int type = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(offset, 4));
        bool mouse = offset == 4; // Maus: ONE_COLOR=1, TWO_COLORS=2, RANDOM_COLORS=3; sonst TWO_COLORS=1, RANDOM_COLORS=2
        if (type == (mouse ? 3 : 2)) return ChromaFrame.Spectrum();
        return ChromaFrame.Breathing(Color(p, offset + 4), mouse && type == 1 ? RgbColor.Black : Color(p, offset + 8));
    }

    /// <summary>WAVE_EFFECT_TYPE { Direction } — 1 = links nach rechts, 2 = rechts nach links.</summary>
    private static ChromaFrame Wave(ReadOnlySpan<byte> p, int offset) =>
        ChromaFrame.Wave(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(offset, 4)) == 2 ? -1 : 1);

    // ===================== REST-API (http://localhost:54235) =====================

    public static bool TryParseDevice(string name, out ChromaDevice device)
    {
        switch (name.ToLowerInvariant())
        {
            case "keyboard": device = ChromaDevice.Keyboard; return true;
            case "mouse": device = ChromaDevice.Mouse; return true;
            case "headset": device = ChromaDevice.Headset; return true;
            case "mousepad": device = ChromaDevice.Mousepad; return true;
            case "keypad": device = ChromaDevice.Keypad; return true;
            case "chromalink": device = ChromaDevice.ChromaLink; return true;
            default: device = default; return false;
        }
    }

    /// <summary>Ein Effekt aus der REST-API: <c>{"effect":"CHROMA_CUSTOM","param":[[…]]}</c>. Die Rastergröße
    /// ergibt sich aus dem JSON (Tastatur 6×22, Maus 9×7, Mauspad 15 …), nicht aus dem Gerät — so funktionieren
    /// auch neuere Varianten wie CHROMA_CUSTOM2.</summary>
    public static ChromaFrame? DecodeRest(ChromaDevice device, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("effect", out var effectEl) || effectEl.ValueKind != JsonValueKind.String)
            return null;
        body.TryGetProperty("param", out var param);
        switch (effectEl.GetString())
        {
            case "CHROMA_NONE":
                return ChromaFrame.Off;
            case "CHROMA_STATIC":
                return param.ValueKind == JsonValueKind.Object && param.TryGetProperty("color", out var c) && TryColor(c, out var color)
                    ? ChromaFrame.Solid(color) : null;
            case "CHROMA_CUSTOM":
            case "CHROMA_CUSTOM2":
            case "CHROMA_CUSTOM_KEY":
                if (param.ValueKind == JsonValueKind.Array) return JsonGrid(param, default);
                if (param.ValueKind == JsonValueKind.Object && param.TryGetProperty("color", out var grid))
                    return JsonGrid(grid, param.TryGetProperty("key", out var keys) ? keys : default);
                return null;
            default:
                return null;
        }
    }

    private const int MaxRestCells = 64 * 64;

    private static ChromaFrame? JsonGrid(JsonElement colors, JsonElement keys)
    {
        if (colors.ValueKind != JsonValueKind.Array) return null;
        int rows, cols;
        bool twoD = colors.GetArrayLength() > 0 && colors[0].ValueKind == JsonValueKind.Array;
        if (twoD) { rows = colors.GetArrayLength(); cols = colors[0].GetArrayLength(); }
        else { rows = 1; cols = colors.GetArrayLength(); }
        if (rows == 0 || cols == 0 || rows * cols > MaxRestCells) return null;

        var result = new RgbColor[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            var row = twoD ? colors[r] : colors;
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols) return null;
            for (int col = 0; col < cols; col++)
            {
                if (!TryColor(row[col], out var color)) return null;
                result[r * cols + col] = color;
            }
        }

        // Tasten-Überlagerung (CHROMA_CUSTOM_KEY): gleiche Rastergröße, Bit 0x01000000 = Taste gesetzt.
        if (keys.ValueKind == JsonValueKind.Array && twoD && keys.GetArrayLength() == rows)
        {
            for (int r = 0; r < rows; r++)
            {
                var row = keys[r];
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols) continue;
                for (int col = 0; col < cols; col++)
                    if (row[col].TryGetInt64(out long key) && (key & KeyOverrideFlag) != 0)
                        result[r * cols + col] = ChromaFrame.FromColorRef((uint)key);
            }
        }
        return ChromaFrame.Grid(cols, rows, result);
    }

    private static bool TryColor(JsonElement el, out RgbColor color)
    {
        color = RgbColor.Black;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out long v)) return false;
        color = ChromaFrame.FromColorRef((uint)v);
        return true;
    }
}
