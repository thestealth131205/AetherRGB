using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Systemweite Tastenkürzel über <c>RegisterHotKey</c> — lösen auch aus, wenn ein Spiel im Vordergrund läuft
/// und AetherRGB nur im Tray liegt. Hängt am Fensterhandle des Hauptfensters, das beim Verstecken erhalten bleibt.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, (ProfileHotkey Key, Action Action)> _registered = new();
    private readonly List<(ProfileHotkey Key, Action Action, string Owner)> _wanted = new();
    private int _nextId = 0xA000;
    private int _suspended;

    public GlobalHotkeys(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Ersetzt alle Kürzel. Liefert die, die Windows abgelehnt hat (meist schon von einem anderen
    /// Programm belegt), mit Besitzer für die Meldung.</summary>
    public IReadOnlyList<string> SetAll(IEnumerable<(ProfileHotkey Key, Action Action, string Owner)> hotkeys)
    {
        _wanted.Clear();
        _wanted.AddRange(hotkeys);
        return _suspended > 0 ? Array.Empty<string>() : RegisterWanted();
    }

    /// <summary>Während der Hotkey-Eingabe im Profil-Dialog abmelden — sonst schluckt Windows die Tastenkombination,
    /// bevor das Eingabefeld sie sieht. Verschachtelbar.</summary>
    public void Suspend()
    {
        if (_suspended++ == 0) UnregisterAll();
    }

    public void Resume()
    {
        if (_suspended == 0 || --_suspended > 0) return;
        RegisterWanted();
    }

    private IReadOnlyList<string> RegisterWanted()
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var (key, action, owner) in _wanted)
        {
            int id = _nextId++;
            if (_nextId > 0xBFFF) _nextId = 0xA000; // erlaubter Bereich für Anwendungen
            if (RegisterHotKey(_hwnd, id, key.Modifiers | ModNoRepeat, key.VirtualKey))
                _registered[id] = (key, action);
            else
                failed.Add($"{Format(key)} ({owner})");
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            entry.Action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
    }

    // ===================== Anzeige / Eingabe =====================

    /// <summary>„Strg+Alt+1“, „Strg+Umschalt+F5“, „Num 3“ …</summary>
    public static string Format(ProfileHotkey? hotkey)
    {
        if (hotkey is null) return "";
        var parts = new List<string>();
        if ((hotkey.Modifiers & ProfileHotkey.Control) != 0) parts.Add("Strg");
        if ((hotkey.Modifiers & ProfileHotkey.Alt) != 0) parts.Add("Alt");
        if ((hotkey.Modifiers & ProfileHotkey.Shift) != 0) parts.Add("Umschalt");
        if ((hotkey.Modifiers & ProfileHotkey.Win) != 0) parts.Add("Win");
        parts.Add(KeyName(KeyInterop.KeyFromVirtualKey(hotkey.VirtualKey)));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
        Key.Add => "Num +",
        Key.Subtract => "Num −",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Prior => "Bild↑",
        Key.Next => "Bild↓",
        Key.Home => "Pos1",
        Key.End => "Ende",
        Key.Insert => "Einfg",
        Key.Delete => "Entf",
        Key.Space => "Leertaste",
        Key.Pause => "Pause",
        Key.Scroll => "Rollen",
        _ => key.ToString()
    };

    /// <summary>Tastenkombination aus einem KeyDown im Eingabefeld. Null, solange nur Modifier gedrückt sind.</summary>
    public static ProfileHotkey? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || ProfileHotkey.IsModifierKey(vk)) return null;

        var mods = Keyboard.Modifiers;
        int m = 0;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= ProfileHotkey.Alt;
        if (mods.HasFlag(ModifierKeys.Control)) m |= ProfileHotkey.Control;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= ProfileHotkey.Shift;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= ProfileHotkey.Win;
        return new ProfileHotkey(m, vk);
    }
}
