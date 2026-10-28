using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;
using LiteJob.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace LiteJob.Web.Api;

public sealed record LoginRequest(string Username, string Password);

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private const string TokenCookieName = "lj_token";

    private readonly SqliteConnectionFactory _factory;
    private readonly LoginThrottler _throttler;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(SqliteConnectionFactory factory, LoginThrottler throttler, IConfiguration configuration, ILogger<AuthController> logger)
    {
        _factory = factory;
        _throttler = throttler;
        _configuration = configuration;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (_throttler.IsLocked(username))
        {
            await WriteAuditAsync(username, "locked", ip, now, targetId: null);
            return StatusCode(429, new { message = "失败次数过多，请15分钟后重试" });
        }

        await using var conn = await _factory.CreateOpenConnectionAsync();
        var user = await conn.QuerySingleOrDefaultAsync<UserEntity>(
            "SELECT id, username, password_hash, role, enabled, created_at FROM t_user WHERE username = @username;",
            new { username });

        var passwordOk = user is not null
            && user.Enabled == 1
            && BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

        if (!passwordOk)
        {
            _throttler.RecordFailure(username);
            await WriteAuditAsync(username, "bad_password", ip, now, user?.Id);
            return Unauthorized(new { message = "用户名或密码错误" });
        }

        _throttler.RecordSuccess(username);

        var token = GenerateToken(user!);
        Response.Cookies.Append(TokenCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddHours(8)
        });

        await WriteAuditAsync(username, "success", ip, now, user!.Id);
        return Ok(new { token });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Ok(new { username, role });
    }

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(TokenCookieName);
        return Ok(new { ok = true });
    }

    private string GenerateToken(UserEntity user)
    {
        var jwtKey = _configuration["Security:JwtKey"] ?? throw new InvalidOperationException("缺少配置 Security:JwtKey");
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task WriteAuditAsync(string username, string detail, string? ip, string now, long? targetId)
    {
        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO t_audit_log(username, action, target_type, target_id, target_name, detail, ip, create_at) " +
                "VALUES(@username, 'login', 'user', @targetId, @username, @detail, @ip, @now);",
                new { username, targetId, detail, ip, now });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入登录审计失败 username={Username}", username);
        }
    }
}
