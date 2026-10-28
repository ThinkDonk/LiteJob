using LiteJob.Core.Text;

namespace LiteJob.Core.Tests;

public class CsvBuilderTests
{
    [Fact]
    public void Plain_Value_Is_Unchanged()
    {
        Assert.Equal("hello", CsvBuilder.Escape("hello"));
        Assert.Equal("", CsvBuilder.Escape(null));
        Assert.Equal("", CsvBuilder.Escape(""));
    }

    [Fact]
    public void Value_With_Comma_Is_Quoted()
    {
        Assert.Equal("\"a,b\"", CsvBuilder.Escape("a,b"));
    }

    [Fact]
    public void Value_With_Quote_Is_Quoted_And_Doubled()
    {
        Assert.Equal("\"he said \"\"hi\"\"\"", CsvBuilder.Escape("he said \"hi\""));
    }

    [Fact]
    public void Value_With_NewLine_Is_Quoted()
    {
        Assert.Equal("\"line1\r\nline2\"", CsvBuilder.Escape("line1\r\nline2"));
        Assert.Equal("\"line1\nline2\"", CsvBuilder.Escape("line1\nline2"));
    }

    [Fact]
    public void Comma_Quote_NewLine_Combined_Is_Fully_Escaped()
    {
        var value = "任务, \"A\"\n第二行";
        var escaped = CsvBuilder.Escape(value);
        Assert.StartsWith("\"", escaped);
        Assert.EndsWith("\"", escaped);
        Assert.Equal("\"任务, \"\"A\"\"\n第二行\"", escaped);
    }

    [Fact]
    public void Build_Joins_Cells_With_Comma_And_Rows_With_Crlf()
    {
        var csv = CsvBuilder.Build(new[]
        {
            new[] { "任务名", "状态" },
            new[] { "t1", "success" }
        });
        Assert.Equal("任务名,状态\r\nt1,success\r\n", csv);
    }

    [Fact]
    public void ToUtf8BomBytes_StartsWith_EfBbBf()
    {
        var bytes = CsvBuilder.ToUtf8BomBytes("任务名,状态\r\nt1,success\r\n");

        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var body = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("任务名", body);
        Assert.Contains("t1,success", body);
    }

    [Fact]
    public void Built_Csv_Is_Excel_Friendly_For_Special_Names()
    {
        var csv = CsvBuilder.Build(new[]
        {
            new[] { "任务名", "状态" },
            new[] { "报表, \"日\"", "failed" }
        });
        var bytes = CsvBuilder.ToUtf8BomBytes(csv);

        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Contains("\"报表, \"\"日\"\"\"", csv);
    }
}
