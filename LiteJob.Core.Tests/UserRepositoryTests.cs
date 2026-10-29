using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class UserRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly UserRepository _repository;

    public UserRepositoryTests()
    {
        _repository = new UserRepository(_db.Factory);
    }

    private Task Migrate() => new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory,
        NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();

    [Fact]
    public async Task Insert_And_GetByUsername_Roundtrip()
    {
        await Migrate();
        var id = await _repository.InsertAsync("wms-user", "hash-abc", "viewer");

        var loaded = await _repository.GetByUsernameAsync("wms-user");
        Assert.NotNull(loaded);
        Assert.Equal("wms-user", loaded!.Username);
        Assert.Equal("hash-abc", loaded.PasswordHash);
        Assert.Equal("viewer", loaded.Role);
        Assert.Equal(1, loaded.Enabled);
        Assert.Equal(id, loaded.Id);
    }

    [Fact]
    public async Task Duplicate_Username_Detected()
    {
        await Migrate();
        await _repository.InsertAsync("dup", "h1", "viewer");
        Assert.NotNull(await _repository.GetByUsernameAsync("dup"));
    }

    [Fact]
    public async Task Update_Password_Role_Enabled_Independently()
    {
        await Migrate();
        var id = await _repository.InsertAsync("u1", "old-hash", "viewer");

        await _repository.UpdateAsync(id, "new-hash", null, null);
        var afterPassword = await _repository.GetByIdAsync(id);
        Assert.Equal("new-hash", afterPassword!.PasswordHash);
        Assert.Equal("viewer", afterPassword.Role);

        await _repository.UpdateAsync(id, null, "admin", null);
        var afterRole = await _repository.GetByIdAsync(id);
        Assert.Equal("admin", afterRole!.Role);
        Assert.Equal("new-hash", afterRole.PasswordHash);

        await _repository.UpdateAsync(id, null, null, 0);
        var afterEnabled = await _repository.GetByIdAsync(id);
        Assert.Equal(0, afterEnabled!.Enabled);
    }

    [Fact]
    public async Task CountEnabledAdmins_Tracks_Admin_Rows()
    {
        await Migrate();
        Assert.Equal(0, await _repository.CountEnabledAdminsAsync());

        var admin1 = await _repository.InsertAsync("admin1", "h", "admin");
        Assert.Equal(1, await _repository.CountEnabledAdminsAsync());

        var admin2 = await _repository.InsertAsync("admin2", "h", "admin");
        Assert.Equal(2, await _repository.CountEnabledAdminsAsync());

        await _repository.UpdateAsync(admin2, null, null, 0);
        Assert.Equal(1, await _repository.CountEnabledAdminsAsync());
    }

    [Fact]
    public async Task List_Does_Not_Expose_Password_Hash()
    {
        await Migrate();
        await _repository.InsertAsync("secret-user", "super-secret-hash", "admin");

        var list = await _repository.ListAsync();
        var item = Assert.Single(list);
        Assert.Equal("", item.PasswordHash);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
