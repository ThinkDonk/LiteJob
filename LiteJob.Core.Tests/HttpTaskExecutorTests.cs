using System.Net;
using System.Text;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;

namespace LiteJob.Core.Tests;

public class HttpTaskExecutorTests
{
    private readonly HttpTaskExecutor _executor = new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static TaskEntity CreateTask(object paramsObject)
    {
        return new TaskEntity
        {
            Name = "http-test",
            TaskType = "http",
            Cron = "0 0 1 1 * *",
            Params = System.Text.Json.JsonSerializer.Serialize(paramsObject),
            TimeoutSec = 10,
            RetryMax = 0,
            Enabled = 1
        };
    }

    [Fact]
    public async Task Http_200_Returns_Result_With_BodySnapshot()
    {
        using var server = TestHttpServer.Start();
        var task = CreateTask(new { method = "GET", url = $"{server.Url}ping" });

        var result = await _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("\"httpCode\":200", result.ResultJson);
        Assert.Contains("hello-litejob", result.ResultJson);
        Assert.Equal(1, server.Hits);
    }

    [Fact]
    public async Task Http_500_Throws_ExecutorException_With_Snapshot()
    {
        using var server = TestHttpServer.Start();
        server.StatusCode = 500;
        var task = CreateTask(new { method = "GET", url = $"{server.Url}boom" });

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None));

        Assert.Contains("HTTP 500", ex.Message);
        Assert.NotNull(ex.ResultJson);
        Assert.Contains("bodySnapshot", ex.ResultJson);
    }

    [Fact]
    public async Task Http_404_Throws_ExecutorException()
    {
        using var server = TestHttpServer.Start();
        server.StatusCode = 404;
        var task = CreateTask(new { method = "GET", url = $"{server.Url}missing" });

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None));

        Assert.Contains("HTTP 404", ex.Message);
    }

    [Fact]
    public async Task SuccessContains_Hit_Is_Successful()
    {
        using var server = TestHttpServer.Start();
        server.Body = "ABCDEFG";
        var task = CreateTask(new { method = "GET", url = $"{server.Url}check", successContains = "CDE" });

        var result = await _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("\"httpCode\":200", result.ResultJson);
        Assert.Contains("ABCDEFG", result.ResultJson);
    }

    [Fact]
    public async Task SuccessContains_Miss_Throws_ExecutorException()
    {
        using var server = TestHttpServer.Start();
        server.Body = "ABCDEFG";
        var task = CreateTask(new { method = "GET", url = $"{server.Url}check", successContains = "XYZ" });

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None));

        Assert.Contains("successContains 断言失败", ex.Message);
    }

    [Fact]
    public async Task Missing_Url_Throws_ExecutorException()
    {
        var task = CreateTask(new { method = "GET" });

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None));

        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public async Task Post_With_Body_And_Headers_Is_Sent()
    {
        using var server = TestHttpServer.Start();
        string? receivedBody = null;
        string? receivedHeader = null;
        server.OnRequest = context =>
        {
            receivedHeader = context.Request.Headers["X-Custom"];
            using var reader = new StreamReader(context.Request.InputStream);
            receivedBody = reader.ReadToEnd();
        };
        var task = CreateTask(new
        {
            method = "POST",
            url = $"{server.Url}submit",
            headers = new Dictionary<string, string> { ["X-Custom"] = "v1" },
            body = "{\"a\":1}"
        });

        var result = await _executor.ExecuteAsync(task, DateTime.Now, CancellationToken.None);

        Assert.Contains("\"httpCode\":200", result.ResultJson);
        Assert.Equal("v1", receivedHeader);
        Assert.Equal("{\"a\":1}", receivedBody);
    }
}

internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;

    public string Url { get; }
    public int Hits;

    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "hello-litejob";
    public Action<HttpListenerContext>? OnRequest { get; set; }

    private TestHttpServer(string url, HttpListener listener)
    {
        Url = url;
        _listener = listener;
        _loop = Task.Run(ListenLoopAsync);
    }

    public static TestHttpServer Start()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = Random.Shared.Next(49000, 53000);
            var url = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return new TestHttpServer(url, listener);
            }
            catch (HttpListenerException)
            {
            }
        }
        throw new InvalidOperationException("无法启动测试 HTTP 服务器");
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
                Interlocked.Increment(ref Hits);
                OnRequest?.Invoke(context);
                var bytes = Encoding.UTF8.GetBytes(Body);
                context.Response.StatusCode = StatusCode;
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
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }
}
