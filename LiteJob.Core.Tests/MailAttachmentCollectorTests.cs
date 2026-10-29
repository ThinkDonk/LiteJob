using System.Net;
using System.Text;
using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class MailAttachmentCollectorTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<(MailAttachmentCollector Collector, string TempDir)> CreateCollectorAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        var collector = new MailAttachmentCollector(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, repository);
        return (collector, Path.Combine(Path.GetTempPath(), $"litejob_m4_att_{Guid.NewGuid():N}"));
    }

    private static JsonElement ParseAttachments(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task File_Attachment_Existing_Is_Collected_With_File_Name()
    {
        var (collector, tempDir) = await CreateCollectorAsync();
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "报表数据.txt");
        await File.WriteAllTextAsync(filePath, "内容");

        var attachments = ParseAttachments($$"""
            [{"type":"file","path":"{{filePath.Replace("\\", "\\\\")}}","optional":false}]
            """);

        var collected = await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        var item = Assert.Single(collected);
        Assert.Equal("报表数据.txt", item.Name);
        Assert.Equal(filePath, item.FilePath);
    }

    [Fact]
    public async Task File_Missing_Optional_True_Is_Skipped()
    {
        var (collector, tempDir) = await CreateCollectorAsync();
        Directory.CreateDirectory(tempDir);
        var missing = Path.Combine(tempDir, "不存在.xlsx");
        var existing = Path.Combine(tempDir, "存在.txt");
        await File.WriteAllTextAsync(existing, "x");

        var attachments = ParseAttachments($$"""
            [
              {"type":"file","path":"{{missing.Replace("\\", "\\\\")}}","optional":true},
              {"type":"file","path":"{{existing.Replace("\\", "\\\\")}}","optional":false}
            ]
            """);

        var collected = await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        var item = Assert.Single(collected);
        Assert.Equal("存在.txt", item.Name);
    }

    [Fact]
    public async Task File_Missing_Optional_False_Throws_With_Path()
    {
        var (collector, tempDir) = await CreateCollectorAsync();
        Directory.CreateDirectory(tempDir);
        var missing = Path.Combine(tempDir, "必须存在.xlsx");

        var attachments = ParseAttachments($$"""
            [{"type":"file","path":"{{missing.Replace("\\", "\\\\")}}","optional":false}]
            """);

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None));

        Assert.Contains("附件文件不存在", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task Http_200_Is_Downloaded_To_Temp_File()
    {
        var (collector, _) = await CreateCollectorAsync();
        using var server = TestDownloadServer.Start();
        server.StatusCode = 200;
        server.Body = Encoding.UTF8.GetBytes(" attachment-bytes ");

        var attachments = ParseAttachments($$"""
            [{"type":"http","url":"{{server.Url}}files/日报数据.xlsx","optional":false}]
            """);

        var collected = await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        var item = Assert.Single(collected);
        Assert.Equal("日报数据.xlsx", item.Name);
        Assert.True(File.Exists(item.FilePath));
        Assert.Equal(" attachment-bytes ", await File.ReadAllTextAsync(item.FilePath));
        File.Delete(item.FilePath);
    }

    [Fact]
    public async Task Http_404_Optional_True_Is_Skipped()
    {
        var (collector, tempDir) = await CreateCollectorAsync();
        Directory.CreateDirectory(tempDir);
        var existing = Path.Combine(tempDir, "a.txt");
        await File.WriteAllTextAsync(existing, "x");
        using var server = TestDownloadServer.Start();
        server.StatusCode = 404;

        var attachments = ParseAttachments($$"""
            [
              {"type":"http","url":"{{server.Url}}missing","optional":true},
              {"type":"file","path":"{{existing.Replace("\\", "\\\\")}}","optional":false}
            ]
            """);

        var collected = await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        var item = Assert.Single(collected);
        Assert.Equal("a.txt", item.Name);
    }

    [Fact]
    public async Task Http_404_Optional_False_Throws_With_Status()
    {
        var (collector, _) = await CreateCollectorAsync();
        using var server = TestDownloadServer.Start();
        server.StatusCode = 404;

        var attachments = ParseAttachments($$"""
            [{"type":"http","url":"{{server.Url}}missing","optional":false}]
            """);

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None));

        Assert.Equal("附件下载失败 HTTP 404", ex.Message);
    }

    [Fact]
    public async Task Http_500_Optional_True_Still_Throws()
    {
        var (collector, _) = await CreateCollectorAsync();
        using var server = TestDownloadServer.Start();
        server.StatusCode = 500;

        var attachments = ParseAttachments($$"""
            [{"type":"http","url":"{{server.Url}}boom","optional":true}]
            """);

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None));

        Assert.Equal("附件下载失败 HTTP 500", ex.Message);
    }

    [Fact]
    public async Task CredentialId_Injects_Bearer_Token_From_Key_Field()
    {
        var (collector, _) = await CreateCollectorAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await repository.InsertAsync("wms-report", "{\"key\":\"tk-wms-1\"}", null, "test");
        using var server = TestDownloadServer.Start();
        string? receivedAuth = null;
        server.OnRequest = ctx => { receivedAuth = ctx.Request.Headers["Authorization"]; };

        var attachments = ParseAttachments($$"""
            [{"type":"http","url":"{{server.Url}}f","optional":false,"credentialId":"wms-report"}]
            """);

        await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        Assert.Equal("Bearer tk-wms-1", receivedAuth);
    }

    [Fact]
    public async Task CredentialId_As_Number_Id_Injects_Bearer_Token()
    {
        var (collector, _) = await CreateCollectorAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        var credentialId = await repository.InsertAsync("numeric-cred", "{\"token\":\"tk-by-id-7\"}", null, "test");
        using var server = TestDownloadServer.Start();
        string? receivedAuth = null;
        server.OnRequest = ctx => { receivedAuth = ctx.Request.Headers["Authorization"]; };

        var attachments = ParseAttachments($$"""
            [{"type":"http","url":"{{server.Url}}f","optional":false,"credentialId":{{credentialId}}}]
            """);

        await collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None);

        Assert.True(credentialId > 0);
        Assert.Equal("Bearer tk-by-id-7", receivedAuth);
    }

    [Fact]
    public async Task CredentialId_Boolean_Throws_Clear_Error()
    {
        var (collector, _) = await CreateCollectorAsync();
        using var server = TestDownloadServer.Start();

        var attachments = ParseAttachments("""
            [{"type":"http","url":"SERVER_PLACEHOLDER","optional":false,"credentialId":true}]
            """.Replace("SERVER_PLACEHOLDER", server.Url + "f"));

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None));

        Assert.Equal("credentialId 仅支持凭证名称(字符串)或数字 id", ex.Message);
    }

    [Fact]
    public async Task Unknown_Type_Throws()
    {
        var (collector, _) = await CreateCollectorAsync();
        var attachments = ParseAttachments("""[{"type":"ftp","path":"x"}]""");

        var ex = await Assert.ThrowsAsync<ExecutorException>(
            () => collector.CollectAsync(attachments, DateTime.Now, CancellationToken.None));

        Assert.Contains("ftp", ex.Message);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

internal sealed class TestDownloadServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;

    public string Url { get; }
    public int StatusCode { get; set; } = 200;
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Action<HttpListenerContext>? OnRequest { get; set; }

    private TestDownloadServer(string url, HttpListener listener)
    {
        Url = url;
        _listener = listener;
        _loop = Task.Run(ListenLoopAsync);
    }

    public static TestDownloadServer Start()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = Random.Shared.Next(54000, 58000);
            var url = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return new TestDownloadServer(url, listener);
            }
            catch (HttpListenerException)
            {
            }
        }
        throw new InvalidOperationException("无法启动附件下载测试服务器");
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
                OnRequest?.Invoke(context);
                context.Response.StatusCode = StatusCode;
                context.Response.ContentType = "application/octet-stream";
                if (StatusCode == 200)
                {
                    context.Response.ContentLength64 = Body.Length;
                    await context.Response.OutputStream.WriteAsync(Body);
                }
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
