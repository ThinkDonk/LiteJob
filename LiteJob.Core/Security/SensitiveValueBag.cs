namespace LiteJob.Core.Security;

/// <summary>
/// 收集单次任务执行中出现的敏感明文值（凭证值/auth token），用于把回显文本（响应快照等）中的它们替换为 ***。
/// 每次任务执行创建一个实例；非线程安全（执行流程内顺序使用）。
/// </summary>
public sealed class SensitiveValueBag
{
    private const int MinLength = 4;

    private readonly List<string> _values = new();

    /// <summary>注册敏感值。null/空/长度小于 4 的值跳过，避免短值误伤正常文本。</summary>
    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinLength)
        {
            return;
        }
        if (!_values.Contains(value))
        {
            _values.Add(value);
        }
    }

    /// <summary>把文本中出现的每个已注册敏感值替换为 ***。对 null/空安全。</summary>
    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text) || _values.Count == 0)
        {
            return text ?? string.Empty;
        }
        var result = text;
        foreach (var value in _values)
        {
            result = result.Replace(value, Masker.MaskedValue);
        }
        return result;
    }
}
