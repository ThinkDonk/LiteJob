using System.Net;
using System.Text;
using System.Text.Json;
using LiteJob.Core.Executors;

namespace LiteJob.Core.Tests;

public class HttpAuthHelperTests
{
    private static HttpAuthHelper CreateHelper()
        => new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static JsonElement ParseAuth(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Login_Flow_Extracts_Token_And_Renders_InjectAs()
    {
        using var server = FakeLoginServer.Start();
        var helper = CreateHelper();
        var auth = ParseAuth($$"""
            {"type":"login","loginUrl":"{{server.Url}}login","loginBody":{"user":"u","pass":"p"},"tokenPath":"$.data.accessToken","injectAs":"Authorization: Bearer {token}"}
            """);

        var (name, value) = await helper.GetHeaderAsync(auth, CancellationToken.None);

        Assert.Equal("Authorization", name);
        Assert.Equal("Bearer tk-123", value);
        Assert.Equal(1, server.Hits);
    }

    [Fact]
    public async Task Missing_TokenPath_Throws_Clear_Error()
    {
        using var server = FakeLoginServer.Start();
        var helper = CreateHelper();
        var auth = ParseAuth($$"""
            {"type":"login","loginUrl":"{{server.Url}}login","loginBody":{},"tokenPath":"$.data.missing.token","injectAs":"X-Token: {token}"}
            """);

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => helper.GetHeaderAsync(auth, CancellationToken.None));

        Assert.Contains("tokenPath", ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task Non_2xx_Login_Throws_With_Status()
    {
        using var server = FakeLoginServer.Start();
        server.StatusCode = 500;
        var helper = CreateHelper();
        var auth = ParseAuth($$"""
            {"type":"login","loginUrl":"{{server.Url}}login","loginBody":{},"tokenPath":"$.data.accessToken"}
            """);

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => helper.GetHeaderAsync(auth, CancellationToken.None));

        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task Unsupported_Type_Throws()
    {
        var helper = CreateHelper();
        var auth = ParseAuth("""{"type":"oauth2","loginUrl":"http://x"}""");

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => helper.GetHeaderAsync(auth, CancellationToken.None));

        Assert.Contains("oauth2", ex.Message);
    }

    private sealed class FakeLoginServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        public string Url { get; }
        public int Hits;
        public int StatusCode { get; set; } = 200;

        private FakeLoginServer(string url, HttpListener listener)
        {
            Url = url;
            _listener = listener;
            _loop = Task.Run(ListenLoopAsync);
        }

        public static FakeLoginServer Start()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = Random.Shared.Next(50000, 54000);
                var url = $"http://localhost:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                    return new FakeLoginServer(url, listener);
                }
                catch (HttpListenerException)
                {
                }
            }
            throw new InvalidOperationException("无法启动模拟登录服务器");
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
                    var body = """{"code":0,"data":{"accessToken":"tk-123","user":"u"}}""";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = StatusCode;
                    context.Response.ContentType = "application/json";
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
}
