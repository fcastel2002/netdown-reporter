namespace NetDownReporter.Tests;

public class OutageTrackerTests
{
    static readonly DateTime T0 = new(2026, 10, 15, 21, 0, 0);
    static DateTime At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void SingleFailureIsIgnored()
    {
        var t = new OutageTracker();
        Assert.Null(t.Update(At(0), Cause.Isp));
        Assert.Null(t.Update(At(5), null));
        Assert.Null(t.Current);
    }

    [Fact]
    public void OutageStartsAtFirstFailureAndEndsAtFirstSuccess()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Isp);
        var started = t.Update(At(5), Cause.Isp);
        Assert.True(started is ({ Start: var s }, true) && s == At(0));

        Assert.Null(t.Update(At(10), Cause.Router));
        var ended = t.Update(At(15), null);

        Assert.NotNull(ended);
        var (o, isStart) = ended.Value;
        Assert.False(isStart);
        Assert.Equal(At(15), o.End);
        Assert.Equal(TimeSpan.FromSeconds(15), o.Duration);
        Assert.Equal(Cause.Isp, o.Cause);
        Assert.Equal([Cause.Isp, Cause.Router], o.Causes);
        Assert.Null(t.Current);
    }

    [Fact]
    public void CauseIsTheOneAtConfirmation()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Isp);
        var started = t.Update(At(5), Cause.Wifi);
        Assert.Equal(Cause.Wifi, started!.Value.Outage.Cause);
        Assert.Equal(At(0), started.Value.Outage.Start);
    }

    [Fact]
    public void BlipDoesNotLeakCausesIntoNextOutage()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Router);
        t.Update(At(5), null);
        t.Update(At(10), Cause.Isp);
        t.Update(At(15), Cause.Isp);
        var ended = t.Update(At(20), null)!.Value.Outage;
        Assert.Equal([Cause.Isp], ended.Causes);
        Assert.Equal(At(10), ended.Start);
    }

    [Fact]
    public void InterruptClosesOpenOutageWithNote()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Wifi);
        t.Update(At(5), Cause.Wifi);
        t.SetDetail("SSID=x");
        var closed = t.Interrupt(At(30), "PC suspendida");
        Assert.NotNull(closed);
        Assert.Equal(At(30), closed.End);
        Assert.Equal("PC suspendida", closed.Note);
        Assert.Equal("SSID=x", closed.Detail);
        Assert.Null(t.Current);
        Assert.Null(t.Interrupt(At(40), "otra vez"));
    }

    [Fact]
    public void WifiOutageIsConfirmedOnFirstFailure()
    {
        var t = new OutageTracker();
        var started = t.Update(At(0), Cause.Wifi);
        Assert.True(started is ({ Start: var s, Cause: Cause.Wifi }, true) && s == At(0));
        Assert.False(t.Pending);
    }

    [Fact]
    public void PendingWhileWaitingForConfirmation()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Isp);
        Assert.True(t.Pending);
        t.Update(At(1), Cause.Isp);
        Assert.False(t.Pending);
    }

    [Fact]
    public void InterruptResetsPendingFailures()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Isp);
        t.Interrupt(At(1), "x");
        Assert.Null(t.Update(At(60), Cause.Isp));
    }

    [Fact]
    public void InterruptAppendsToExistingNote()
    {
        var t = new OutageTracker();
        t.Update(At(0), Cause.Wifi);
        t.Update(At(5), Cause.Wifi);
        t.SetNote("reconexión automática: 1 intento sin éxito");
        Assert.Equal("reconexión automática: 1 intento sin éxito; PC suspendida", t.Interrupt(At(30), "PC suspendida")!.Note);
    }
}
