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
    /// Pide un escaneo en todas las interfaces (lo mismo que hace abrir el panel de WiFi) y, si hay perfil,
    /// la conexión con ese perfil. Los dos pedidos son asíncronos en el driver: vuelven enseguida.
    /// </summary>
    /// <returns>Descripción del error si alguna llamada falló; null si se aceptaron.</returns>
    public static string? Scan() => Use((handle, guid, _) => Check("WlanScan", WlanScan(handle, ref guid, 0, 0, 0)));

    public static string? Connect(string profile) => Use((handle, guid, _) =>
    {
        var p = new ConnectionParameters
        {
            Mode = ConnectionModeProfile,
            Profile = profile,
            BssType = BssTypeInfrastructure,
        };
        return Check("WlanConnect", WlanConnect(handle, ref guid, ref p, 0));
    });

    static string? Check(string call, uint result) => result == 0 ? null : $"{call} error {result}";

    /// <summary>Abre un handle, llama a <paramref name="action"/> por cada interfaz y lo cierra.</summary>
    /// <returns>El primer error; null si no hubo.</returns>
    static string? Use(Func<nint, Guid, int, string?> action)
    {
        string? error = null;
        try
        {
            var result = WlanOpenHandle(ClientVersion, 0, out _, out var handle);
            if (result != 0) return $"WlanOpenHandle error {result}";
            try
            {
                if ((result = WlanEnumInterfaces(handle, 0, out var list)) != 0) return $"WlanEnumInterfaces error {result}";
                try
                {
                    var count = Marshal.ReadInt32(list);
                    if (count == 0) return "sin interfaz WiFi";
                    for (var i = 0; i < count; i++)
                    {
                        var item = list + 8 + i * InterfaceInfoSize;
                        error ??= action(handle, Marshal.PtrToStructure<Guid>(item), Marshal.ReadInt32(item + InterfaceInfoSize - 4));
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(handle, 0); }
            return error;
        }
        catch (DllNotFoundException)
        {
            // Windows sin el servicio WLAN (p. ej. una PC sólo con cable).
            return "wlanapi.dll no disponible";
        }
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

    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(nint memory);
}
