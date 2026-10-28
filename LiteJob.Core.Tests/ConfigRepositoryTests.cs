using System.Text.RegularExpressions;
using Dapper;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class ConfigRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<ConfigRepository> CreateRepositoryAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new ConfigRepository(_db.Factory, _crypto);
    }

    [Fact]
    public async Task Plain_Keys_Roundtrip()
    {
        var repository = await CreateRepositoryAsync();

        await repository.SetAsync("smtp.host", "127.0.0.1");
        await repository.SetAsync("smtp.port", "2525");
        await repository.SetAsync("smtp.ssl", "0");
        await repository.SetAsync("maintenance.run_retention_days", "30");

        Assert.Equal("127.0.0.1", await repository.GetAsync("smtp.host"));
        Assert.Equal("2525", await repository.GetAsync("smtp.port"));
        Assert.Equal("0", await repository.GetAsync("smtp.ssl"));
        Assert.Equal("30", await repository.GetAsync("maintenance.run_retention_days"));
        Assert.Null(await repository.GetAsync("smtp.not-set"));

        var all = await repository.GetAllAsync();
        Assert.Equal("127.0.0.1", all["smtp.host"]);
        Assert.Equal("30", all["maintenance.run_retention_days"]);
    }

    [Fact]
    public async Task Set_Same_Key_Twice_Overwrites()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SetAsync("smtp.host", "old");
        await repository.SetAsync("smtp.host", "new");

        Assert.Equal("new", await repository.GetAsync("smtp.host"));
        var all = await repository.GetAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task SmtpPass_Is_Encrypted_At_Rest_And_Decrypted_On_Read()
    {
        var repository = await CreateRepositoryAsync();
        const string plain = "smtp-pass-秘密-123";

        await repository.SetAsync(ConfigRepository.SmtpPassKey, plain);

        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            var stored = await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM t_config WHERE key='smtp.pass';");
            Assert.NotNull(stored);
            Assert.NotEqual(plain, stored);
            Assert.Matches(new Regex(@"^[A-Za-z0-9+/]+=*$"), stored);
        }

        Assert.Equal(plain, await repository.GetAsync(ConfigRepository.SmtpPassKey));
    }

    [Fact]
    public async Task GetAll_Decrypts_SmtpPass()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SetAsync("smtp.user", "u1");
        await repository.SetAsync(ConfigRepository.SmtpPassKey, "pass-秘密");

        var all = await repository.GetAllAsync();

        Assert.Equal("pass-秘密", all[ConfigRepository.SmtpPassKey]);
        Assert.Equal("u1", all["smtp.user"]);
    }

    [Fact]
    public async Task Delete_Removes_Key()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SetAsync("smtp.host", "h");
        await repository.DeleteAsync("smtp.host");

        Assert.Null(await repository.GetAsync("smtp.host"));
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
