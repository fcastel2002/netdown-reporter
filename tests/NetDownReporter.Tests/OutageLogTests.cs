namespace NetDownReporter.Tests;

public sealed class OutageLogTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "netdownreporter-tests-" + Guid.NewGuid().ToString("N"));
    readonly OutageLog log;
    static readonly DateTime T0 = new(2026, 10, 15, 21, 0, 0);

    public OutageLogTests() => log = new OutageLog(dir);
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    [Fact]
    public void OutagesRoundTripIncludingSeparatorsAndQuotes()
    {
        var o = new Outage(T0, T0.AddMinutes(3), Cause.Isp, [Cause.Isp, Cause.Router],
            "nota; con \"comillas\"", "SSID=Mi;Red | Señal=80%");
        log.Append(o);
        log.Append(o with { Start = T0.AddDays(1), End = T0.AddDays(1).AddSeconds(30), Note = "" });

        var read = log.ReadOutages();
        Assert.Equal(2, read.Count);
        Assert.Equal(o.Start, read[0].Start);
        Assert.Equal(o.End, read[0].End);
        Assert.Equal(Cause.Isp, read[0].Cause);
        Assert.Equal([Cause.Isp, Cause.Router], read[0].Causes);
        Assert.Equal(o.Note, read[0].Note);
        Assert.Equal(o.Detail, read[0].Detail);
        Assert.Equal(TimeSpan.FromSeconds(30), read[1].Duration);

        var lines = File.ReadAllLines(Path.Combine(dir, "cortes.csv"));
        Assert.Equal("inicio;fin;duracion_seg;causa;causas;nota;detalle", lines[0]);
        Assert.StartsWith("2026-10-15 21:00:00;2026-10-15 21:03:00;180;Isp;Isp+Router;", lines[1]);
    }

    [Fact]
    public void RecoverClosesSessionAndOpenOutageAtLastSeen()
    {
        var open = new Outage(T0.AddMinutes(10), null, Cause.Wifi, [Cause.Wifi]);
        log.SaveState(new OutageLog.State(T0, T0.AddMinutes(12), open));

        log.RecoverFromCrash();

        Assert.Equal([(T0, T0.AddMinutes(12))], log.ReadSessions());
        var o = Assert.Single(log.ReadOutages());
        Assert.Equal(T0.AddMinutes(12), o.End);
        Assert.Contains("de golpe", o.Note);
        Assert.False(File.Exists(Path.Combine(dir, "estado.json")));

        log.RecoverFromCrash();
        Assert.Single(log.ReadSessions());
    }

    [Fact]
    public void RecoverWithoutOpenOutageOnlyClosesSession()
    {
        log.SaveState(new OutageLog.State(T0, T0.AddHours(2), null));
        log.RecoverFromCrash();
        Assert.Single(log.ReadSessions());
        Assert.Empty(log.ReadOutages());
    }

    [Fact]
    public void ReportEmbedsDataWithoutBreakingTheScriptTag()
    {
        log.Append(new Outage(T0, T0.AddMinutes(1), Cause.Router, [Cause.Router], "", "SSID=</script><b>x"));
        var path = Report.Write(log, new Outage(T0.AddHours(1), null, Cause.Isp, [Cause.Isp]), T0, T0.AddHours(2));

        var html = File.ReadAllText(path);
        Assert.DoesNotContain("/*__DATA__*/", html);
        Assert.DoesNotContain("</script><b>", html);
        Assert.Contains("\"generated\":\"2026-10-15T23:00:00\"", html);
        Assert.Contains("\"ongoing\":true", html);
    }

    [Theory]
    [InlineData("a;b;c", new[] { "a", "b", "c" })]
    [InlineData("\"a;b\";\"c\"\"d\";", new[] { "a;b", "c\"d", "" })]
    public void SplitRowHandlesQuotes(string line, string[] expected) =>
        Assert.Equal(expected, OutageLog.SplitRow(line));
}
