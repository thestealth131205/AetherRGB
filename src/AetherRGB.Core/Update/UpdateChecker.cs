using System.Net.Http.Headers;
using System.Text.Json;

namespace AetherRGB.Core.Update;

public sealed record ReleaseInfo(string Version, string DownloadUrl, string ReleaseNotesUrl);

/// <summary>Prüft ausschließlich gegen die öffentliche GitHub-Releases-API — kein eigener
/// Update-Server, kein Telemetrie-Call, keine Daten über diesen Rechner werden mitgeschickt.</summary>
public sealed class UpdateChecker
{
    private const string RepoApiUrl = "https://api.github.com/repos/thestealth131205/AetherRGB/releases/latest";
    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AetherRGB", "1.0"));
    }

    public async Task<ReleaseInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(RepoApiUrl, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (string.IsNullOrEmpty(tag) || !IsNewer(tag, currentVersion)) return null;

        string? downloadUrl = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe"))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        downloadUrl ??= doc.RootElement.GetProperty("html_url").GetString() ?? "";

        return new ReleaseInfo(tag, downloadUrl, doc.RootElement.GetProperty("html_url").GetString() ?? "");
    }

    private static bool IsNewer(string remote, string local)
    {
        if (!Version.TryParse(Normalize(remote), out var r)) return false;
        if (!Version.TryParse(Normalize(local), out var l)) return true;
        return r > l;
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('.');
        return parts.Length switch
        {
            1 => $"{v}.0.0.0",
            2 => $"{v}.0.0",
            3 => $"{v}.0",
            _ => v
        };
    }
}
