namespace NetDownReporter;

/// <summary>Cortes periódicos: uno en <see cref="Anchor"/> y después cada <see cref="Period"/>.</summary>
public sealed record CutSchedule(DateTime Anchor, TimeSpan Period)
{
    /// <summary>El primer corte previsto después de <paramref name="at"/>. Siempre da el mismo valor dentro de un período.</summary>
    public DateTime NextAfter(DateTime at) => Anchor + Period * ((at - Anchor).Ticks / Period.Ticks + 1);
}

/// <summary>
/// Busca un ciclo fijo en las desconexiones de WiFi (lógica pura, sin E/S). Hay routers que cortan el WiFi
/// cada N horas con un temporizador propio: el ciclo es exacto pero se corre unos segundos por vez, porque
/// el reloj del router no está sincronizado. Por eso el período se estima de los datos y no se redondea.
/// </summary>
public static class CutPredictor
{
    // Pocos días: con la deriva del reloj del router (~4 s por ciclo), una semana ya se sale de la tolerancia.
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(3);
    // Si el último corte del ciclo es más viejo, el router pudo haberse reiniciado y el horario ya no vale.
    static readonly TimeSpan MaxAnchorAge = TimeSpan.FromHours(24);
    const int MinMatches = 4;

    /// <param name="times">Horas de las desconexiones, en cualquier orden.</param>
    /// <returns>El ciclo, o null si no hay uno claro.</returns>
    public static CutSchedule? Find(IEnumerable<DateTime> times, DateTime now)
    {
        var recent = times.Where(t => t <= now && now - t <= Window).OrderByDescending(t => t).ToList();
        // El corte más nuevo que encaja en un ciclo (los que no encajan son cortes sueltos o manuales).
        foreach (var anchor in recent.TakeWhile(t => now - t <= MaxAnchorAge))
        {
            var older = recent.Where(t => t < anchor).ToList();
            CutSchedule? best = null;
            var bestMatches = 0;
            // De mayor a menor: un ciclo de 4 h también encaja en 2 h y 1 h, y ante el empate gana el más largo.
            for (var hours = 24; hours >= 1; hours--)
            {
                var nominal = TimeSpan.FromHours(hours);
                double sumGk = 0, sumKk = 0;
                var matches = 0;
                foreach (var t in older)
                {
                    var gap = anchor - t;
                    var k = Math.Round(gap / nominal);
                    if (k < 1 || (gap - nominal * k).Duration() > Tolerance) continue;
                    matches++;
                    sumGk += gap.Ticks * k;
                    sumKk += k * k;
                }
                // Tienen que encajar al menos la mitad de los cortes, para no ver ciclos en cortes al azar.
                if (matches < MinMatches || matches * 2 < older.Count || matches <= bestMatches) continue;
                bestMatches = matches;
                best = new CutSchedule(anchor, TimeSpan.FromTicks((long)(sumGk / sumKk)));
            }
            if (best is not null) return best;
        }
        return null;
    }
}
