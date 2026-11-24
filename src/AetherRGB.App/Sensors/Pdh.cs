using System.Runtime.InteropServices;

namespace AetherRGB.App.Sensors;

/// <summary>Windows-Leistungsindikatoren (PDH) mit englischen Pfaden — die gelten auf jedem Sprach-Windows, anders als die
/// deutschen Namen in der Leistungsüberwachung. Wildcard-Zähler liefern ein Feld aus (Instanzname, Wert).</summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public PdhQuery()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0) throw new InvalidOperationException($"PdhOpenQuery fehlgeschlagen (0x{status:X8}).");
    }

    /// <summary>Zähler hinzufügen; false, wenn es ihn auf diesem System nicht gibt (z. B. keine GPU-Zähler vor Windows 10 1709).</summary>
    public bool Add(string path)
    {
        if (_counters.ContainsKey(path)) return true;
        uint status = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter);
        if (status != 0) return false;
        _counters[path] = counter;
        return true;
    }

    public void Remove(string path)
    {
        if (_counters.Remove(path, out var counter)) PdhRemoveCounter(counter);
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public void Collect() => PdhCollectQueryData(_query);

    /// <summary>Einzelwert; NaN, solange noch keine zwei Messungen da sind oder der Zähler fehlt.</summary>
    public double Value(string path)
    {
        if (!_counters.TryGetValue(path, out var counter)) return double.NaN;
        uint status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value);
        if (status != 0 || value.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) return double.NaN;
        return value.DoubleValue;
    }

    /// <summary>Alle Instanzen eines Wildcard-Zählers.</summary>
    public List<(string Instance, double Value)> Array(string path)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var counter)) return result;
        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
            if (status != 0) return result;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counters.Clear();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhRemoveCounter(IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
}
