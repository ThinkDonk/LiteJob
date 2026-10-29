using LiteJob.Core.Entities;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

public sealed record ChannelUpsertRequest(
    string Name, string Type, string WebhookUrl, string? Secret,
    string? AtMobiles, string? BodyTemplate, bool Enabled);

[ApiController]
[Route("api/channels")]
public sealed class ChannelController : ControllerBase
{
    private readonly NotifyChannelRepository _repository;
    private readonly NotifySender _sender;

    public ChannelController(NotifyChannelRepository repository, NotifySender sender)
    {
        _repository = repository;
        _sender = sender;
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List()
    {
        var items = await _repository.ListAsync();
        return Ok(new
        {
            items = items.Select(c => new
            {
                c.Id,
                c.Name,
                c.Type,
                c.WebhookUrl,
                secret = string.IsNullOrEmpty(c.Secret) ? null : "已存储",
                c.AtMobiles,
                c.BodyTemplate,
                c.Enabled,
                c.LastStatus,
                c.LastSentAt
            })
        });
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] ChannelUpsertRequest request)
    {
        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var channel = MapToEntity(request);
        channel.CreatedAt = DateTime.Now;
        var id = await _repository.InsertAsync(channel);
        return CreatedAtAction(nameof(List), new { id }, new { id });
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(long id, [FromBody] ChannelUpsertRequest request)
    {
        if (await _repository.GetByIdAsync(id) is null)
        {
            return NotFound(new { message = "通道不存在" });
        }

        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var channel = MapToEntity(request);
        channel.Id = id;
        await _repository.UpdateAsync(channel, request.Secret);
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(long id)
    {
        if (await _repository.GetByIdAsync(id) is null)
        {
            return NotFound(new { message = "通道不存在" });
        }
        await _repository.DeleteAsync(id);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:long}/test")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Test(long id, CancellationToken ct)
    {
        var channel = await _repository.GetByIdAsync(id);
        if (channel is null)
        {
            return NotFound(new { message = "通道不存在" });
        }

        var (ok, error) = await _sender.SendToChannelAsync(channel, NotifyKind.Test, "【LiteJob】通道测试", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ct);
        return Ok(ok ? new { ok = true } : new { ok = false, error });
    }

    private static string? Validate(ChannelUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "通道名称不能为空";
        }
        if (request.Type is not ("wecom" or "dingtalk" or "feishu" or "custom"))
        {
            return $"通道类型不支持：{request.Type}（wecom/dingtalk/feishu/custom）";
        }
        if (string.IsNullOrWhiteSpace(request.WebhookUrl))
        {
            return "webhook_url 不能为空";
        }
        if (!Uri.TryCreate(request.WebhookUrl, UriKind.Absolute, out _))
        {
            return "webhook_url 不是合法 URL";
        }
        return null;
    }

    private static NotifyChannelEntity MapToEntity(ChannelUpsertRequest request)
    {
        return new NotifyChannelEntity
        {
            Name = request.Name.Trim(),
            Type = request.Type,
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret) ? null : request.Secret.Trim(),
            AtMobiles = request.AtMobiles,
            BodyTemplate = request.BodyTemplate,
            Enabled = request.Enabled ? 1 : 0
        };
    }
}
