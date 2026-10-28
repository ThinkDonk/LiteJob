using System.Text;

namespace LiteJob.Core.Text;

public static class CsvBuilder
{
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        var needsQuote = text.Contains(',') || text.Contains('"') || text.Contains('\r') || text.Contains('\n');
        return needsQuote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    public static string Build(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(Escape(row[i]));
            }
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    public static byte[] ToUtf8BomBytes(string csv)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
