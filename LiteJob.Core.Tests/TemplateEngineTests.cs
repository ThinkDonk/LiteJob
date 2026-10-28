using LiteJob.Core.Interpolate;

namespace LiteJob.Core.Tests;

public class TemplateEngineTests
{
    private static readonly DateTime TriggerAt = new(2026, 10, 28, 9, 5, 3);

    [Fact]
    public void Date_Variable_Is_Replaced()
    {
        Assert.Equal("2026-10-28", TemplateEngine.Interpolate("{date}", TriggerAt));
    }

    [Fact]
    public void DateTime_Variable_Is_Replaced()
    {
        Assert.Equal("2026-10-28 09:05:03", TemplateEngine.Interpolate("{datetime}", TriggerAt));
    }

    [Fact]
    public void Arbitrary_Format_Patterns_Are_Replaced()
    {
        Assert.Equal("20261028", TemplateEngine.Interpolate("{yyyyMMdd}", TriggerAt));
        Assert.Equal("09", TemplateEngine.Interpolate("{HH}", TriggerAt));
    }

    [Fact]
    public void Mixed_Text_And_Variables_Are_Replaced()
    {
        Assert.Equal("report_2026-10-28_0905.txt", TemplateEngine.Interpolate("report_{date}_{HH}{mm}.txt", TriggerAt));
    }

    [Fact]
    public void Input_Without_Variables_Is_Unchanged()
    {
        Assert.Equal("plain text 123", TemplateEngine.Interpolate("plain text 123", TriggerAt));
        Assert.Equal("braces {} stay", TemplateEngine.Interpolate("braces {} stay", TriggerAt));
        Assert.Equal("{unknown} stays", TemplateEngine.Interpolate("{unknown} stays", TriggerAt));
    }

    [Fact]
    public void Params_Json_Is_Interpolated_Recursively()
    {
        const string json = """
            {"url":"http://wms/api/export?date={date}","headers":{"X-Day":"{yyyyMMdd}"},"body":{"text":"day {date} {HH}"},"list":["{HH}"],"count":5,"keep":"no-var"}
            """;

        var result = TemplateEngine.InterpolateParams(json, TriggerAt);

        Assert.Contains("http://wms/api/export?date=2026-10-28", result);
        Assert.Contains("\"X-Day\":\"20261028\"", result);
        Assert.Contains("day 2026-10-28 09", result);
        Assert.Contains("\"09\"", result);
        Assert.Contains("\"count\":5", result);
        Assert.Contains("\"keep\":\"no-var\"", result);
    }

    [Fact]
    public void Empty_Params_Are_Returned_As_Is()
    {
        Assert.Equal("", TemplateEngine.InterpolateParams("", TriggerAt));
        Assert.Equal("{}", TemplateEngine.InterpolateParams("{}", TriggerAt));
    }
}
