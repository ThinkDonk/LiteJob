using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class CredentialResolverTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<CredentialResolver> CreateResolverAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new CredentialResolver(new CredentialRepository(_db.Factory, _crypto));
    }

    private async Task InsertCredentialAsync(CredentialRepository repository, string name, string plainJson)
    {
        await repository.InsertAsync(name, plainJson, null, "test");
    }

    [Fact]
    public async Task Key_Reference_Is_Replaced_With_Decrypted_Value()
    {
        var resolver = await CreateResolverAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await InsertCredentialAsync(repository, "wms-report", "{\"user\":\"wms-user\",\"pass\":\"wms-pass\"}");

        var resolved = await resolver.ResolveAsync("http://wms/api?u=$cred:wms-report.user&p=$cred:wms-report.pass");

        Assert.Equal("http://wms/api?u=wms-user&p=wms-pass", resolved);
    }

    [Fact]
    public async Task Whole_Object_Reference_Returns_Serialized_Json()
    {
        var resolver = await CreateResolverAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await InsertCredentialAsync(repository, "cred-obj", "{\"key\":\"abc\"}");

        var resolved = await resolver.ResolveAsync("payload=$cred:cred-obj");

        Assert.Equal("payload={\"key\":\"abc\"}", resolved);
    }

    [Fact]
    public async Task Missing_Credential_Throws_With_Name()
    {
        var resolver = await CreateResolverAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("u=$cred:no-such-cred.user"));

        Assert.Contains("no-such-cred", ex.Message);
    }

    [Fact]
    public async Task Missing_Key_In_Credential_Throws()
    {
        var resolver = await CreateResolverAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await InsertCredentialAsync(repository, "limited", "{\"user\":\"u\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("$cred:limited.token"));

        Assert.Contains("token", ex.Message);
    }

    [Fact]
    public async Task Invalid_Plaintext_Json_Throws()
    {
        var resolver = await CreateResolverAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await repository.InsertAsync("broken", "not-json-object", null, "test");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("$cred:broken.user"));
    }

    [Fact]
    public async Task Params_Json_Is_Resolved_Recursively()
    {
        var resolver = await CreateResolverAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await InsertCredentialAsync(repository, "wms", "{\"key\":\"tk-999\"}");

        const string json = """
            {"url":"http://wms/api","headers":{"Authorization":"Bearer $cred:wms.key"},"attachments":[{"type":"http","url":"http://wms/f?$cred:wms.key"}]}
            """;

        var resolved = await resolver.ResolveParamsAsync(json);

        Assert.Contains("Bearer tk-999", resolved);
        Assert.Contains("http://wms/f?tk-999", resolved);
        Assert.DoesNotContain("$cred:", resolved);
    }

    [Fact]
    public async Task Input_Without_Reference_Is_Unchanged()
    {
        var resolver = await CreateResolverAsync();
        var input = "http://plain/api";
        Assert.Equal(input, await resolver.ResolveAsync(input));
        Assert.Equal("{}", await resolver.ResolveParamsAsync("{}"));
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
