namespace NetDownReporter;

/// <summary>
/// Convierte la secuencia de chequeos en cortes. Un corte se confirma recién tras
/// <c>failuresToConfirm</c> chequeos fallidos seguidos (para no registrar microcortes),
/// pero su inicio es la hora del primer fallo. Termina con el primer chequeo exitoso.
/// </summary>
public sealed class OutageTracker(int failuresToConfirm = 2)
{
    DateTime? firstFailure;
    int failures;
    readonly List<Cause> seen = [];

    public Outage? Current { get; private set; }
    public Cause? LastFailure { get; private set; }
    /// <summary>Hubo fallos que todavía no alcanzan para confirmar un corte.</summary>
    public bool Pending => Current is null && failures > 0;

    /// <param name="failure">null si el chequeo tuvo internet.</param>
    /// <returns>El corte que empezó o terminó en este chequeo; null si no cambió nada.</returns>
    public (Outage Outage, bool Started)? Update(DateTime at, Cause? failure)
    {
        LastFailure = failure;
        if (failure is not { } cause)
        {
            var ended = Current is null ? null : Current with { End = at, Causes = [.. seen] };
            Reset();
            return ended is null ? null : (ended, false);
        }

        if (!seen.Contains(cause)) seen.Add(cause);
        if (Current is not null) return null;

        firstFailure ??= at;
        // Sin gateway no hay duda posible (no es pérdida de paquetes): se confirma enseguida.
        if (++failures < failuresToConfirm && cause != Cause.Wifi) return null;

        Current = new Outage(firstFailure.Value, null, cause, [.. seen]);
        return (Current, true);
    }

    public void SetDetail(string detail)
    {
        if (Current is not null) Current = Current with { Detail = detail };
    }

    public void SetNote(string note)
    {
        if (Current is not null) Current = Current with { Note = note };
    }

    /// <summary>Cierra el corte abierto sin que haya vuelto internet (suspensión, salida).</summary>
    /// <param name="note">Se agrega a la nota que ya tenga el corte.</param>
    public Outage? Interrupt(DateTime at, string note)
    {
        var closed = Current is null ? null : Current with { End = at, Causes = [.. seen], Note = Outage.JoinNotes(Current.Note, note) };
        Reset();
        return closed;
    }

    void Reset()
    {
        Current = null;
        firstFailure = null;
        failures = 0;
        seen.Clear();
    }
}
