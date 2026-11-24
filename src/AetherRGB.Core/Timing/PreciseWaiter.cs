using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AetherRGB.Core.Timing;

/// <summary>Wartet auf feste Termine mit ~1 ms Genauigkeit. <c>Thread.Sleep</c> und
/// <c>System.Threading.Timer</c> arbeiten unter Windows standardmäßig in 15,6-ms-Schritten — ein
/// 33-ms-Takt wird dadurch abwechselnd 31 und 47 ms lang, was auf den LEDs als ungleich große
/// Sprünge (Ruckeln) sichtbar wird. Unter Windows 10 1803+ wird ein hochauflösender Wartetimer
/// benutzt, sonst <c>timeBeginPeriod(1)</c> für die Lebensdauer dieses Objekts.
/// Nicht threadsicher — jeder Takt-Thread hat seinen eigenen.</summary>
public sealed class PreciseWaiter : IDisposable
{
    private readonly IntPtr _timer;
    private readonly bool _raisedPeriod;
    private bool _disposed;

    public PreciseWaiter()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _timer = IntPtr.Zero;
        }
        if (_timer == IntPtr.Zero)
        {
            try { _raisedPeriod = timeBeginPeriod(1) == 0; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    /// <summary>True, wenn der hochauflösende Wartetimer verfügbar ist (für die Log-Ausgabe).</summary>
    public bool IsHighResolution => _timer != IntPtr.Zero;

    /// <summary>Wartet, bis <paramref name="clock"/> mindestens <paramref name="deadline"/> erreicht hat
    /// oder <paramref name="cancel"/> gesetzt wird. Gibt sofort zurück, wenn der Termin schon vorbei ist.</summary>
    public void WaitUntil(Stopwatch clock, TimeSpan deadline, WaitHandle? cancel = null)
    {
        while (true)
        {
            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return;

            if (_timer != IntPtr.Zero)
            {
                // Negativ = relative Zeit in 100-ns-Einheiten.
                long dueTime = -remaining.Ticks;
                if (!SetWaitableTimerEx(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    if (SleepOrCancel(remaining, cancel)) return;
                    continue;
                }
                if (cancel is null) WaitForSingleObject(_timer, INFINITE);
                else if (WaitHandle.WaitAny(new[] { cancel, new TimerHandle(_timer) }) == 0) return;
            }
            else if (SleepOrCancel(remaining, cancel)) return;
        }
    }

    /// <returns>true, wenn abgebrochen wurde.</returns>
    private static bool SleepOrCancel(TimeSpan remaining, WaitHandle? cancel)
    {
        // Die letzte Millisekunde nicht schlafen, sonst rundet der Systemtakt auf.
        var sleep = remaining - TimeSpan.FromMilliseconds(1);
        if (sleep <= TimeSpan.Zero) { Thread.SpinWait(200); return false; }
        if (cancel is null) { Thread.Sleep(sleep); return false; }
        return cancel.WaitOne(sleep);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        if (_raisedPeriod) timeEndPeriod(1);
    }

    /// <summary>Verpackt das Timer-Handle für <see cref="WaitHandle.WaitAny(WaitHandle[])"/>, ohne es beim
    /// Aufräumen zu schließen (gehört dem <see cref="PreciseWaiter"/>).</summary>
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(IntPtr handle) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: false);
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion,
        IntPtr completionArg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
