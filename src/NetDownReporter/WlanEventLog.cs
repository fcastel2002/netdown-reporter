using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace NetDownReporter;

/// <summary>Una desconexión de WiFi que Windows registró (evento 8003 de WLAN-AutoConfig).</summary>
public sealed record WlanDisconnect(DateTime Time, string Ssid, string Reason);

/// <summary>
/// Lee del log de Windows las desconexiones de WiFi, incluidas las de antes de instalar la app
/// y las ocurridas con la app cerrada. Se lee al generar el reporte y, para prever cortes periódicos,
/// solo los últimos días al arrancar y al terminar cada corte (nunca en un loop).
/// </summary>
public static class WlanEventLog
{
    const string LogName = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    const int DisconnectId = 8003;
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Desconexiones en orden cronológico, en hora local. Vacío si el log no existe o no se puede leer.</summary>
    /// <param name="within">Solo las de este último tiempo; null para todas.</param>
    public static List<WlanDisconnect> Read(TimeSpan? within = null)
    {
        var list = new List<WlanDisconnect>();
        try
        {
            var age = within is { } w ? $" and TimeCreated[timediff(@SystemTime) <= {(long)w.TotalMilliseconds}]" : "";
            var query = new EventLogQuery(LogName, PathType.LogName, $"*[System[EventID={DisconnectId}{age}]]");
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                    if (Parse(e.ToXml()) is { } d) list.Add(d);
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    /// <summary>Parsea el XML de un evento 8003. Devuelve null si no es una desconexión válida.</summary>
    internal static WlanDisconnect? Parse(string xml)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        var system = root.Element(Ns + "System");
        if ((string?)system?.Element(Ns + "EventID") != DisconnectId.ToString(CultureInfo.InvariantCulture)) return null;
        var created = (string?)system.Element(Ns + "TimeCreated")?.Attribute("SystemTime");
        if (!DateTime.TryParse(created, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return null;

        var data = root.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? [];
        string Field(string name) => data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value.Trim() ?? "";
        return new WlanDisconnect(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(), Field("SSID"), Field("Reason"));
    }
}
