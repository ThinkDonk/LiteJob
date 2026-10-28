using LiteJob.Core.Scheduling;

namespace LiteJob.Core.Tests;

public class TriggerCalcTests
{
    [Fact]
    public void Five_Fields_Get_A_Leading_Seconds_Field()
    {
        var next = TriggerCalc.Next("0 8 * * *", new DateTime(2026, 10, 28, 9, 30, 0));

        Assert.Equal(new DateTime(2026, 10, 29, 8, 0, 0), next);
    }

    [Fact]
    public void Six_Fields_Keep_Seconds_Semantics()
    {
        var next = TriggerCalc.Next("30 0 8 * * *", new DateTime(2026, 10, 28, 9, 30, 0));

        Assert.Equal(new DateTime(2026, 10, 29, 8, 0, 30), next);
    }

    [Fact]
    public void Question_Mark_In_Day_Fields_Is_Accepted()
    {
        var next = TriggerCalc.Next("0 0 8 * * ?", new DateTime(2026, 10, 28, 9, 30, 0));

        Assert.Equal(new DateTime(2026, 10, 29, 8, 0, 0), next);
    }

    [Fact]
    public void Seconds_Step_Cron_Fires_Within_The_Step()
    {
        var now = new DateTime(2026, 10, 28, 10, 0, 1);
        var next = TriggerCalc.Next("*/2 * * * * ?", now);

        Assert.NotNull(next);
        Assert.True((next!.Value - now).TotalSeconds <= 2);
        Assert.Equal(0, next.Value.Second % 2);
    }

    [Fact]
    public void Once_Future_Returns_The_Specified_Time()
    {
        var at = DateTime.Now.AddHours(1);
        var expr = $"once:{at:yyyy-MM-dd HH:mm}";

        var next = TriggerCalc.Next(expr, DateTime.Now);

        Assert.Equal(new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0), next);
    }

    [Fact]
    public void Once_Past_Returns_Null()
    {
        var next = TriggerCalc.Next("once:2000-01-01 10:00", DateTime.Now);

        Assert.Null(next);
    }

    [Fact]
    public void Once_With_Invalid_Time_Throws_FormatException()
    {
        Assert.Throws<FormatException>(() => TriggerCalc.Next("once:not-a-date", DateTime.Now));
    }

    [Fact]
    public void Invalid_Cron_Throws_FormatException()
    {
        Assert.Throws<FormatException>(() => TriggerCalc.Next("bad cron", DateTime.Now));
    }

    [Fact]
    public void Wrong_Field_Count_Throws_FormatException()
    {
        Assert.Throws<FormatException>(() => TriggerCalc.Next("0 8 * *", DateTime.Now));
        Assert.Throws<FormatException>(() => TriggerCalc.Next("0 0 8 * * * 2026", DateTime.Now));
    }

    [Fact]
    public void Empty_Expression_Throws_FormatException()
    {
        Assert.Throws<FormatException>(() => TriggerCalc.Next("  ", DateTime.Now));
    }

    [Fact]
    public void Preview_Returns_Strictly_Increasing_Times()
    {
        var times = TriggerCalc.Preview("*/10 * * * * ?", 5);

        Assert.Equal(5, times.Count);
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] > times[i - 1]);
            Assert.Equal(10, (times[i] - times[i - 1]).TotalSeconds);
        }
        Assert.All(times, t => Assert.True(t > DateTime.Now));
    }

    [Fact]
    public void Preview_Of_Daily_Cron_Returns_Daily_Times()
    {
        var times = TriggerCalc.Preview("0 8 * * *", 3);

        Assert.Equal(3, times.Count);
        Assert.All(times, t => Assert.Equal(8, t.Hour));
        Assert.Equal(TimeSpan.FromDays(1), times[1] - times[0]);
        Assert.Equal(TimeSpan.FromDays(1), times[2] - times[1]);
    }
}
