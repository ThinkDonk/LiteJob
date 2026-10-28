using LiteJob.Core.Db;
using LiteJob.Core.Db.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

internal sealed class TempSqlite : IAsyncDisposable
{
    public string Path { get; }
    public SqliteConnectionFactory Factory { get; }

    private TempSqlite(string path, SqliteConnectionFactory factory)
    {
        Path = path;
        Factory = factory;
    }

    public static TempSqlite CreateUnmigrated()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"litejob_test_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory($"Data Source={path};Pooling=False");
        return new TempSqlite(path, factory);
    }

    public static async Task<TempSqlite> CreateMigratedAsync()
    {
        var db = CreateUnmigrated();
        await new MigrationRunner(db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return db;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return ValueTask.CompletedTask;
    }
}
