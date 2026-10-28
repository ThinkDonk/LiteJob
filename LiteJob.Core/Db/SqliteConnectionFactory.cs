using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace LiteJob.Core.Db;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    static SqliteConnectionFactory()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecutePragmaAsync(conn, "PRAGMA busy_timeout=5000;");
        await ExecutePragmaAsync(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static async Task ExecutePragmaAsync(SqliteConnection conn, string pragma)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        await cmd.ExecuteNonQueryAsync();
    }
}
