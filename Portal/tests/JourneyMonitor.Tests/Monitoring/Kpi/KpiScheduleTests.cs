using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

public class KpiScheduleTests
{
    private static readonly DateTimeOffset Tick = new(2026, 11, 8, 13, 18, 30, TimeSpan.Zero);

    [Fact]
    public void NeverEvaluated_IsDue() => Assert.True(KpiSchedule.IsDue(null, Tick, TimeSpan.FromMinutes(1)));

    [Fact]
    public void PreviousRunLandedMillisecondsAfterTheTick_NextTickIsStillDue()
    {
        var previous = Tick.AddMinutes(-1).AddMilliseconds(52);
        Assert.True(KpiSchedule.IsDue(previous, Tick, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SameTickRunTwice_IsNotDue()
    {
        Assert.False(KpiSchedule.IsDue(Tick.AddSeconds(-1), Tick, TimeSpan.FromMinutes(1)));
        Assert.False(KpiSchedule.IsDue(Tick.AddMinutes(-4), Tick, TimeSpan.FromMinutes(5)));
    }
}
