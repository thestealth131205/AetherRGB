using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Chroma;

/// <summary>Empfängt die UDP-Pakete der Bridge-DLL (<c>native/ChromaBridge</c>), die Spiele statt der
/// Razer-RzChromaSDK64.dll laden. Lauscht nur auf 127.0.0.1. Paketformat:
/// <c>"AECB" | u8 Version | u8 Art | u8 Gerät | u8 0 | u32 PID | i32 Effekttyp | u32 Länge | Nutzdaten</c>.</summary>
public sealed class ChromaBridgeListener : IDisposable
{
    /// <summary>Fest in der DLL eingebaut — nicht konfigurierbar.</summary>
    public const int Port = 34535;
    public const string SourceName = "Chroma SDK";

    private const int HeaderSize = 20;
    private const int MaxPayload = 2048;
    private const byte KindInit = 1, KindUnInit = 2, KindEffect = 3;

    private readonly ChromaHub _hub;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Timer? _pruneTimer;
    private int _rejected;

    public ChromaBridgeListener(ChromaHub hub) => _hub = hub;

    public event Action<LogLevel, string>? Log;

    public bool IsRunning => _udp is not null;

    /// <summary>Startet den Empfang. Wirft <see cref="SocketException"/>, wenn der Port belegt ist.</summary>
    public void Start()
    {
        if (_udp is not null) return;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        if (OperatingSystem.IsWindows())
        {
            // Ohne das beendet ein ICMP "Port unreachable" (z. B. nach einem gesendeten Paket) jeden
            // weiteren Empfang mit WSAECONNRESET.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch (SocketException) { }
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(_udp, token));
        _pruneTimer = new Timer(_ => _hub.Prune(IsProcessAlive, _ => null), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        _pruneTimer?.Dispose();
        _pruneTimer = null;
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
        _hub.CloseAll(SourceName);
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            try { Handle(result.Buffer); }
            catch (Exception ex) { Log?.Invoke(LogLevel.Warning, "Chroma-Paket fehlerhaft: " + ExceptionText.Describe(ex)); }
        }
    }

    /// <summary>Wertet ein Paket aus. Öffentlich für Tests.</summary>
    public void Handle(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize || !packet[..4].SequenceEqual("AECB"u8) || packet[4] != 1)
        {
            if (Interlocked.Increment(ref _rejected) == 1)
                Log?.Invoke(LogLevel.Warning, "Unbekanntes Paket auf dem Chroma-Port verworfen (weitere werden nicht gemeldet).");
            return;
        }
        byte kind = packet[5], device = packet[6];
        int pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        int effectType = BinaryPrimitives.ReadInt32LittleEndian(packet[12..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(packet[16..]);
        if (length > MaxPayload || packet.Length != HeaderSize + length) return;
        var payload = packet.Slice(HeaderSize, (int)length);
        string key = "sdk:" + pid;

        switch (kind)
        {
            case KindInit:
                _hub.Open(key, payload.Length >= 2 ? Encoding.Unicode.GetString(payload[..(payload.Length & ~1)]) : "PID " + pid, SourceName, pid);
                break;
            case KindUnInit:
                _hub.Close(key);
                break;
            case KindEffect:
                if (!Enum.IsDefined(typeof(ChromaDevice), device)) return;
                // Läuft das Spiel schon länger als AetherRGB, fehlt das Init-Paket — dann mit dem Prozessnamen anmelden.
                if (!_hub.IsOpen(key) && !_hub.Open(key, ProcessName(pid), SourceName, pid)) return;
                if (ChromaDecoder.DecodeNative((ChromaDevice)device, effectType, payload) is { } frame)
                    _hub.SetFrame(key, (ChromaDevice)device, frame);
                break;
        }
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
        catch (Exception) { return "PID " + pid; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }      // läuft nicht mehr
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // geschützter Prozess (Anti-Cheat): läuft, aber kein Zugriff
    }

    public void Dispose() => Stop();
}
