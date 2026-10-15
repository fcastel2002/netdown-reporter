using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetDownReporter;

public static class NetworkProbe
{
    static readonly IPAddress[] InternetHosts = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];
    const int TimeoutMs = 1000;

    /// <summary>
    /// Con internet hace un solo ping. Sólo si falla baja capa por capa para clasificar el corte.
    /// </summary>
    /// <returns>null si hay internet; si no, la primera capa que falla.</returns>
    public static async Task<Cause?> CheckAsync()
    {
        foreach (var host in InternetHosts)
            if (await PingAsync(host)) return null;

        if (FindGateway() is not { } gateway) return Cause.Wifi;
        return await PingAsync(gateway) ? Cause.Isp : Cause.Router;
    }

    static async Task<bool> PingAsync(IPAddress address)
    {
        using var ping = new Ping();
        try { return (await ping.SendPingAsync(address, TimeoutMs)).Status == IPStatus.Success; }
        catch (PingException) { return false; }
    }

    static IPAddress? FindGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    // Las claves de netsh vienen traducidas y con encoding OEM; sólo se usan las que son iguales
    // en español e inglés (o ASCII), y la señal se reconoce por terminar en '%'.
    static readonly (string Key, string Label)[] WlanFields =
    [
        ("Estado", "Estado"), ("State", "Estado"), ("SSID", "SSID"), ("AP BSSID", "BSSID"),
        ("Banda", "Banda"), ("Band", "Banda"), ("Canal", "Canal"), ("Channel", "Canal"), ("Rssi", "RSSI"),
    ];

    /// <summary>Resumen de una línea de <c>netsh wlan show interfaces</c> para guardar como evidencia.</summary>
    public static async Task<string> DescribeWifiAsync()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return ParseWlan(output);
        }
        catch (Exception e)
        {
            return $"netsh falló: {e.Message}";
        }
    }

    internal static string ParseWlan(string output)
    {
        var parts = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.EndsWith('%'))
            {
                parts.Add($"Señal={value}");
                continue;
            }
            var field = WlanFields.FirstOrDefault(f => key == f.Key);
            if (field.Label is not null) parts.Add($"{field.Label}={value}");
        }
        return parts.Count == 0 ? "sin interfaz WiFi" : string.Join(" | ", parts);
    }
}
