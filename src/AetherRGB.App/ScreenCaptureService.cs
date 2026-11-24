using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AetherRGB.Core.Config;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Screen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AetherRGB.App;

/// <summary>Ein Monitor für die Auswahl in den Einstellungen.</summary>
public sealed record MonitorInfo(string DeviceName, int X, int Y, int Width, int Height, bool Primary)
{
    public string Label { get; init; } = DeviceName;
    public override string ToString() => Label;
}

/// <summary>Nimmt einen Monitor auf, solange ein Bildschirm-Effekt läuft (<see cref="ScreenHub.IsDemanded"/>), und gibt die
/// Bilder an den <see cref="ScreenHub"/>. Nur das fertige Bild wird gelesen — wie OBS oder Discord beim Teilen — kein Code
/// im Spiel, kein Speicherzugriff. Erst Desktop Duplication (DXGI), bei Fehlern GDI.</summary>
public sealed class ScreenCaptureService : IDisposable
{
    private readonly ScreenHub _hub;
    private readonly Func<ScreenSyncConfig> _config;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;

    private ICaptureSource? _source;
    private string? _lastAutoMonitor;
    private (string? Monitor, TaskCompletionSource<ScreenImage?> Result)? _stillRequest;
    private readonly object _stillLock = new();
    private readonly int _ownPid = Environment.ProcessId;
    private long _nextForegroundCheck;
    private readonly Dictionary<int, string?> _processNames = new();
    private string? _lastFailure;
    private long _retryAfter, _sourceCreated;

    // Statistik
    private int _frames, _unchanged;
    private double _workMs, _maxWorkMs;
    private long _statsStart = Stopwatch.GetTimestamp(), _lastStatusUpdate;

    public event Action<LogLevel, string>? Log;

    public ScreenCaptureService(ScreenHub hub, Func<ScreenSyncConfig> config)
    {
        _hub = hub;
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = "AetherRGB Bildschirmaufnahme", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Einstellungen geändert: Quelle neu aufbauen.</summary>
    public void Reconfigure()
    {
        lock (_stillLock) _forceRecreate = true;
        _wake.Set();
    }
    private bool _forceRecreate;

    /// <summary>Standbild in voller Auflösung für den Bereichs-Editor. <paramref name="monitor"/> leer = wie in den Einstellungen.</summary>
    public Task<ScreenImage?> CaptureStillAsync(string? monitor)
    {
        var tcs = new TaskCompletionSource<ScreenImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stillLock)
        {
            _stillRequest?.Result.TrySetResult(null);
            _stillRequest = (monitor, tcs);
        }
        _wake.Set();
        return tcs.Task;
    }

    // ===================== Monitore =====================

    /// <summary>Alle Monitore, links nach rechts. Koordinaten in echten Pixeln (über DXGI), sonst wie Windows sie meldet.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            foreach (var o in EnumerateOutputs())
                if (!list.Any(m => m.DeviceName == o.DeviceName))
                    list.Add(new MonitorInfo(o.DeviceName, o.X, o.Y, o.Width, o.Height, o.X == 0 && o.Y == 0));
        }
        catch { /* ohne DXGI: Forms */ }
        if (list.Count == 0)
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
                list.Add(new MonitorInfo(s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary));

        list = list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            string pos = list.Count > 1 ? (i == 0 ? ", links" : i == list.Count - 1 ? ", rechts" : ", Mitte") : "";
            list[i] = m with { Label = $"Monitor {i + 1}: {m.Width} × {m.Height}{pos}{(m.Primary ? ", Hauptmonitor" : "")}" };
        }
        return list;
    }

    private sealed record OutputInfo(string DeviceName, int X, int Y, int Width, int Height, int Adapter, int Output);

    private static List<OutputInfo> EnumerateOutputs()
    {
        var result = new List<OutputInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        result.Add(new OutputInfo(d.DeviceName, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (int)a, (int)o));
                    }
                }
            }
        }
        return result;
    }

    // ===================== Aufnahme-Schleife =====================

    private void Run()
    {
        // Echte Pixelkoordinaten auch bei unterschiedlicher Skalierung der Monitore (GDI-Weg, Fensterposition).
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { /* Windows < 10 1703 */ }

        while (!_stop)
        {
            (string? Monitor, TaskCompletionSource<ScreenImage?> Result)? still;
            bool recreate;
            lock (_stillLock)
            {
                still = _stillRequest;
                _stillRequest = null;
                recreate = _forceRecreate;
                _forceRecreate = false;
            }

            if (!_hub.IsDemanded && still is null)
            {
                if (_source is not null)
                {
                    StopSource("kein Bildschirm-Effekt mehr aktiv");
                    _hub.Status = "Aufnahme aus — kein Bildschirm-Effekt aktiv.";
                }
                _wake.WaitOne(250);
                continue;
            }

            var cfg = _config();
            int fps = Math.Clamp(cfg.MaxFps, 5, 60);
            long frameStart = Stopwatch.GetTimestamp();
            UpdateForeground();

            if (still is null && Environment.TickCount64 < _retryAfter)
            {
                _wake.WaitOne(100);
                continue;
            }

            try
            {
                string monitor = !string.IsNullOrEmpty(still?.Monitor) ? still.Value.Monitor! : ResolveMonitor(cfg);
                // Automatisch auf GDI ausgewichen (z. B. weil gerade die UAC-Abfrage lief): alle 30 s DXGI erneut versuchen.
                bool retryDxgi = _source is GdiSource && cfg.Method == ScreenCaptureMethod.Auto && Environment.TickCount64 - _sourceCreated > 30_000;
                if (recreate || retryDxgi || still is not null || _source is null || _source.DeviceName != monitor)
                {
                    _source?.Dispose();
                    _source = null;
                    _source = CreateSource(monitor, cfg.Method, quiet: retryDxgi);
                    _sourceCreated = Environment.TickCount64;
                }

                if (still is not null)
                {
                    var full = _source.Capture(1, 500, force: true);
                    still.Value.Result.TrySetResult(full);
                    continue;
                }

                int timeout = Math.Max(1, 1000 / fps);
                var image = _source.Capture(ScreenImage.FactorFor(_source.Width), timeout, force: false);
                if (image is null)
                {
                    _unchanged++;
                    _hub.Heartbeat();
                }
                else
                {
                    _hub.Publish(image);
                    _frames++;
                    double ms = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                    _workMs += ms;
                    _maxWorkMs = Math.Max(_maxWorkMs, ms);
                }
                _lastFailure = null;
                UpdateStats(fps);
            }
            catch (Exception ex)
            {
                still?.Result.TrySetResult(null);
                var text = ExceptionText.Describe(ex);
                if (text != _lastFailure) Log?.Invoke(LogLevel.Warning, $"Bildschirmaufnahme: {text} — neuer Versuch gleich.");
                _lastFailure = text;
                _hub.Status = "Aufnahme gestört: " + text;
                _source?.Dispose();
                _source = null;
                _hub.Clear();
                _retryAfter = Environment.TickCount64 + 1000;
                continue;
            }

            // Takt: höchstens fps Bilder pro Sekunde (DXGI wartet selbst bis zum nächsten Bild, GDI nicht).
            double elapsed = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            int rest = (int)(1000.0 / fps - elapsed);
            if (rest > 0) _wake.WaitOne(rest);
        }
        StopSource(null);
    }

    private void StopSource(string? reason)
    {
        if (_source is null) return;
        _source.Dispose();
        _source = null;
        _hub.Clear();
        if (reason is not null) Log?.Invoke(LogLevel.Debug, $"Bildschirmaufnahme beendet ({reason}).");
    }

    private ICaptureSource CreateSource(string monitor, ScreenCaptureMethod method, bool quiet = false)
    {
        var outputs = EnumerateOutputs();
        var output = outputs.FirstOrDefault(o => o.DeviceName == monitor) ?? outputs.FirstOrDefault(o => o.X == 0 && o.Y == 0) ?? outputs.FirstOrDefault()
                     ?? throw new InvalidOperationException("Kein Monitor gefunden.");
        string label = GetMonitors().FirstOrDefault(m => m.DeviceName == output.DeviceName)?.Label ?? output.DeviceName;

        if (method != ScreenCaptureMethod.Gdi)
        {
            try
            {
                var dx = new DxgiSource(output);
                Log?.Invoke(LogLevel.Info, $"Bildschirmaufnahme: {label} ({output.DeviceName}) per Desktop Duplication.");
                dx.Label = label;
                return dx;
            }
            catch (Exception ex) when (method == ScreenCaptureMethod.Auto)
            {
                if (quiet) return new GdiSource(output) { Label = label };
                Log?.Invoke(LogLevel.Warning, $"Desktop Duplication für {label} nicht möglich ({ExceptionText.Describe(ex)}) — nehme GDI. " +
                                              "Bei exklusivem Vollbild kann das Schwarz zeigen; dann im Spiel „Fenstermodus-Vollbild“ wählen.");
            }
        }
        var gdi = new GdiSource(output) { Label = label };
        Log?.Invoke(LogLevel.Info, $"Bildschirmaufnahme: {label} ({output.DeviceName}) per GDI.");
        return gdi;
    }

    private string ResolveMonitor(ScreenSyncConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.Monitor)) return cfg.Monitor;
        // Automatisch: Monitor mit dem Fenster im Vordergrund — außer es ist AetherRGB selbst (man bedient gerade die App
        // auf dem zweiten Monitor, das Spiel läuft auf dem ersten weiter).
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != _ownPid)
            {
                var hmon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (hmon != IntPtr.Zero && GetMonitorInfo(hmon, ref info)) _lastAutoMonitor = info.szDevice;
            }
        }
        return _lastAutoMonitor ?? GetMonitors().FirstOrDefault(m => m.Primary)?.DeviceName ?? "";
    }

    private void UpdateForeground()
    {
        if (Environment.TickCount64 < _nextForegroundCheck) return;
        _nextForegroundCheck = Environment.TickCount64 + 1000;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownPid) return; // AetherRGB im Vordergrund: Profil des Spiels behalten
        if (!_processNames.TryGetValue((int)pid, out var name))
        {
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { name = null; }
            if (_processNames.Count > 256) _processNames.Clear();
            _processNames[(int)pid] = name;
        }
        _hub.SetForegroundProcess(name);
    }

    private void UpdateStats(int fps)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _statsStart) / (double)Stopwatch.Frequency;
        if (now - _lastStatusUpdate > Stopwatch.Frequency)
        {
            _lastStatusUpdate = now;
            double rate = seconds > 0 ? _frames / seconds : 0;
            _hub.Status = $"Aufnahme läuft: {_source?.Label} per {_source?.Method}, {rate:0.#} neue Bilder/s (max. {fps})" +
                          (_hub.CurrentProfile is { } p ? $", Profil „{p.Name}“" : "") + ".";
        }
        if (seconds < 60) return;
        Log?.Invoke(LogLevel.Debug,
            $"Bildschirmaufnahme: {_frames / seconds:0.#} neue Bilder/s, {_unchanged / seconds:0.#}/s unverändert, " +
            $"Aufnahme+Analyse Ø {(_frames > 0 ? _workMs / _frames : 0):0.0} ms, max. {_maxWorkMs:0.0} ms ({_source?.Method}, {_source?.Label}).");
        _frames = _unchanged = 0;
        _workMs = _maxWorkMs = 0;
        _statsStart = now;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        lock (_stillLock) _stillRequest?.Result.TrySetResult(null);
        _wake.Dispose();
    }

    // ===================== Quellen =====================

    private interface ICaptureSource : IDisposable
    {
        string DeviceName { get; }
        string Label { get; set; }
        string Method { get; }
        int Width { get; }
        /// <summary>Neues Bild, verkleinert um <paramref name="factor"/>; null = Bild unverändert (nur ohne <paramref name="force"/>).</summary>
        ScreenImage? Capture(int factor, int timeoutMs, bool force);
    }

    /// <summary>Desktop Duplication: bekommt vom Grafiktreiber nur geänderte Bilder, funktioniert auch bei den meisten
    /// Vollbild-Spielen. Ein HDR-Monitor kann die Farben verfälschen.</summary>
    private sealed class DxgiSource : ICaptureSource
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private ID3D11Texture2D? _staging;
        private bool _haveFrame;

        public string DeviceName { get; }
        public string Label { get; set; } = "";
        public string Method => "Desktop Duplication";
        public int Width { get; }
        public int Height { get; }

        public DxgiSource(OutputInfo info)
        {
            DeviceName = info.DeviceName;
            Width = info.Width;
            Height = info.Height;
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)info.Adapter, out var adapter).CheckError();
            using (adapter)
            {
                adapter.EnumOutputs((uint)info.Output, out var output).CheckError();
                using (output)
                {
                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                        out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                    _device = device!;
                    _context = context!;
                    try
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _duplication = output1.DuplicateOutput(_device);
                    }
                    catch
                    {
                        _context.Dispose();
                        _device.Dispose();
                        throw;
                    }
                }
            }
        }

        public unsafe ScreenImage? Capture(int factor, int timeoutMs, bool force)
        {
            var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                if (force) throw new TimeoutException("Kein Bild vom Monitor erhalten.");
                return null;
            }
            result.CheckError(); // AccessLost (Auflösung/Vollbild gewechselt, Sperrbildschirm) → neu aufbauen

            try
            {
                // Nur der Mauszeiger hat sich bewegt — das Bild ist gleich.
                if (frameInfo.LastPresentTime == 0 && _haveFrame && !force) return null;

                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                var desc = texture.Description;
                if (desc.Format != Format.B8G8R8A8_UNorm && desc.Format != Format.B8G8R8A8_UNorm_SRgb)
                    throw new NotSupportedException($"Bildformat {desc.Format} (HDR?) wird nicht unterstützt.");

                if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                {
                    _staging?.Dispose();
                    _staging = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None, MiscFlags = ResourceOptionFlags.None,
                    });
                }
                _context.CopyResource(_staging, texture);
                _haveFrame = true;
            }
            finally
            {
                resource?.Dispose();
                _duplication.ReleaseFrame();
            }

            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int w = (int)_staging!.Description.Width, h = (int)_staging.Description.Height;
                var span = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)mapped.RowPitch * h);
                return ScreenImage.Downsample(span, w, h, (int)mapped.RowPitch, factor);
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }

        public void Dispose()
        {
            _staging?.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>GDI: kopiert den Monitorbereich vom Desktop (beim Verkleinern gleich mit Glättung). Langsamer, liefert immer
    /// ein Bild, bei exklusivem Vollbild aber oft Schwarz.</summary>
    private sealed class GdiSource : ICaptureSource
    {
        private readonly OutputInfo _info;
        private IntPtr _memDc, _bitmap, _oldBitmap, _bits;
        private int _bw, _bh;

        public string DeviceName => _info.DeviceName;
        public string Label { get; set; } = "";
        public string Method => "GDI";
        public int Width => _info.Width;

        public GdiSource(OutputInfo info) => _info = info;

        public ScreenImage? Capture(int factor, int timeoutMs, bool force)
        {
            int w = Math.Max(1, _info.Width / factor), h = Math.Max(1, _info.Height / factor);
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC fehlgeschlagen.");
            try
            {
                if (_memDc == IntPtr.Zero || w != _bw || h != _bh) CreateBuffer(screen, w, h);
                bool ok = factor == 1
                    ? BitBlt(_memDc, 0, 0, w, h, screen, _info.X, _info.Y, 0x00CC0020 /* SRCCOPY */)
                    : StretchFrom(screen, w, h);
                if (!ok) throw new InvalidOperationException($"BitBlt fehlgeschlagen (Fehler {Marshal.GetLastWin32Error()}).");
                GdiFlush();
                var pixels = new byte[w * h * 4];
                Marshal.Copy(_bits, pixels, 0, pixels.Length);
                return new ScreenImage(w, h, pixels, _info.Width, _info.Height);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private bool StretchFrom(IntPtr screen, int w, int h)
        {
            SetStretchBltMode(_memDc, 4 /* HALFTONE */);
            SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
            return StretchBlt(_memDc, 0, 0, w, h, screen, _info.X, _info.Y, _info.Width, _info.Height, 0x00CC0020);
        }

        private void CreateBuffer(IntPtr screen, int w, int h)
        {
            FreeBuffer();
            _memDc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, // negativ = von oben nach unten
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _bitmap = CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection fehlgeschlagen.");
            _oldBitmap = SelectObject(_memDc, _bitmap);
            _bw = w;
            _bh = h;
        }

        private void FreeBuffer()
        {
            if (_memDc != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
                DeleteDC(_memDc);
            }
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            _memDc = _bitmap = _bits = IntPtr.Zero;
        }

        public void Dispose() => FreeBuffer();
    }

    // ===================== Win32 =====================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int rcMonitorLeft, rcMonitorTop, rcMonitorRight, rcMonitorBottom;
        public int rcWorkLeft, rcWorkTop, rcWorkRight, rcWorkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
