using System.Text.Json;

namespace NetDownReporter;

/// <summary>
/// Genera un reporte HTML autocontenido (sin CDN: tiene que abrir aunque no haya internet)
/// inyectando los datos como JSON en una plantilla embebida. Las dos plantillas reciben el mismo JSON.
/// </summary>
public static class Report
{
    const string Placeholder = "/*__DATA__*/null";
    const string IsoLocal = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>Plantilla embebida y nombre del archivo que se escribe en la carpeta de datos.</summary>
    public sealed record Kind(string Template, string FileName);

    /// <summary>Todo el detalle técnico, con filtros.</summary>
    public static readonly Kind Full = new("report.html", "reporte.html");
    /// <summary>Para mostrar en el reclamo: el ciclo de los cortes explicado sin términos técnicos.</summary>
    public static readonly Kind Claim = new("claim.html", "reclamo.html");

    /// <param name="ongoing">Corte en curso, se muestra como terminado ahora.</param>
    /// <param name="sessionStart">Inicio de la sesión actual, que todavía no está en sesiones.csv.</param>
    /// <param name="wlan">Desconexiones de WiFi del log de Windows (<see cref="WlanEventLog.Read"/>).</param>
    public static string Write(Kind kind, OutageLog log, Outage? ongoing, DateTime sessionStart, DateTime now, IEnumerable<WlanDisconnect> wlan)
    {
        var outages = log.ReadOutages().Select(o => Row(o, o.End!.Value, false)).ToList();
        if (ongoing is not null) outages.Add(Row(ongoing, now, true));
        var sessions = log.ReadSessions()
            .Append((Start: sessionStart, End: now))
            .Select(s => new { start = Iso(s.Start), end = Iso(s.End) });

        // El encoder por defecto escapa '<' y '>', así que el JSON no puede cerrar el <script>.
        var disconnects = wlan.Select(d => new { time = Iso(d.Time), ssid = d.Ssid, reason = d.Reason, code = d.Code });
        var json = JsonSerializer.Serialize(new { generated = Iso(now), outages, sessions, disconnects });

        using var stream = typeof(Report).Assembly.GetManifestResourceStream(kind.Template)!;
        var html = new StreamReader(stream).ReadToEnd().Replace(Placeholder, json);

        Directory.CreateDirectory(log.Dir);
        var path = Path.Combine(log.Dir, kind.FileName);
        File.WriteAllText(path, html);
        return path;
    }

    static object Row(Outage o, DateTime end, bool ongoing) => new
    {
        start = Iso(o.Start),
        end = Iso(end),
        cause = o.Cause.ToString(),
        causes = o.Causes.Select(c => c.ToString()),
        note = o.Note,
        detail = o.Detail,
        ongoing,
    };

    static string Iso(DateTime t) => t.ToString(IsoLocal, System.Globalization.CultureInfo.InvariantCulture);
}
