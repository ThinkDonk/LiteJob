using System.Text.Json;
using LiteJob.Core.Entities;
using LiteJob.Core.Interpolate;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace LiteJob.Core.Executors;

public sealed class MailTaskExecutor : ITaskExecutor
{
    private const int SmtpTimeoutMs = 30000;

    private readonly MailAttachmentCollector _collector;
    private readonly ConfigRepository _configRepository;
    private readonly CryptoService _cryptoService;
    private readonly CredentialResolver _credentialResolver;
    private readonly ILogger<MailTaskExecutor> _logger;

    public MailTaskExecutor(MailAttachmentCollector collector, ConfigRepository configRepository,
        CryptoService cryptoService, CredentialResolver credentialResolver, ILogger<MailTaskExecutor> logger)
    {
        _collector = collector;
        _configRepository = configRepository;
        _cryptoService = cryptoService;
        _credentialResolver = credentialResolver;
        _logger = logger;
    }

    public string TaskType => "mail";

    public async Task<ExecutorResult> ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct)
    {
        var interpolated = TemplateEngine.InterpolateParams(task.Params, triggerAt);
        var resolved = await _credentialResolver.ResolveParamsAsync(interpolated);

        using var doc = JsonDocument.Parse(resolved);
        var root = doc.RootElement;

        var to = ReadStringList(root, "to");
        if (to.Count == 0)
        {
            throw new ExecutorException("mail 参数缺少收件人 to");
        }
        var cc = ReadStringList(root, "cc");
        var subject = ReadString(root, "subject") ?? string.Empty;
        var body = ReadString(root, "body") ?? string.Empty;
        var bodyIsHtml = root.TryGetProperty("bodyIsHtml", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.True;
        var attachmentsEl = root.TryGetProperty("attachments", out var attEl) && attEl.ValueKind == JsonValueKind.Array
            ? attEl.Clone()
            : (JsonElement?)null;

        var collected = attachmentsEl is null
            ? new List<CollectedAttachment>()
            : await _collector.CollectAsync(attachmentsEl.Value, triggerAt, ct);
        var tempFiles = collected.Where(c => c.IsTemp).Select(c => c.FilePath).ToList();
        try
        {
            var smtpResponse = await SendAsync(to, cc, subject, body, bodyIsHtml, collected, ct);

            var resultJson = JsonSerializer.Serialize(new
            {
                attachments = collected.Select(c => new
                {
                    name = c.Name,
                    size = File.Exists(c.FilePath) ? new FileInfo(c.FilePath).Length : 0
                }),
                smtp = smtpResponse
            });
            return new ExecutorResult(resultJson);
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除附件临时文件失败：{File}", tempFile);
                }
            }
        }
    }

    private async Task<string> SendAsync(List<string> to, List<string> cc, string subject, string body,
        bool bodyIsHtml, List<CollectedAttachment> collected, CancellationToken ct)
    {
        var config = await _configRepository.GetAllAsync();
        var host = config.GetValueOrDefault("smtp.host", "");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ExecutorException("SMTP 未配置（smtp.host 为空），请先在系统设置中配置并保存");
        }
        var port = int.TryParse(config.GetValueOrDefault("smtp.port"), out var parsedPort) ? parsedPort : 25;
        var useSsl = config.GetValueOrDefault("smtp.ssl") is "1" or "true";
        var user = config.GetValueOrDefault("smtp.user", "");
        var pass = config.GetValueOrDefault(ConfigRepository.SmtpPassKey, "");
        var fromAddress = config.GetValueOrDefault("smtp.from", "");
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new ExecutorException("SMTP 未配置发件人（smtp.from 为空）");
        }
        var fromName = config.GetValueOrDefault("smtp.from_name", "");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        foreach (var address in to)
        {
            message.To.Add(MailboxAddress.Parse(address));
        }
        foreach (var address in cc)
        {
            message.Cc.Add(MailboxAddress.Parse(address));
        }
        message.Subject = subject;

        var builder = new BodyBuilder();
        if (bodyIsHtml)
        {
            builder.HtmlBody = body;
        }
        else
        {
            builder.TextBody = body;
        }
        foreach (var attachment in collected)
        {
            var part = new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(File.OpenRead(attachment.FilePath), ContentEncoding.Default),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = attachment.Name
            };
            builder.Attachments.Add(part);
        }
        message.Body = builder.ToMessageBody();

        using (message)
        using (var client = new SmtpClient())
        {
            client.Timeout = SmtpTimeoutMs;
            var secureOption = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(host, port, secureOption, ct);
            try
            {
                if (!string.IsNullOrEmpty(user))
                {
                    await client.AuthenticateAsync(user, pass, ct);
                }
                return await client.SendAsync(message, ct);
            }
            finally
            {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
        }
    }

    private static List<string> ReadStringList(JsonElement root, string name)
    {
        var result = new List<string>();
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } address && address.Trim().Length > 0)
                {
                    result.Add(address.Trim());
                }
            }
        }
        return result;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
