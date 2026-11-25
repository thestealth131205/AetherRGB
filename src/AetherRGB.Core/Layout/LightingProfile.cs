using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Layout;

/// <summary>Tastenkombination für einen globalen Hotkey. <see cref="Modifiers"/> benutzt dieselben Bits wie
/// Windows' <c>RegisterHotKey</c> (Alt = 1, Strg = 2, Umschalt = 4, Win = 8), <see cref="VirtualKey"/> ist der
/// Windows-Tastencode — so bleibt der Kern frei von WPF und die Datei lesbar genug.</summary>
public sealed record ProfileHotkey(int Modifiers, int VirtualKey)
{
    public const int Alt = 1, Control = 2, Shift = 4, Win = 8;

    /// <summary>Ohne Strg/Alt/Win würde die Taste im Spiel ständig mit auslösen — erlaubt sind nur Kombinationen
    /// mit mindestens einem davon, oder F13–F24 (Makrotasten, die kein Spiel belegt). Umschalt allein reicht nicht.</summary>
    public bool IsUsable =>
        VirtualKey is > 0 and < 256 && !IsModifierKey(VirtualKey) &&
        ((Modifiers & (Alt | Control | Win)) != 0 || VirtualKey is >= 0x7C and <= 0x87);

    /// <summary>Strg, Alt, Umschalt, Win (links/rechts/neutral) — allein keine gültige Taste.</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
}

/// <summary>Ein gespeicherter Stand aller Effekt-Fenster: Effekte, Farben, Tempo, platzierte Geräte samt Position,
/// Helligkeit und Spiel-Anzeige-Zuweisung, synchrone Gruppe und gewähltes Fenster. Nicht enthalten sind die
/// LED-Anordnungen (die beschreiben das echte Gerät und gelten überall) und die Einstellungen.</summary>
public sealed class LightingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profil";
    public ProfileHotkey? Hotkey { get; set; }
    public List<EffectScene> Scenes { get; set; } = new();
    public string? ActiveSceneId { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Kurzbeschreibung für Listen: „3 Fenster · 7 Geräte · Regenbogen-Welle, Spiel: Fortnite“.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var effects = Scenes.Select(s => EffectCatalog.Get(s.EffectId).Name).Distinct().ToList();
            return $"{Scenes.Count} Fenster · {Scenes.Sum(s => s.Items.Count)} Geräte · {string.Join(", ", effects)}";
        }
    }
}

/// <summary>Alle Profile und welches gerade aktiv ist. Persistiert in <c>%APPDATA%\AetherRGB\profiles.json</c>.
/// Die laufenden Fenster bleiben in scenes.json — das aktive Profil ist der Stand, in den sie beim nächsten Wechsel
/// zurückgeschrieben werden (<see cref="RememberChanges"/>). Es gibt immer mindestens ein Profil, und das aktive
/// lässt sich nicht löschen: so geht beim Wechseln nie ein ungesicherter Stand verloren.</summary>
public sealed class LightingProfileStore
{
    public List<LightingProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }

    /// <summary>Beim Wechsel den jetzigen Stand der Fenster ins bisher aktive Profil übernehmen (Standard). Aus =
    /// Profile ändern sich nur über „Mit aktuellem Stand überschreiben“, ein Wechsel verwirft Änderungen.</summary>
    public bool RememberChanges { get; set; } = true;

    public ProfileHotkey? NextHotkey { get; set; }
    public ProfileHotkey? PreviousHotkey { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "profiles.json");

    [JsonIgnore]
    public LightingProfile? Active => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    /// <summary>Lädt die Profile. Fehlt die Datei (erster Start) oder ist sie defekt/leer, wird aus dem jetzigen
    /// Stand das Profil „Standard“ angelegt und aktiviert — die bisherigen Fenster gehen also nie verloren.</summary>
    public static LightingProfileStore LoadOrCreate(SceneCollection current, string? path = null)
    {
        path ??= DefaultPath;
        LightingProfileStore? store = null;
        try
        {
            if (File.Exists(path))
                store = JsonSerializer.Deserialize<LightingProfileStore>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }

        store ??= new LightingProfileStore();
        store.Profiles.RemoveAll(p => p is null);
        foreach (var p in store.Profiles)
        {
            p.Scenes ??= new();
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profil";
            if (p.Hotkey is { IsUsable: false }) p.Hotkey = null;
        }
        if (store.NextHotkey is { IsUsable: false }) store.NextHotkey = null;
        if (store.PreviousHotkey is { IsUsable: false }) store.PreviousHotkey = null;

        // Doppelte IDs (von Hand kopierte Einträge) würden Aktivieren/Löschen verwechseln.
        var seen = new HashSet<string>();
        foreach (var p in store.Profiles)
            if (!seen.Add(p.Id)) { p.Id = Guid.NewGuid().ToString("N"); seen.Add(p.Id); }

        // Aktives Profil fehlt: den jetzigen Stand nicht einem fremden Profil unterschieben, sondern sichern.
        if (store.Active is null)
        {
            var profile = store.CreateFromCurrent(store.Profiles.Count == 0 ? "Standard" : store.UniqueName("Gesicherter Stand"), current);
            store.ActiveProfileId = profile.Id;
        }
        return store;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public string UniqueName(string baseName)
    {
        if (Profiles.All(p => !string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
        for (int n = 2; ; n++)
        {
            var name = $"{baseName} {n}";
            if (Profiles.All(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    /// <summary>Neues Profil aus dem jetzigen Stand der Fenster (wird ans Ende angehängt, nicht aktiviert).</summary>
    public LightingProfile CreateFromCurrent(string name, SceneCollection current)
    {
        var profile = new LightingProfile { Name = name };
        Capture(profile, current);
        Profiles.Add(profile);
        return profile;
    }

    /// <summary>Überschreibt <paramref name="profile"/> mit dem jetzigen Stand der Fenster.</summary>
    public void Capture(LightingProfile profile, SceneCollection current)
    {
        profile.Scenes = current.CloneScenes();
        profile.ActiveSceneId = current.ActiveSceneId;
        profile.UpdatedUtc = DateTime.UtcNow;
    }

    public LightingProfile Duplicate(LightingProfile source)
    {
        var copy = new LightingProfile
        {
            Name = UniqueName(source.Name + " (Kopie)"),
            Scenes = SceneCollection.CloneScenes(source.Scenes),
            ActiveSceneId = source.ActiveSceneId
        };
        // Kopie gleich hinter dem Original, damit „nächstes Profil“ sie nicht ans Ende verbannt.
        Profiles.Insert(Profiles.IndexOf(source) + 1, copy);
        return copy;
    }

    /// <summary>Löscht ein Profil. Das aktive und das letzte verbleibende lassen sich nicht löschen.</summary>
    public bool Remove(LightingProfile profile)
    {
        if (profile.Id == ActiveProfileId || Profiles.Count <= 1) return false;
        return Profiles.Remove(profile);
    }

    public void Move(LightingProfile profile, int delta)
    {
        int index = Profiles.IndexOf(profile), target = index + delta;
        if (index < 0 || target < 0 || target >= Profiles.Count) return;
        Profiles.RemoveAt(index);
        Profiles.Insert(target, profile);
    }

    /// <summary>Wechselt zu <paramref name="profile"/>: sichert vorher (bei <see cref="RememberChanges"/>) den
    /// jetzigen Stand ins bisher aktive Profil und tauscht dann alle Fenster gegen eine Kopie des neuen aus. Die
    /// Kopie sorgt dafür, dass spätere Änderungen an den Fenstern das gespeicherte Profil erst beim nächsten Wechsel
    /// erreichen. Die synchrone Gruppe wird auf <paramref name="globalElapsed"/> ausgerichtet.</summary>
    public void Activate(LightingProfile profile, SceneCollection current, TimeSpan globalElapsed)
    {
        if (!Profiles.Contains(profile)) throw new ArgumentException("Profil gehört nicht zu dieser Liste.", nameof(profile));
        if (RememberChanges && Active is { } previous) Capture(previous, current);
        current.ReplaceScenes(SceneCollection.CloneScenes(profile.Scenes), profile.ActiveSceneId);
        current.AlignSynced(globalElapsed);
        ActiveProfileId = profile.Id;
    }

    /// <summary>Nachbar des aktiven Profils in der Liste, im Kreis (<paramref name="direction"/> +1/−1). Null, wenn
    /// es nur ein Profil gibt.</summary>
    public LightingProfile? Neighbor(int direction)
    {
        if (Profiles.Count < 2) return null;
        int index = Math.Max(0, Profiles.FindIndex(p => p.Id == ActiveProfileId));
        int count = Profiles.Count;
        return Profiles[((index + Math.Sign(direction)) % count + count) % count];
    }

    /// <summary>Profil per ID oder Name (ohne Groß-/Kleinschreibung) — für die lokale API.</summary>
    public LightingProfile? Find(string idOrName) =>
        Profiles.FirstOrDefault(p => p.Id == idOrName)
        ?? Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Wer <paramref name="hotkey"/> schon benutzt (Profilname oder „Nächstes/Vorheriges Profil“), außer
    /// dem Besitzer <paramref name="except"/> — null, wenn frei.</summary>
    public string? HotkeyUsedBy(ProfileHotkey hotkey, object? except)
    {
        foreach (var p in Profiles)
            if (!ReferenceEquals(p, except) && p.Hotkey == hotkey) return $"Profil „{p.Name}“";
        if (!ReferenceEquals(except, nameof(NextHotkey)) && NextHotkey == hotkey) return "„Nächstes Profil“";
        if (!ReferenceEquals(except, nameof(PreviousHotkey)) && PreviousHotkey == hotkey) return "„Vorheriges Profil“";
        return null;
    }
}

/// <summary>Profilsteuerung für die lokale REST-API. Die App führt den Wechsel auf dem UI-Thread aus.</summary>
public interface ILightingProfileControl
{
    IReadOnlyList<(string Id, string Name, bool Active, string Summary)> ListProfiles();

    /// <summary>Aktiviert das Profil per ID oder Name. Name des aktivierten Profils, null wenn nicht gefunden.</summary>
    Task<string?> ActivateAsync(string idOrName);

    /// <summary>Wechselt zum nächsten (+1) bzw. vorherigen (−1) Profil. Name des neuen Profils, null bei nur einem.</summary>
    Task<string?> StepAsync(int direction);
}
