using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Systemweite Tastenkürzel über einen Low-Level-Tastaturhook — lösen auch aus, wenn ein Spiel im
/// Vordergrund läuft und AetherRGB nur im Tray liegt.
///
/// Ursprünglich lief das über Windows' <c>RegisterHotKey</c>. Das verlangt, dass die Zusatztasten (Strg/Alt/Win)
/// im exakten Augenblick des Tastendrucks als „gerade gehalten“ im internen Tastaturstatus stehen. Eine echte
/// Tastatur hält sie naturgemäß eine Weile, ein USB-Makropad wie das Ulanzi D200 kann seine Tastenkombination
/// aber in einem einzigen, sehr kurzen HID-Report schicken — Zusatztaste und Haupttaste fast gleichzeitig, die
/// Zusatztaste manchmal schon wieder los, bevor der interne Status aktualisiert ist. Dadurch hat <c>RegisterHotKey</c>
/// den Tastendruck gelegentlich verpasst, bei echten Tastaturen aber nicht. Dieser Hook verfolgt den Tastenstatus
/// deshalb selbst mit einer kurzen Gnadenfrist (<see cref="GraceMs"/>): Eine Zusatztaste zählt auch dann noch als
/// gehalten, wenn sie gerade erst losgelassen wurde.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmKeyUp = 0x0101, WmSysKeyUp = 0x0105;

    /// <summary>Wie lange eine kurz zuvor losgelassene Zusatztaste noch als „gehalten“ zählt — fängt den Fall ab,
    /// dass ein Makropad Zusatz- und Haupttaste in einem einzigen HID-Report mit minimalem Zeitabstand schickt.</summary>
    private const long GraceMs = 150;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    // Die native KBDLLHOOKSTRUCT hat noch scanCode/flags/time/dwExtraInfo danach — die brauchen wir nicht,
    // Marshal.PtrToStructure liest hier bewusst nur die ersten 4 Byte (vkCode liegt an Offset 0).
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static readonly int[] CtrlVks = { 0x11, 0xA2, 0xA3 };
    private static readonly int[] AltVks = { 0x12, 0xA4, 0xA5 };
    private static readonly int[] ShiftVks = { 0x10, 0xA0, 0xA1 };
    private static readonly int[] WinVks = { 0x5B, 0x5C };

    private readonly LowLevelKeyboardProc _proc;
    private readonly IntPtr _hook;
    private readonly HashSet<int> _down = new();
    private readonly Dictionary<int, long> _lastUp = new();
    private readonly List<(ProfileHotkey Key, Action Action, string Owner)> _wanted = new();
    private int _suspended;

    public GlobalHotkeys(Window window)
    {
        _ = window; // Fensterbezug nicht mehr nötig, Signatur bleibt für die Aufrufer gleich.
        _proc = Hook;
        using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Tastaturhook konnte nicht installiert werden.");
    }

    /// <summary>Ersetzt alle Kürzel.</summary>
    public IReadOnlyList<string> SetAll(IEnumerable<(ProfileHotkey Key, Action Action, string Owner)> hotkeys)
    {
        _wanted.Clear();
        _wanted.AddRange(hotkeys);
        return Array.Empty<string>(); // ein Hook kann nicht „schon vergeben“ sein wie RegisterHotKey — Mehrfachbelegung meldet HotkeyUsedBy() vorher.
    }

    /// <summary>Während der Hotkey-Eingabe im Profil-Dialog pausieren — sonst würde der Hook die Tastenkombination
    /// abfangen, bevor das Eingabefeld sie sieht. Verschachtelbar.</summary>
    public void Suspend() => _suspended++;

    public void Resume() { if (_suspended > 0) _suspended--; }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        int msg = wParam.ToInt32();
        bool isDown = msg is WmKeyDown or WmSysKeyDown;
        bool isUp = msg is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp) return CallNextHookEx(_hook, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        int vk = data.VkCode;

        if (isUp)
        {
            _down.Remove(vk);
            _lastUp[vk] = Environment.TickCount64;
            if (_lastUp.Count > 64) _lastUp.Clear(); // winzige Tabelle, nie wirklich groß
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        bool wasAlreadyDown = !_down.Add(vk);
        if (_suspended > 0 || ProfileHotkey.IsModifierKey(vk) || wasAlreadyDown)
            return CallNextHookEx(_hook, code, wParam, lParam);

        int mods = 0;
        if (GroupActive(CtrlVks)) mods |= ProfileHotkey.Control;
        if (GroupActive(AltVks)) mods |= ProfileHotkey.Alt;
        if (GroupActive(ShiftVks)) mods |= ProfileHotkey.Shift;
        if (GroupActive(WinVks)) mods |= ProfileHotkey.Win;

        foreach (var (key, action, _) in _wanted)
        {
            if (key.VirtualKey != vk || key.Modifiers != mods) continue;
            action();
            return (IntPtr)1; // wie RegisterHotKey: Tastendruck nicht an Spiel/Fenster weiterreichen
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Eine der Varianten (links/rechts/neutral) gerade gehalten, oder erst vor <see cref="GraceMs"/> los.</summary>
    private bool GroupActive(int[] vks)
    {
        long now = Environment.TickCount64;
        foreach (var vk in vks)
        {
            if (_down.Contains(vk)) return true;
            if (_lastUp.TryGetValue(vk, out var up) && now - up <= GraceMs) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
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
