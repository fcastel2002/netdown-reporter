namespace NetDownReporter;

/// <summary>Primera capa que falla, de la más cercana a la PC a la más lejana.</summary>
public enum Cause
{
    /// <summary>La PC no tiene WiFi/gateway: Windows se desconectó o el access point desapareció.</summary>
    Wifi,
    /// <summary>Hay WiFi pero el gateway (router) no responde.</summary>
    Router,
    /// <summary>El router responde pero no hay salida a internet: problema del servicio.</summary>
    Isp,
}

public sealed record Outage(
    DateTime Start,
    DateTime? End,
    Cause Cause,
    IReadOnlyList<Cause> Causes,
    string Note = "",
    string Detail = "")
{
    public TimeSpan Duration => (End ?? DateTime.Now) - Start;

    public static string JoinNotes(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}; {b}";
}

public static class Labels
{
    public static string Short(Cause c) => c switch
    {
        Cause.Wifi => "Sin WiFi",
        Cause.Router => "Router",
        _ => "ISP",
    };

    public static string Explain(Cause c) => c switch
    {
        Cause.Wifi => "La PC quedó desconectada del WiFi. Abrí el panel de WiFi para reconectar.",
        Cause.Router => "Hay WiFi pero el router no responde.",
        _ => "El router responde pero no hay salida a internet (ISP).",
    };

    public const string Reconnecting = "La PC quedó desconectada del WiFi. Intentando reconectar automáticamente…";

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min"
        : d.TotalMinutes >= 1 ? $"{d.Minutes} min {d.Seconds:00} s"
        : $"{Math.Max(0, d.Seconds)} s";
}
