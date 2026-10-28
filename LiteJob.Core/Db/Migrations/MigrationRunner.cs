using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Db.Migrations;

public sealed class MigrationRunner
{
    private static readonly Regex ScriptNameRegex = new(@"^V(?<version>\d+)__(?<name>.+)\.sql$", RegexOptions.Compiled);

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(SqliteConnectionFactory factory, ILogger<MigrationRunner> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(ct);

        var journalMode = await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
        _logger.LogInformation("SQLite journal_mode={JournalMode}", journalMode);

        var scripts = LoadScripts();
        var currentVersion = await GetUserVersionAsync(conn);
        _logger.LogInformation("迁移开始：当前 user_version={Version}，共 {Total} 个内嵌脚本", currentVersion, scripts.Count);

        foreach (var script in scripts.Where(s => s.Version > currentVersion))
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                await conn.ExecuteAsync(script.Sql, transaction: tx);
                await conn.ExecuteAsync($"PRAGMA user_version = {script.Version};", transaction: tx);
                await tx.CommitAsync(ct);
                _logger.LogInformation("迁移已执行 {ScriptName} → user_version={Version}", script.Name, script.Version);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        var finalVersion = await GetUserVersionAsync(conn);
        _logger.LogInformation("迁移完成：user_version={Version}，本次执行 {Executed} 个脚本", finalVersion, scripts.Count(s => s.Version > currentVersion));
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection conn)
        => (int)await conn.ExecuteScalarAsync<long>("PRAGMA user_version;");

    private List<(int Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var result = new List<(int, string, string)>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var match = ScriptNameRegex.Match(resourceName);
            if (!match.Success) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"内嵌迁移脚本读取失败：{resourceName}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEndAsync().GetAwaiter().GetResult();
            result.Add((int.Parse(match.Groups["version"].Value), resourceName, sql));
        }
        return result.OrderBy(s => s.Item1).ToList();
    }
}
