using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace AetherRGB.Providers.Wled;

/// <summary>Findet WLED-Geräte im lokalen Netz per mDNS (WLED meldet sich als <c>_wled._tcp.local</c>).
/// Reiner Multicast im eigenen LAN (224.0.0.251:5353) — nichts verlässt das Heimnetz.
/// Jeder Antwortende wird danach per <c>/json/info</c> bestätigt, damit nur echte WLED-Geräte erscheinen.</summary>
public static class WledDiscovery
{
    public sealed record FoundDevice(string Host, string Name, int LedCount);

    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<FoundDevice>> DiscoverAsync(TimeSpan listenTime, CancellationToken ct = default)
    {
        var responders = new HashSet<IPAddress>();
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var query = BuildPtrQuery("_wled._tcp.local");
            await udp.SendAsync(query, query.Length, MdnsEndpoint);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(listenTime);
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    // Antworten auf unsere Unicast-Quelle stammen von Geräten, die den Dienst anbieten —
                    // die Quelladresse ist die IP des WLED-Controllers.
                    if (ContainsAscii(result.Buffer, "_wled"))
                        responders.Add(result.RemoteEndPoint.Address);
                }
            }
            catch (OperationCanceledException)
            {
                // Lauschzeit vorbei.
            }
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var checks = responders.Select(async ip =>
        {
            try
            {
                var info = await http.GetFromJsonAsync<InfoResponse>($"http://{ip}/json/info", ct);
                return info is null ? null : new FoundDevice(ip.ToString(), info.Name ?? ip.ToString(), info.Leds?.Count ?? 0);
            }
            catch
            {
                return null;
            }
        });
        return (await Task.WhenAll(checks)).OfType<FoundDevice>().ToList();
    }

    /// <summary>Minimale DNS-Anfrage: 1 Frage, Typ PTR, Klasse IN mit gesetztem "unicast response"-Bit,
    /// damit die Antwort direkt an unseren Port kommt (wir lauschen nicht auf 5353).</summary>
    private static byte[] BuildPtrQuery(string name)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0x00, 0x0C, 0x80, 0x01 });
        return bytes.ToArray();
    }

    private static bool ContainsAscii(byte[] buffer, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        return buffer.AsSpan().IndexOf(needle) >= 0;
    }

    private sealed class InfoResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("leds")] public LedsInfo? Leds { get; set; }
    }

    private sealed class LedsInfo
    {
        [JsonPropertyName("count")] public int Count { get; set; }
    }
}
