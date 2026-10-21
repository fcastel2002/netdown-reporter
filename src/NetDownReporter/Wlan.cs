using System.Runtime.InteropServices;

namespace NetDownReporter;

/// <summary>
/// Native WiFi API (wlanapi.dll) para reconectar sin abrir el panel de WiFi. Se usa en lugar de netsh
/// porque no lanza un proceso y los nombres de perfil vienen en UTF-16 (netsh los da en OEM).
/// </summary>
static class Wlan
{
    const uint ClientVersion = 2;
    const int OpcodeCurrentConnection = 7;
    const int InterfaceStateConnected = 1;
    const int InterfaceStateDisconnected = 4;
    const uint NotificationSourceAcm = 0x8;
    const int AcmScanComplete = 7, AcmScanFail = 8;
    const int ConnectionModeProfile = 0;
    const int BssTypeInfrastructure = 1;
    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + WLAN_INTERFACE_STATE (4).
    const int InterfaceInfoSize = 532;

    /// <summary>Perfil con el que está conectada la primera interfaz WiFi; null si no hay ninguna conectada.</summary>
    public static string? CurrentProfile()
    {
        string? profile = null;
        Use((handle, guid, state) =>
        {
            if (profile is not null || state != InterfaceStateConnected) return null;
            if (WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, 0, out _, out var data, 0) != 0) return null;
            // WLAN_CONNECTION_ATTRIBUTES: estado (4) + modo (4) + WCHAR strProfileName[256].
            profile = Marshal.PtrToStringUni(data + 8);
            WlanFreeMemory(data);
            return null;
        });
        return string.IsNullOrEmpty(profile) ? null : profile;
    }

    /// <summary>
    /// Pide un escaneo en las interfaces desconectadas (lo mismo que hace abrir el panel de WiFi) y espera
    /// a que Windows avise que terminó, o como mucho <paramref name="timeout"/>. Un escaneo suele tardar
    /// 1–3 s. Una interfaz que ya está conectándose se deja tranquila, como en <see cref="Connect"/>.
    /// </summary>
    /// <returns>Descripción del error si alguna llamada falló; null si se aceptaron.</returns>
    public static async Task<string?> ScanAsync(TimeSpan timeout)
    {
        // Las notificaciones llegan en un hilo de wlanapi: no hay que cerrar el handle desde ahí.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Escaneos sin terminar, más 1 hasta haberlos pedido todos (para no terminar antes de tiempo).
        var pending = 1;
        WlanNotificationCallback callback = (data, _) =>
        {
            var code = Marshal.ReadInt32(data + 4);
            if ((uint)Marshal.ReadInt32(data) == NotificationSourceAcm && code is AcmScanComplete or AcmScanFail
                && Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
        };
        try
        {
            var result = WlanOpenHandle(ClientVersion, 0, out _, out var handle);
            if (result != 0) return $"WlanOpenHandle error {result}";
            try
            {
                // Si no se puede registrar, se espera el timeout completo como antes. Sin ignorar duplicados:
                // si no, Windows descarta el aviso de fin de escaneo cuando es igual al del intento anterior.
                WlanRegisterNotification(handle, NotificationSourceAcm, false, callback, 0, 0, out _);
                var error = ForEachInterface(handle, (guid, state) =>
                {
                    if (state != InterfaceStateDisconnected) return Busy(state);
                    var e = Check("WlanScan", WlanScan(handle, ref guid, 0, 0, 0));
                    if (e is null) Interlocked.Increment(ref pending);
                    return e;
                });
                if (Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
                await Task.WhenAny(done.Task, Task.Delay(timeout));
                return error;
            }
            finally
            {
                WlanCloseHandle(handle, 0);
                GC.KeepAlive(callback);
            }
        }
        catch (DllNotFoundException)
        {
            return "wlanapi.dll no disponible";
        }
    }

    /// <summary>
    /// Pide la conexión con <paramref name="profile"/> en las interfaces desconectadas. Una interfaz que ya
    /// está conectándose se deja tranquila: pedirle otra conexión la haría empezar de cero.
    /// </summary>
    public static string? Connect(string profile) => Use((handle, guid, state) =>
    {
        if (state != InterfaceStateDisconnected) return Busy(state);
        var p = new ConnectionParameters
        {
            Mode = ConnectionModeProfile,
            Profile = profile,
            BssType = BssTypeInfrastructure,
        };
        return Check("WlanConnect", WlanConnect(handle, ref guid, ref p, 0));
    });

    static string? Check(string call, uint result) => result == 0 ? null : $"{call} error {result}";

    // Queda en la nota del corte si nunca se pudo pedir la conexión: muestra si Windows se trabó a mitad
    // de conectarse (y no desconectado), que es lo que hay que saber para forzarlo en ese estado.
    static string? Busy(int state) => state switch
    {
        InterfaceStateConnected => null,
        0 => "WiFi no listo",
        3 => "WiFi desconectándose",
        5 => "WiFi asociándose",
        6 => "WiFi buscando la red",
        7 => "WiFi autenticándose",
        _ => $"WiFi en estado {state}",
    };

    /// <summary>Abre un handle, llama a <paramref name="action"/> por cada interfaz y lo cierra.</summary>
    /// <returns>El primer error; null si no hubo.</returns>
    static string? Use(Func<nint, Guid, int, string?> action)
    {
        try
        {
            var result = WlanOpenHandle(ClientVersion, 0, out _, out var handle);
            if (result != 0) return $"WlanOpenHandle error {result}";
            try { return ForEachInterface(handle, (guid, state) => action(handle, guid, state)); }
            finally { WlanCloseHandle(handle, 0); }
        }
        catch (DllNotFoundException)
        {
            // Windows sin el servicio WLAN (p. ej. una PC sólo con cable).
            return "wlanapi.dll no disponible";
        }
    }

    static string? ForEachInterface(nint handle, Func<Guid, int, string?> action)
    {
        var result = WlanEnumInterfaces(handle, 0, out var list);
        if (result != 0) return $"WlanEnumInterfaces error {result}";
        try
        {
            var count = Marshal.ReadInt32(list);
            if (count == 0) return "sin interfaz WiFi";
            string? error = null;
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                // Sin ??= directo: después de un error hay que seguir con las otras interfaces.
                var e = action(Marshal.PtrToStructure<Guid>(item), Marshal.ReadInt32(item + InterfaceInfoSize - 4));
                error ??= e;
            }
            return error;
        }
        finally { WlanFreeMemory(list); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectionParameters
    {
        public int Mode;
        public string Profile;
        public nint Ssid;
        public nint BssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [DllImport("wlanapi.dll")]
    static extern uint WlanCloseHandle(nint handle, nint reserved);

    [DllImport("wlanapi.dll")]
    static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    static extern uint WlanQueryInterface(nint handle, ref Guid interfaceGuid, int opcode, nint reserved,
        out uint dataSize, out nint data, nint opcodeValueType);

    [DllImport("wlanapi.dll")]
    static extern uint WlanScan(nint handle, ref Guid interfaceGuid, nint ssid, nint ieData, nint reserved);

    [DllImport("wlanapi.dll")]
    static extern uint WlanConnect(nint handle, ref Guid interfaceGuid, ref ConnectionParameters parameters, nint reserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void WlanNotificationCallback(nint data, nint context);

    [DllImport("wlanapi.dll")]
    static extern uint WlanRegisterNotification(nint handle, uint notificationSource, bool ignoreDuplicate,
        WlanNotificationCallback callback, nint context, nint reserved, out uint previousSource);

    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(nint memory);
}
