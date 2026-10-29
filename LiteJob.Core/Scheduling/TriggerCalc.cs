using Cronos;

namespace LiteJob.Core.Scheduling;

public static class TriggerCalc
{
    public static DateTime? Next(string expr, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new FormatException("触发表达式不能为空");

        var trimmed = expr.Trim();
        if (trimmed.StartsWith("once:", StringComparison.OrdinalIgnoreCase))
        {
            var timeText = trimmed["once:".Length..].Trim();
            if (!DateTime.TryParse(timeText, out var at))
                throw new FormatException($"once 时间格式非法：{timeText}");
            return at > now ? at : null;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var withSeconds = fields.Length == 5 ? new[] { "0" }.Concat(fields) : fields;
        var normalized = string.Join(" ", withSeconds.Select(f => f == "?" ? "*" : f));
        if (normalized.Split(' ').Length != 6)
            throw new FormatException($"cron 字段数应为 5 或 6，实际 {fields.Length}：{trimmed}");

        try
        {
            var cron = CronExpression.Parse(normalized, CronFormat.IncludeSeconds);
            return cron.GetNextOccurrence(now.ToUniversalTime(), TimeZoneInfo.Local)?.ToLocalTime();
        }
        catch (CronFormatException ex)
        {
            throw new FormatException($"cron 表达式非法：{trimmed}（{ex.Message}）", ex);
        }
    }

    /// <summary>
    /// 未来 count 次触发预览。once 表达式特殊处理：未过期返回单元素数组，已过期返回空数组；
    /// cron 返回未来 count 个触发点。非法表达式抛 FormatException。
    /// </summary>
    public static List<DateTime> Preview(string expr, int count)
    {
        var trimmed = expr?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new FormatException("触发表达式不能为空");
        }

        if (trimmed.StartsWith("once:", StringComparison.OrdinalIgnoreCase))
        {
            var onceTime = Next(trimmed, DateTime.Now);
            return onceTime is null ? new List<DateTime>() : new List<DateTime> { onceTime.Value };
        }

        var result = new List<DateTime>();
        var now = DateTime.Now;
        for (var i = 0; i < count; i++)
        {
            var next = Next(trimmed, now) ?? throw new FormatException($"表达式已无后续触发时间：{trimmed}");
            result.Add(next);
            now = next.AddSeconds(1);
        }
        return result;
    }
}
