using System.Security.Claims;
using System.Text.Json;
using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace LiteJob.Web.Api;

public sealed record CredentialUpsertRequest(string Name, string? ValueJson, string? Remark);

[ApiController]
[Route("api/credentials")]
public sealed class CredentialController : ControllerBase
{
    private readonly CredentialRepository _repository;

    public CredentialController(CredentialRepository repository)
    {
        _repository = repository;
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
                c.Remark,
                c.UpdatedAt,
                value = "***"
            })
        });
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] CredentialUpsertRequest request)
    {
        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        if (await _repository.GetByNameAsync(request.Name.Trim()) is not null)
        {
            return BadRequest(new { message = "凭证名称已存在" });
        }

        var id = await _repository.InsertAsync(request.Name.Trim(), request.ValueJson!.Trim(),
            request.Remark, User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier));
        return CreatedAtAction(nameof(List), new { id }, new { id });
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(long id, [FromBody] CredentialUpsertRequest request)
    {
        if (await _repository.GetByIdAsync(id) is null)
        {
            return NotFound(new { message = "凭证不存在" });
        }

        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var duplicate = await _repository.GetByNameAsync(request.Name.Trim());
        if (duplicate is not null && duplicate.Id != id)
        {
            return BadRequest(new { message = "凭证名称已存在" });
        }

        await _repository.UpdateAsync(id, request.ValueJson!.Trim(), request.Remark,
            User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier));
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(long id)
    {
        if (await _repository.GetByIdAsync(id) is null)
        {
            return NotFound(new { message = "凭证不存在" });
        }
        await _repository.DeleteAsync(id);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:long}/verify")]
    [Authorize]
    public async Task<IActionResult> Verify(long id)
    {
        string plain;
        try
        {
            plain = await _repository.GetDecryptedJsonByIdAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = "解密失败：" + ex.Message });
        }

        try
        {
            using var doc = JsonDocument.Parse(plain);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Ok(new { ok = false, error = "明文不是 JSON 键值对象" });
            }
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            return Ok(new { ok = true, keys });
        }
        catch (JsonException ex)
        {
            return Ok(new { ok = false, error = "明文 JSON 解析失败：" + ex.Message });
        }
    }

    private static string? Validate(CredentialUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "凭证名称不能为空";
        }
        if (string.IsNullOrWhiteSpace(request.ValueJson))
        {
            return "凭证值不能为空";
        }
        try
        {
            using var _ = JsonDocument.Parse(request.ValueJson);
        }
        catch (JsonException ex)
        {
            return $"凭证值不是合法 JSON：{ex.Message}";
        }
        return null;
    }
}
