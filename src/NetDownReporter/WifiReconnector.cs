namespace NetDownReporter;

/// <summary>
/// Decide cuándo intentar reconectar el WiFi durante un corte (lógica pura, sin E/S). No se puede
/// confiar en que Windows reconecte solo (a veces no lo hace hasta que se abre el panel de WiFi), así
/// que se repite lo que hace el panel: un escaneo y la conexión con el último perfil. El primer intento
/// es al confirmarse el corte y los siguientes, <see cref="RetryDelay"/> después de terminar el anterior,
/// mientras el corte siga siendo <see cref="Cause.Wifi"/>. Repetir seguido no pisa una conexión en curso:
/// <see cref="Wlan"/> sólo actúa sobre interfaces desconectadas.
/// </summary>
public sealed class WifiReconnector
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    DateTime? next;
    bool busy, recovered;
    string? lastError;

    public bool Enabled { get; set; } = true;
    public int Attempts { get; private set; }

    /// <summary>Llamar en cada chequeo, después de actualizar el tracker.</summary>
    /// <returns>true si hay que lanzar un intento ahora; al terminarlo, llamar a <see cref="Completed"/>.</returns>
    public bool Update(DateTime at, Outage? current, Cause? lastFailure)
    {
        if (current is null)
        {
            Reset();
            return false;
        }
        // El WiFi volvió después de intentar: la causa pasó a otra capa (o volvió internet, ver Note).
        if (Attempts > 0 && lastFailure != Cause.Wifi) recovered = true;
        if (!Enabled || busy || lastFailure != Cause.Wifi || at < next) return false;

        busy = true;
        Attempts++;
        return true;
    }

    /// <param name="error">Error de la API de WiFi, o null si se aceptó el pedido.</param>
    public void Completed(DateTime at, string? error)
    {
        if (!busy) return;
        busy = false;
        lastError = error ?? lastError;
        next = at + RetryDelay;
    }

    /// <summary>Nota para el corte; null si no hubo intentos.</summary>
    /// <param name="ended">true si el corte terminó porque volvió internet.</param>
    public string? Note(bool ended = false) => Attempts == 0 ? null
        : recovered || ended ? $"reconexión automática: WiFi recuperado tras {Plural(Attempts)}"
        : $"reconexión automática: {Plural(Attempts)} sin éxito" + (lastError is null ? "" : $" ({lastError})");

    /// <summary>Olvida el corte actual (terminó, se suspendió la PC). Un intento en vuelo se ignora.</summary>
    public void Reset()
    {
        next = null;
        busy = recovered = false;
        lastError = null;
        Attempts = 0;
    }

    static string Plural(int n) => n == 1 ? "1 intento" : $"{n} intentos";
}
