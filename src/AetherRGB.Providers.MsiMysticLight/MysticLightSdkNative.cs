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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InitializeFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ReleaseFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetErrorMessageFn(int errorCode, [MarshalAs(UnmanagedType.BStr)] out string description);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetDeviceInfoFn(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] deviceTypes,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] ledCounts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetLedInfoFn(
        [MarshalAs(UnmanagedType.BStr)] string deviceType, uint index,
        [MarshalAs(UnmanagedType.BStr)] out string name,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] styles);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetLedNameFn(
        [MarshalAs(UnmanagedType.BStr)] string deviceType,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] ledNames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetLedStyleFn([MarshalAs(UnmanagedType.BStr)] string deviceType, uint index, [MarshalAs(UnmanagedType.BStr)] out string style);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SetLedColorFn([MarshalAs(UnmanagedType.BStr)] string deviceType, uint index, uint r, uint g, uint b);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SetLedStyleFn([MarshalAs(UnmanagedType.BStr)] string deviceType, uint index, [MarshalAs(UnmanagedType.BStr)] string style);

    public required InitializeFn Initialize { get; init; }
    public required ReleaseFn Release { get; init; }
    public required GetErrorMessageFn GetErrorMessage { get; init; }
    public required GetDeviceInfoFn GetDeviceInfo { get; init; }
    public required GetLedInfoFn GetLedInfo { get; init; }
    public required GetLedNameFn GetLedName { get; init; }
    public required GetLedStyleFn GetLedStyle { get; init; }
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

            var archError = CheckArchitecture(path);
            if (archError is not null)
            {
                error = archError;
                return null;
            }

            // Abhängigkeiten im DLL-Ordner UND in System32 suchen: Die SDK-DLL importiert
            // OLEACC.dll und WINSPOOL.DRV, die keine KnownDLLs sind. Mit nur
            // UseDllDirectoryForDependencies (LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR) findet Windows sie
            // nicht und das Laden scheitert mit 0x8007007E.
            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(path, typeof(MysticLightSdkNative).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                error = $"'{path}' konnte nicht geladen werden: {ex.Message}";
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
                    GetLedName = Get<GetLedNameFn>(handle, "MLAPI_GetLedName"),
                    GetLedStyle = Get<GetLedStyleFn>(handle, "MLAPI_GetLedStyle"),
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

    /// <summary>Liest den Machine-Typ aus dem PE-Kopf, damit eine 32-Bit-DLL in der 64-Bit-App
    /// eine klare Meldung ergibt statt eines allgemeinen Ladefehlers.</summary>
    private static string? CheckArchitecture(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt16() != 0x5A4D) return $"'{path}' ist keine DLL (MZ-Kopf fehlt).";
            reader.BaseStream.Position = 0x3C;
            reader.BaseStream.Position = reader.ReadInt32();
            if (reader.ReadUInt32() != 0x00004550) return $"'{path}' ist keine DLL (PE-Kopf fehlt).";
            var machine = reader.ReadUInt16();
            var expected = Environment.Is64BitProcess ? (ushort)0x8664 : (ushort)0x014C;
            if (machine == expected) return null;
            var found = machine switch { 0x8664 => "64-Bit", 0x014C => "32-Bit", _ => $"0x{machine:X4}" };
            var wanted = Environment.Is64BitProcess ? "64-Bit (MysticLight_SDK_x64.dll)" : "32-Bit (MysticLight_SDK.dll)";
            return $"'{path}' ist eine {found}-DLL, AetherRGB braucht die {wanted}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return $"'{path}' konnte nicht gelesen werden: {ex.Message}";
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
