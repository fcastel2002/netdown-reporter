using System.Text.Json;

namespace NetDownReporter;

/// <summary>
/// Genera <c>reporte.html</c> autocontenido (sin CDN: tiene que abrir aunque no haya internet)
/// inyectando los datos como JSON en la plantilla embebida <c>report.html</c>.
/// </summary>
public static class Report
{
    const string Placeholder = "/*__DATA__*/null";
    const string IsoLocal = "yyyy-MM-dd'T'HH:mm:ss";

    /// <param name="ongoing">Corte en curso, se muestra como terminado ahora.</param>
    /// <param name="sessionStart">Inicio de la sesión actual, que todavía no está en sesiones.csv.</param>
    public static string Write(OutageLog log, Outage? ongoing, DateTime sessionStart, DateTime now)
    {
        var outages = log.ReadOutages().Select(o => Row(o, o.End!.Value, false)).ToList();
        if (ongoing is not null) outages.Add(Row(ongoing, now, true));
        var sessions = log.ReadSessions()
            .Append((Start: sessionStart, End: now))
            .Select(s => new { start = Iso(s.Start), end = Iso(s.End) });

        // El encoder por defecto escapa '<' y '>', así que el JSON no puede cerrar el <script>.
        var json = JsonSerializer.Serialize(new { generated = Iso(now), outages, sessions });

        using var stream = typeof(Report).Assembly.GetManifestResourceStream("report.html")!;
        var html = new StreamReader(stream).ReadToEnd().Replace(Placeholder, json);

        Directory.CreateDirectory(log.Dir);
        var path = Path.Combine(log.Dir, "reporte.html");
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
