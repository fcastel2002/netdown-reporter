namespace NetDownReporter.Tests;

public class WlanEventLogTests
{
    // Evento real de esta PC (Windows en español), con las comillas simples que usa EventRecord.ToXml().
    const string Disconnect = """
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WLAN-AutoConfig' Guid='{9580d7dd-0379-4658-9870-d5be7d52d6de}'/><EventID>8003</EventID><Version>0</Version><Level>4</Level><Task>24010</Task><Opcode>192</Opcode><Keywords>0x8000000000000600</Keywords><TimeCreated SystemTime='2026-10-17T03:11:52.9626307Z'/><EventRecordID>7587</EventRecordID><Correlation/><Execution ProcessID='5248' ThreadID='5688'/><Channel>Microsoft-Windows-WLAN-AutoConfig/Operational</Channel><Computer>PC</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='InterfaceGuid'>{f0061af8-ef39-4855-9c72-815f8e74a500}</Data><Data Name='InterfaceDescription'>TP-Link Wireless Nano USB Adapter</Data><Data Name='ConnectionMode'>Conexión manual con un perfil</Data><Data Name='ProfileName'>MiPerfil</Data><Data Name='SSID'>MiRed</Data><Data Name='BSSType'>Infrastructure</Data><Data Name='Reason'>El controlador desconectó la red.</Data><Data Name='ConnectionId'>0x4</Data><Data Name='ReasonCode'>0</Data></EventData></Event>
        """;

    [Fact]
    public void ParsesSsidReasonAndLocalTime()
    {
        var d = WlanEventLog.Parse(Disconnect)!;
        Assert.Equal("MiRed", d.Ssid);
        Assert.Equal("El controlador desconectó la red.", d.Reason);
        Assert.Equal(DateTimeKind.Local, d.Time.Kind);
        Assert.Equal(new DateTime(2026, 10, 17, 3, 11, 52, 962, DateTimeKind.Utc).AddTicks(6307), d.Time.ToUniversalTime());
    }

    [Fact]
    public void IgnoresOtherEvents() =>
        Assert.Null(WlanEventLog.Parse(Disconnect.Replace("<EventID>8003<", "<EventID>8001<")));

    [Fact]
    public void MissingFieldsAreEmpty()
    {
        var d = WlanEventLog.Parse("""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>8003</EventID><TimeCreated SystemTime="2026-10-17T03:11:52Z"/></System></Event>
            """)!;
        Assert.Equal("", d.Ssid);
        Assert.Equal("", d.Reason);
    }

    [Theory]
    [InlineData("no es xml")]
    [InlineData("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>8003</EventID></System></Event>")]
    public void RejectsBrokenEvents(string xml) => Assert.Null(WlanEventLog.Parse(xml));
}
