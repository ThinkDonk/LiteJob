using System.Net;
using System.Text;
using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

/// <summary>bodySnapshot 脱敏集成：$cred 解析出的值/auth token 不得进 t_task_run.result（设计 §6）。</summary>
public class HttpExecutorMaskingTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<HttpTaskExecutor> CreateExecutorAsync(string credentialName, string token)
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await repository.InsertAsync(credentialName, JsonSerializer.Serialize(new { token }), null, "test");
        var resolver = new CredentialResolver(repository);
        return new HttpTaskExecutor(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, resolver);
    }

    private static TaskEntity CreateTask(string paramsJson)
    {
        return new TaskEntity
        {
            Name = "mask-test",
            TaskType = "http",
            Cron = "0 0 1 1 * *",
            Params = paramsJson,
            TimeoutSec = 10,
            RetryMax = 0,
            Enabled = 1
        };
    }

    [Fact]
    public async Task Echoed_Credential_Token_Is_Masked_In_BodySnapshot()
    {
        var token = "echo-secret-12345";
        using var server = EchoServer.Start();
        var executor = await CreateExecutorAsync("echo-cred", token);
        var task = CreateTask(JsonSerializer.Serialize(new
        {
            method = "GET",
            url = server.Url + "echo",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer $cred:echo-cred.token" }
        }));

        var result = await executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("***", result.ResultJson);
        Assert.DoesNotContain(token, result.ResultJson);
        Assert.Contains("\"requestHeaders\":{\"Authorization\":\"***\"}", result.ResultJson);
        Assert.Contains("echo: Bearer ***", result.ResultJson);
    }

    [Fact]
    public async Task BodySnapshot_Stays_Original_When_No_Sensitive_Value()
    {
        using var server = EchoServer.Start();
        var executor = await CreateExecutorAsync("unused", "not-used-token");
        var task = CreateTask(JsonSerializer.Serialize(new
        {
            method = "GET",
            url = server.Url + "echo",
            headers = new Dictionary<string, string> { ["X-Custom"] = "plain-value" }
        }));

        var result = await executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("\"bodySnapshot\":\"echo: none\"", result.ResultJson);
        Assert.DoesNotContain("***", result.ResultJson);
        Assert.Contains("\"X-Custom\":\"plain-value\"", result.ResultJson);
    }

    [Fact]
    public async Task Auth_Block_Token_Is_Masked_In_BodySnapshot()
    {
        using var server = EchoServer.Start();
        using var echoServer = EchoServer.Start();
        var helper = new HttpAuthHelper(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        var executor = new HttpTaskExecutor(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, null, helper);
        var token = "login-secret-98765";
        var task = CreateTask(JsonSerializer.Serialize(new
        {
            method = "GET",
            url = echoServer.Url + "echo",
            auth = new
            {
                type = "login",
                loginUrl = server.Url + "login",
                loginBody = new { },
                tokenPath = "$.data.accessToken",
                injectAs = "Authorization: Bearer {token}"
            }
        }));

        var result = await executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("***", result.ResultJson);
        Assert.DoesNotContain(token, result.ResultJson);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

/// <summary>响应体回显收到的 Authorization 头，用于验证快照脱敏。</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly HttpListener _listener;

    public string Url { get; }

    private EchoServer(string url, HttpListener listener)
    {
        Url = url;
        _listener = listener;
        _ = Task.Run(ListenLoopAsync);
    }

    public static EchoServer Start()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = Random.Shared.Next(58000, 62000);
            var url = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return new EchoServer(url, listener);
            }
            catch (HttpListenerException)
            {
            }
        }
        throw new InvalidOperationException("无法启动 echo 测试服务器");
    }

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            try
            {
                string body;
                if (context.Request.Url!.AbsolutePath == "/login")
                {
                    context.Response.ContentType = "application/json";
                    body = """{"code":0,"data":{"accessToken":"login-secret-98765"}}""";
                }
                else
                {
                    context.Response.ContentType = "text/plain";
                    var auth = context.Request.Headers["Authorization"] ?? "none";
                    body = $"echo: {auth}";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch
            {
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
