using LiteJob.Core.Security;
using Microsoft.AspNetCore.Http;

namespace LiteJob.Web.Middlewares;

/// <summary>/open 路径的 X-API-Key 校验（固定时间比较）。其他路径直通。</summary>
public sealed class ApiKeyMiddleware
{
    private const string HeaderName = "X-API-Key";
    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, string apiKey)
    {
        _next = next;
        _apiKey = apiKey;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/open"))
        {
            await _next(context);
            return;
        }

        var provided = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!ApiKeyComparer.Equals(_apiKey, provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"message\":\"无效的 API Key\"}");
            return;
        }

        await _next(context);
    }
}
