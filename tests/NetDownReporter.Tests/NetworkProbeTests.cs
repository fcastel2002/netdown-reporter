namespace NetDownReporter.Tests;

public class NetworkProbeTests
{
    [Fact]
    public void ParsesSpanishNetshOutput()
    {
        const string output = """
            Hay 1 interfaz en el sistema:

                Nombre                   : Wi-Fi
                Descripci�n            : TP-Link Wireless Nano USB Adapter
                Estado                  : conectado
                SSID                   : MiRed
                AP BSSID               : 02:00:00:aa:bb:cc
                Banda                 : 5 GHz
                Canal: 149
                Se�al                           : 100%
                Rssi           : -42
            """;
        Assert.Equal(
            "Estado=conectado | SSID=MiRed | BSSID=02:00:00:aa:bb:cc | Banda=5 GHz | Canal=149 | Señal=100% | RSSI=-42",
            NetworkProbe.ParseWlan(output));
    }

    [Fact]
    public void ParsesDisconnectedInterface()
    {
        const string output = """
            There is 1 interface on the system:

                Name                   : Wi-Fi
                State                  : disconnected
            """;
        Assert.Equal("Estado=disconnected", NetworkProbe.ParseWlan(output));
    }
}
