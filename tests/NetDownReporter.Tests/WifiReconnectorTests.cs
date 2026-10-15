namespace NetDownReporter.Tests;

public class WifiReconnectorTests
{
    static readonly DateTime T0 = new(2026, 10, 15, 21, 0, 0);
    static DateTime At(int seconds) => T0.AddSeconds(seconds);
    static readonly Outage Open = new(T0, null, Cause.Wifi, [Cause.Wifi]);

    [Fact]
    public void FirstAttemptIsImmediateThenBacksOff()
    {
        var r = new WifiReconnector();
        Assert.True(r.Update(At(5), Open, Cause.Wifi));
        r.Completed(At(9), null);

        Assert.False(r.Update(At(10), Open, Cause.Wifi));
        Assert.True(r.Update(At(20), Open, Cause.Wifi));    // +10 s
        r.Completed(At(24), null);
        Assert.False(r.Update(At(50), Open, Cause.Wifi));
        Assert.True(r.Update(At(55), Open, Cause.Wifi));    // +30 s
        r.Completed(At(59), null);
        Assert.False(r.Update(At(115), Open, Cause.Wifi));
        Assert.True(r.Update(At(120), Open, Cause.Wifi));   // +60 s
        r.Completed(At(124), null);
        Assert.True(r.Update(At(185), Open, Cause.Wifi));   // el último se repite
        Assert.Equal(5, r.Attempts);
    }

    [Fact]
    public void NoNewAttemptWhileOneIsInFlight()
    {
        var r = new WifiReconnector();
        Assert.True(r.Update(At(5), Open, Cause.Wifi));
        Assert.False(r.Update(At(10), Open, Cause.Wifi));
        Assert.False(r.Update(At(60), Open, Cause.Wifi));
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public void OnlyRetriesWhileTheFailureIsWifi()
    {
        var r = new WifiReconnector();
        var routerOutage = Open with { Cause = Cause.Router };
        Assert.False(r.Update(At(5), routerOutage, Cause.Router));
        Assert.True(r.Update(At(10), routerOutage, Cause.Wifi));
        r.Completed(At(14), null);
        Assert.False(r.Update(At(30), routerOutage, Cause.Isp));
    }

    [Fact]
    public void DisabledNeverAttempts()
    {
        var r = new WifiReconnector { Enabled = false };
        Assert.False(r.Update(At(5), Open, Cause.Wifi));
        Assert.Null(r.Note(ended: true));
    }

    [Fact]
    public void NoOutageResetsState()
    {
        var r = new WifiReconnector();
        r.Update(At(5), Open, Cause.Wifi);
        r.Completed(At(9), "WlanConnect error 1168");
        Assert.False(r.Update(At(10), null, null));
        Assert.Equal(0, r.Attempts);
        Assert.Null(r.Note());
        // Un corte nuevo empieza intentando enseguida.
        Assert.True(r.Update(At(15), Open, Cause.Wifi));
    }

    [Fact]
    public void LateCompletionAfterResetIsIgnored()
    {
        var r = new WifiReconnector();
        r.Update(At(5), Open, Cause.Wifi);
        r.Reset();
        r.Completed(At(9), "error");
        Assert.True(r.Update(At(10), Open, Cause.Wifi));
    }

    [Fact]
    public void NoteDescribesFailureWithLastError()
    {
        var r = new WifiReconnector();
        r.Update(At(5), Open, Cause.Wifi);
        r.Completed(At(9), "WlanConnect error 1168");
        r.Update(At(20), Open, Cause.Wifi);
        r.Completed(At(24), null);
        Assert.Equal("reconexión automática: 2 intentos sin éxito (WlanConnect error 1168)", r.Note());
    }

    [Fact]
    public void NoteSaysRecoveredWhenOutageEnds()
    {
        var r = new WifiReconnector();
        r.Update(At(5), Open, Cause.Wifi);
        r.Completed(At(9), null);
        Assert.Equal("reconexión automática: WiFi recuperado tras 1 intento", r.Note(ended: true));
    }

    [Fact]
    public void NoteSaysRecoveredWhenCauseMovesPastWifi()
    {
        // El WiFi volvió pero el router todavía no da internet: la reconexión sí funcionó.
        var r = new WifiReconnector();
        r.Update(At(5), Open, Cause.Wifi);
        r.Completed(At(9), null);
        r.Update(At(10), Open, Cause.Isp);
        Assert.Equal("reconexión automática: WiFi recuperado tras 1 intento", r.Note());
    }
}
