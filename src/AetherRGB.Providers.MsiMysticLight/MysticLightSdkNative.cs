using System.Runtime.InteropServices;

namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Dünne P/Invoke-Schicht über die offizielle MysticLight_SDK(_x64).dll von MSI.
///
/// Die DLL wird bewusst NICHT mitgeliefert (MSI-Lizenzbedingungen, öffentliches MIT-Repo) — der
/// Nutzer lädt das SDK selbst bei MSI herunter und legt die DLL in den Programmordner, nach
/// %APPDATA%\AetherRGB oder gibt den Pfad in den Einstellungen an.
///
/// Geladen wird per <see cref="NativeLibrary"/> + Funktionszeigern statt [DllImport], damit eine
/// fehlende DLL nur ein sauberes "nicht gefunden" ergibt und der Pfad zur Laufzeit wählbar ist.
/// Strings sind BSTR, Listen SAFEARRAY of BSTR — so ist die SDK-Schnittstelle definiert.</summary>
internal sealed class MysticLightSdkNative
{
    public const int MLAPI_OK = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int InitializeFn();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int ReleaseFn();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetErrorMessageFn(int errorCode, [MarshalAs(UnmanagedType.BStr)] out string description);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetDeviceInfoFn(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] deviceTypes,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] ledCounts);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetLedInfoFn(
        [MarshalAs(UnmanagedType.BStr)] string deviceType, uint index,
        [MarshalAs(UnmanagedType.BStr)] out string name,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] styles);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int SetLedColorFn([MarshalAs(UnmanagedType.BStr)] string deviceType, uint index, uint r, uint g, uint b);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int SetLedStyleFn([MarshalAs(UnmanagedType.BStr)] string deviceType, uint index, [MarshalAs(UnmanagedType.BStr)] string style);

    public required InitializeFn Initialize { get; init; }
    public required ReleaseFn Release { get; init; }
    public required GetErrorMessageFn GetErrorMessage { get; init; }
    public required GetDeviceInfoFn GetDeviceInfo { get; init; }
    public required GetLedInfoFn GetLedInfo { get; init; }
    public required SetLedColorFn SetLedColor { get; init; }
    public required SetLedStyleFn SetLedStyle { get; init; }
    public required string LoadedFrom { get; init; }

    private static readonly object LoadLock = new();
    private static MysticLightSdkNative? _loaded;

    /// <summary>Wo nach der DLL gesucht wird, in dieser Reihenfolge.</summary>
    public static IReadOnlyList<string> CandidatePaths(string? configuredPath)
    {
        var names = Environment.Is64BitProcess
            ? new[] { "MysticLight_SDK_x64.dll", "MysticLight_SDK.dll" }
            : new[] { "MysticLight_SDK.dll" };
        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB")
        };

        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // Ordner oder direkt die DLL — beides ist erlaubt.
            if (Directory.Exists(configuredPath)) result.AddRange(names.Select(n => Path.Combine(configuredPath, n)));
            else result.Add(configuredPath);
        }
        foreach (var dir in dirs)
            result.AddRange(names.Select(n => Path.Combine(dir, n)));
        return result;
    }

    public static string? FindDll(string? configuredPath) =>
        CandidatePaths(configuredPath).FirstOrDefault(File.Exists);

    /// <summary>Lädt die DLL einmal pro Prozess. Ein Entladen und erneutes Laden eines anderen Pfads
    /// wird absichtlich nicht unterstützt (das SDK hängt an Dienst-Handles) — dafür App neu starten.</summary>
    public static MysticLightSdkNative? TryLoad(string? configuredPath, out string error)
    {
        lock (LoadLock)
        {
            error = "";
            if (_loaded is not null) return _loaded;

            if (!OperatingSystem.IsWindows())
            {
                error = "Das Mystic Light SDK gibt es nur unter Windows.";
                return null;
            }

            var path = FindDll(configuredPath);
            if (path is null)
            {
                error = "MysticLight_SDK DLL nicht gefunden. Gesucht in: " + string.Join(" | ", CandidatePaths(configuredPath));
                return null;
            }

            if (!NativeLibrary.TryLoad(path, typeof(MysticLightSdkNative).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies, out var handle))
            {
                error = $"'{path}' konnte nicht geladen werden (falsche Architektur oder beschädigt?).";
                return null;
            }

            try
            {
                _loaded = new MysticLightSdkNative
                {
                    Initialize = Get<InitializeFn>(handle, "MLAPI_Initialize"),
                    Release = Get<ReleaseFn>(handle, "MLAPI_Release"),
                    GetErrorMessage = Get<GetErrorMessageFn>(handle, "MLAPI_GetErrorMessage"),
                    GetDeviceInfo = Get<GetDeviceInfoFn>(handle, "MLAPI_GetDeviceInfo"),
                    GetLedInfo = Get<GetLedInfoFn>(handle, "MLAPI_GetLedInfo"),
                    SetLedColor = Get<SetLedColorFn>(handle, "MLAPI_SetLedColor"),
                    SetLedStyle = Get<SetLedStyleFn>(handle, "MLAPI_SetLedStyle"),
                    LoadedFrom = path
                };
                return _loaded;
            }
            catch (EntryPointNotFoundException ex)
            {
                NativeLibrary.Free(handle);
                error = $"'{path}' ist keine passende Mystic-Light-SDK-DLL: {ex.Message}";
                return null;
            }
        }
    }

    private static T Get<T>(IntPtr handle, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    /// <summary>Fehlercode als lesbarer Text — zuerst die SDK-eigene Beschreibung, sonst die
    /// dokumentierten MLAPI-Codes.</summary>
    public string Describe(int code)
    {
        try
        {
            if (GetErrorMessage(code, out var text) == MLAPI_OK && !string.IsNullOrWhiteSpace(text))
                return $"{text} ({code})";
        }
        catch
        {
            // Fallback unten.
        }
        return code switch
        {
            -1 => "MLAPI_ERROR (-1)",
            -2 => "MLAPI_TIMEOUT (-2)",
            -3 => "MLAPI_NO_IMPLEMENTED (-3)",
            -4 => "MLAPI_NOT_INITIALIZED (-4)",
            -101 => "MLAPI_INVALID_ARGUMENT (-101)",
            -102 => "MLAPI_DEVICE_NOT_FOUND (-102)",
            -103 => "MLAPI_NOT_SUPPORTED (-103)",
            _ => $"Fehlercode {code}"
        };
    }
}
