using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetDownReporter;

/// <summary>
/// Persistencia en texto plano para poder abrirla en Excel como evidencia:
/// <list type="bullet">
/// <item><c>cortes.csv</c>: un renglón por corte terminado.</item>
/// <item><c>sesiones.csv</c>: períodos en los que el monitor estuvo corriendo (para normalizar el reporte).</item>
/// <item><c>estado.json</c>: sesión y corte en curso, reescrito cada minuto; si existe al arrancar, la app
/// se cerró de golpe y se usa para cerrar la sesión y el corte pendientes.</item>
/// </list>
/// Separador ';' porque Excel en español no abre bien CSV con ','.
/// </summary>
public sealed class OutageLog(string dir)
{
    public static string DefaultDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetDownReporter");

    const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    const string OutagesHeader = "inicio;fin;duracion_seg;causa;causas;nota;detalle";
    const string SessionsHeader = "inicio;fin";

    public string Dir => dir;
    string OutagesPath => Path.Combine(dir, "cortes.csv");
    string SessionsPath => Path.Combine(dir, "sesiones.csv");
    string StatePath => Path.Combine(dir, "estado.json");

    public sealed record State(DateTime SessionStart, DateTime LastSeen, Outage? Open);

    public void Append(Outage o)
    {
        var end = o.End ?? throw new ArgumentException("El corte no terminó", nameof(o));
        AppendLine(OutagesPath, OutagesHeader,
            Fmt(o.Start), Fmt(end), ((long)(end - o.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            o.Cause.ToString(), string.Join('+', o.Causes), o.Note, o.Detail);
    }

    public void AppendSession(DateTime start, DateTime end) => AppendLine(SessionsPath, SessionsHeader, Fmt(start), Fmt(end));

    public IReadOnlyList<Outage> ReadOutages() =>
        ReadRows(OutagesPath).Select(r => new Outage(
            Parse(r[0]), Parse(r[1]), Enum.Parse<Cause>(r[3]),
            r[4].Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<Cause>).ToArray(),
            r[5], r[6])).ToList();

    public IReadOnlyList<(DateTime Start, DateTime End)> ReadSessions() =>
        ReadRows(SessionsPath).Select(r => (Parse(r[0]), Parse(r[1]))).ToList();

    public void SaveState(State state)
    {
        Directory.CreateDirectory(dir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, StatePath, overwrite: true);
    }

    public void ClearState() => File.Delete(StatePath);

    /// <summary>Cierra la sesión y el corte que quedaron abiertos si la app terminó sin salir limpio.</summary>
    public void RecoverFromCrash()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            if (JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) is { } s)
            {
                AppendSession(s.SessionStart, s.LastSeen);
                if (s.Open is { } open)
                {
                    var end = s.LastSeen > open.Start ? s.LastSeen : open.Start;
                    Append(open with { End = end, Note = Outage.JoinNotes(open.Note, "monitor cerrado de golpe (fin estimado)") });
                }
            }
        }
        catch (JsonException) { }
        ClearState();
    }

    static string Fmt(DateTime t) => t.ToString(TimeFormat, CultureInfo.InvariantCulture);
    static DateTime Parse(string s) => DateTime.ParseExact(s, TimeFormat, CultureInfo.InvariantCulture);

    void AppendLine(string path, string header, params string[] fields)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        if (!File.Exists(path)) sb.Append(header).Append("\r\n");
        sb.AppendJoin(';', fields.Select(Escape)).Append("\r\n");
        // UTF-8 con BOM para que Excel muestre bien los acentos.
        File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
    }

    static string Escape(string field) =>
        field.IndexOfAny([';', '"', '\r', '\n']) < 0 ? field
        : "\"" + field.Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\"";

    static IEnumerable<string[]> ReadRows(string path) =>
        File.Exists(path) ? File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(SplitRow) : [];

    internal static string[] SplitRow(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') sb.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }
}
