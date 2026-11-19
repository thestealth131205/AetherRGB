namespace AetherRGB.Core.Devices;

/// <summary>Hält alle aktiven Provider und bündelt Geräteabfragen/-steuerung providerübergreifend.
/// Ein einzelner nicht erreichbarer Provider (z. B. OpenRGB läuft nicht, WLED offline) darf die
/// anderen nie beeinträchtigen — jeder Fehler wird isoliert geloggt, nie weitergeworfen.</summary>
public sealed class DeviceRegistry : IAsyncDisposable
{
    private readonly List<IDeviceProvider> _providers = new();
    public event Action<string, Exception>? ProviderError;

    public void Register(IDeviceProvider provider) => _providers.Add(provider);

    public IReadOnlyList<IDeviceProvider> Providers => _providers;

    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                await provider.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                ProviderError?.Invoke(provider.ProviderId, ex);
            }
        }
    }

    public async Task<IReadOnlyList<RgbDevice>> GetAllDevicesAsync(CancellationToken ct = default)
    {
        var all = new List<RgbDevice>();
        foreach (var provider in _providers)
        {
            if (!provider.IsConnected) continue;
            try
            {
                all.AddRange(await provider.GetDevicesAsync(ct));
            }
            catch (Exception ex)
            {
                ProviderError?.Invoke(provider.ProviderId, ex);
            }
        }
        return all;
    }

    public IDeviceProvider? FindProviderFor(string deviceId) =>
        deviceId.Contains(':') ? _providers.FirstOrDefault(p => deviceId.StartsWith(p.ProviderId + ":", StringComparison.OrdinalIgnoreCase)) : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }
}
