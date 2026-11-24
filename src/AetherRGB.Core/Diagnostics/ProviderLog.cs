namespace AetherRGB.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>Optional für Provider: meldet Verbindungs- und Fehlerdetails an die Log-Leiste der App,
/// statt sie still zu schlucken. Ereignisse können von beliebigen Threads kommen.</summary>
public interface IProviderLog
{
    event Action<LogLevel, string>? Log;
}

public static class ExceptionText
{
    /// <summary>"Typ: Meldung" inkl. innerer Exceptions — reicht fürs Log, ohne kompletten Stacktrace.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
        var text = $"{ex.GetType().Name}: {ex.Message}";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text += $" → {inner.GetType().Name}: {inner.Message}";
        return text;
    }
}
