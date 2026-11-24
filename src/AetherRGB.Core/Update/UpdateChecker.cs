using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AetherRGB.Core.Update;

/// <summary>Eine Datei aus einem GitHub-Release. <see cref="Sha256"/> stammt aus dem
/// <c>digest</c>-Feld der Releases-API (fehlt bei älteren Releases).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record ReleaseInfo(string Version, string ReleaseNotesUrl, ReleaseAsset? Setup, ReleaseAsset? Portable);

/// <summary>Prüft ausschließlich gegen die öffentliche GitHub-Releases-API — kein eigener
/// Update-Server, kein Telemetrie-Call, keine Daten über diesen Rechner werden mitgeschickt.</summary>
public sealed class UpdateChecker
{
    private const string RepoApiUrl = "https://api.github.com/repos/thestealth131205/AetherRGB/releases/latest";
    public const string SetupAssetName = "AetherRGB-Setup-x64.exe";
    public const string PortableAssetName = "AetherRGB-Portable-x64.exe";
    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AetherRGB", "1.0"));
    }

    /// <summary>Liefert das neueste Release, falls es neuer als <paramref name="currentVersion"/> ist,
    /// sonst <c>null</c>. Wirft bei Netzwerk-/API-Fehlern, damit ein Fehler nicht als
    /// "bereits aktuell" missverstanden wird.</summary>
    public async Task<ReleaseInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(RepoApiUrl, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub antwortet mit {(int)response.StatusCode} {response.ReasonPhrase}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (string.IsNullOrEmpty(tag) || !IsNewer(tag, currentVersion)) return null;

        ReleaseAsset? setup = null, portable = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha256 = digest["sha256:".Length..];

            var parsed = new ReleaseAsset(name, url, size, sha256);
            if (name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase)) setup = parsed;
            else if (name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase)) portable = parsed;
        }

        return new ReleaseInfo(tag, root.GetProperty("html_url").GetString() ?? "", setup, portable);
    }

    /// <summary>Lädt <paramref name="asset"/> nach <paramref name="targetPath"/>. Es wird zuerst in
    /// eine <c>.part</c>-Datei geschrieben und erst nach geprüfter Größe und SHA-256 umbenannt —
    /// ein abgebrochener oder manipulierter Download landet nie unter dem Zielnamen.</summary>
    public async Task DownloadAsync(ReleaseAsset asset, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var partPath = targetPath + ".part";
        try
        {
            using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? asset.Size;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (total > 0) progress?.Report((double)written / total);
                }

                if (asset.Size > 0 && written != asset.Size)
                    throw new IOException($"Download unvollständig ({written} von {asset.Size} Bytes).");
            }

            if (asset.Sha256 is not null)
            {
                await using var check = File.OpenRead(partPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Prüfsumme (SHA-256) stimmt nicht mit dem GitHub-Release überein — Datei verworfen.");
            }

            File.Move(partPath, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partPath); } catch { /* Aufräumen ist best effort. */ }
            throw;
        }
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
