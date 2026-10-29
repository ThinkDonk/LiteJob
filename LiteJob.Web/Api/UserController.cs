using System.Security.Claims;
using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

public sealed record UserCreateRequest(string Username, string Password, string Role);
public sealed record UserUpdateRequest(string? Password, string? Role, bool? Enabled);

[ApiController]
[Route("api/users")]
[Authorize(Roles = "admin")]
public sealed class UserController : ControllerBase
{
    private readonly UserRepository _repository;
    private readonly AuditLogRepository _auditRepository;
    private readonly ILogger<UserController> _logger;

    public UserController(UserRepository repository, AuditLogRepository auditRepository,
        ILogger<UserController> logger)
    {
        _repository = repository;
        _auditRepository = auditRepository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await _repository.ListAsync();
        return Ok(new
        {
            items = items.Select(u => new
            {
                u.Id,
                u.Username,
                u.Role,
                u.Enabled,
                u.CreatedAt
            })
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateRequest request)
    {
        var username = request.Username?.Trim() ?? "";
        if (username.Length == 0)
        {
            return BadRequest(new { message = "用户名不能为空" });
        }
        if (request.Role is not ("admin" or "viewer"))
        {
            return BadRequest(new { message = "角色仅支持 admin/viewer" });
        }
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8)
        {
            return BadRequest(new { message = "密码至少 8 位" });
        }
        if (await _repository.GetByUsernameAsync(username) is not null)
        {
            return BadRequest(new { message = "用户名已存在" });
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 10);
        var id = await _repository.InsertAsync(username, hash, request.Role);
        await WriteAuditAsync("create", username, $"role={request.Role}");
        return CreatedAtAction(nameof(List), new { id }, new { id });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] UserUpdateRequest request)
    {
        var user = await _repository.GetByIdAsync(id);
        if (user is null)
        {
            return NotFound(new { message = "用户不存在" });
        }
        if (request.Role is not null && request.Role is not ("admin" or "viewer"))
        {
            return BadRequest(new { message = "角色仅支持 admin/viewer" });
        }

        var changes = new List<string>();

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (request.Password.Length < 8)
            {
                return BadRequest(new { message = "密码至少 8 位" });
            }
            passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 10);
            changes.Add("password");
        }

        int? enabled = request.Enabled.HasValue ? (request.Enabled.Value ? 1 : 0) : null;
        if (enabled.HasValue)
        {
            if (IsSelf(user) && enabled.Value == 0)
            {
                return BadRequest(new { message = "不能停用自己的账号" });
            }
            if (user.Role == "admin" && user.Enabled == 1 && enabled.Value == 0
                && await _repository.CountEnabledAdminsAsync() <= 1)
            {
                return BadRequest(new { message = "不能停用最后一个 admin" });
            }
            if (enabled.Value != user.Enabled)
            {
                changes.Add($"enabled={enabled.Value}");
            }
        }

        string? role = request.Role;
        if (role is not null && role != user.Role)
        {
            if (IsSelf(user) && role != "admin")
            {
                return BadRequest(new { message = "不能降级自己的 admin 角色" });
            }
            if (user.Role == "admin" && role != "admin" && await _repository.CountEnabledAdminsAsync() <= 1)
            {
                return BadRequest(new { message = "不能降级最后一个 admin" });
            }
            changes.Add($"role={role}");
        }

        await _repository.UpdateAsync(id, passwordHash, role, enabled);
        await WriteAuditAsync("update", user.Username, changes.Count > 0 ? string.Join(",", changes) : "无变更");
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var user = await _repository.GetByIdAsync(id);
        if (user is null)
        {
            return NotFound(new { message = "用户不存在" });
        }
        if (IsSelf(user))
        {
            return BadRequest(new { message = "不能删除自己的账号" });
        }
        if (user.Role == "admin" && user.Enabled == 1 && await _repository.CountEnabledAdminsAsync() <= 1)
        {
            return BadRequest(new { message = "不能删除最后一个 admin" });
        }

        await _repository.DeleteAsync(id);
        await WriteAuditAsync("delete", user.Username, $"role={user.Role}");
        return Ok(new { ok = true });
    }

    private bool IsSelf(UserEntity user)
        => user.Username == CurrentUsername();

    private string CurrentUsername()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private async Task WriteAuditAsync(string action, string targetName, string detail)
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _auditRepository.InsertAsync(CurrentUsername(), action, "user", null, targetName, detail, ip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用户管理审计写入失败");
        }
    }
}
