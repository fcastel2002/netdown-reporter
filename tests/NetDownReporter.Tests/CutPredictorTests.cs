namespace NetDownReporter.Tests;

public class CutPredictorTests
{
    // Como en esta PC: cada 4 h, unos 4 s antes cada vez (el reloj del router adelanta).
    static readonly TimeSpan RouterPeriod = TimeSpan.FromHours(4) - TimeSpan.FromSeconds(4);
    static readonly DateTime Last = new(2026, 10, 17, 0, 11, 52);

    static List<DateTime> Cycle(int count) => [.. Enumerable.Range(0, count).Select(i => Last - RouterPeriod * i)];

    [Fact]
    public void FindsRouterCycleWithDrift()
    {
        var s = CutPredictor.Find(Cycle(12), Last.AddMinutes(30))!;
        Assert.Equal(Last, s.Anchor);
        Assert.InRange(s.Period, RouterPeriod - TimeSpan.FromSeconds(1), RouterPeriod + TimeSpan.FromSeconds(1));
        Assert.Equal(Last + RouterPeriod, s.NextAfter(Last.AddMinutes(30)), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PredictsSeveralCyclesAhead()
    {
        var s = CutPredictor.Find(Cycle(12), Last.AddMinutes(1))!;
        // Unas 9 h después (la PC estuvo apagada): el corte de las ~12:11, no el de las ~08:11.
        Assert.Equal(Last + RouterPeriod * 3, s.NextAfter(Last.AddHours(9)), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void NextAfterIsStableWithinAPeriod()
    {
        var s = new CutSchedule(Last, RouterPeriod);
        Assert.Equal(s.NextAfter(Last.AddHours(1)), s.NextAfter(Last.AddHours(3.9)));
    }

    [Fact]
    public void IgnoresOffCycleDisconnects()
    {
        // Un corte suelto después del último del ciclo (p. ej. uno manual) no tiene que ser el ancla.
        var times = Cycle(12);
        times.Add(Last.AddMinutes(25));
        var s = CutPredictor.Find(times, Last.AddMinutes(30))!;
        Assert.Equal(Last, s.Anchor);
        Assert.InRange(s.Period, RouterPeriod - TimeSpan.FromSeconds(1), RouterPeriod + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PrefersLongestPeriodThatFitsAll()
    {
        // Un ciclo de 4 h también encaja en 2 h y 1 h: tiene que quedar 4 h, no 1 h.
        var s = CutPredictor.Find(Cycle(12), Last.AddMinutes(1))!;
        Assert.Equal(4, Math.Round(s.Period.TotalHours));
    }

    [Fact]
    public void NoCycleWithFewDisconnects() => Assert.Null(CutPredictor.Find(Cycle(3), Last.AddMinutes(1)));

    [Fact]
    public void NoCycleWithIrregularDisconnects()
    {
        DateTime[] times = [Last, Last.AddMinutes(-97), Last.AddMinutes(-311), Last.AddMinutes(-529), Last.AddMinutes(-1003), Last.AddMinutes(-1650)];
        Assert.Null(CutPredictor.Find(times, Last.AddMinutes(1)));
    }

    [Fact]
    public void NoCycleWhenLastCutIsTooOld()
    {
        // Si hace más de un día que no hay cortes del ciclo, el router pudo haberse reiniciado.
        Assert.Null(CutPredictor.Find(Cycle(12), Last.AddHours(25)));
    }
}
