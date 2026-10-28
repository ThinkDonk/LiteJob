using System.Net;
using System.Text.Json;
using LiteJob.Core.Repositories;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

public sealed record ConfigUpdateRequest(Dictionary<string, string> Values);

[ApiController]
[Route("api/configs")]
[Authorize]
public sealed class ConfigController : ControllerBase
{
    private const string SmtpPassKey = "smtp.pass";
    private const string SmtpPassPlaceholder = "***";

    private static readonly string[] KnownKeys =
    {
        "smtp.host", "smtp.port", "smtp.ssl", "smtp.user", SmtpPassKey,
        "smtp.from", "smtp.from_name",
        "maintenance.run_retention_days", "maintenance.backup_keep"
    };

    private readonly ConfigRepository _repository;

    public ConfigController(ConfigRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var all = await _repository.GetAllAsync();
        var result = KnownKeys.ToDictionary(
            key => key,
            key => key == SmtpPassKey
                ? (all.TryGetValue(key, out var pass) && pass.Length > 0 ? SmtpPassPlaceholder : null)
                : all.TryGetValue(key, out var value) ? value : null);
        return Ok(new { values = result });
    }

    [HttpPut]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update([FromBody] ConfigUpdateRequest request)
    {
        foreach (var (key, value) in request.Values)
        {
            if (!KnownKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = $"未知的配置键：{key}" });
            }
            if (key == SmtpPassKey && (value == SmtpPassPlaceholder || value.Length == 0))
            {
                continue;
            }
            await _repository.SetAsync(key, value);
        }
        return Ok(new { ok = true });
    }

    [HttpPost("smtp-test")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> SmtpTest(CancellationToken ct)
    {
        var config = await _repository.GetAllAsync();
        var host = config.GetValueOrDefault("smtp.host", "");
        if (string.IsNullOrWhiteSpace(host))
        {
            return Ok(new { ok = false, error = "smtp.host 未配置" });
        }
        var port = int.TryParse(config.GetValueOrDefault("smtp.port"), out var parsed) ? parsed : 25;
        var useSsl = config.GetValueOrDefault("smtp.ssl") is "1" or "true";
        var user = config.GetValueOrDefault("smtp.user", "");
        var pass = config.GetValueOrDefault(SmtpPassKey, "");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new SmtpClient { Timeout = 15000 };
        try
        {
            var secureOption = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(host, port, secureOption, timeoutCts.Token);
            try
            {
                if (!string.IsNullOrEmpty(user))
                {
                    await client.AuthenticateAsync(user, pass, timeoutCts.Token);
                }
            }
            finally
            {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
            return Ok(new { ok = true });
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return Ok(new { ok = false, error = "连接超时（15s）" });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }
}
