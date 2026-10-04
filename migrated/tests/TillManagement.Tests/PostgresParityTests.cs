using Npgsql;
using TillManagement.Core;
using TillManagement.Postgres;

namespace TillManagement.Tests;

/// <summary>
/// Runs the parity scenarios against a real PostgreSQL database with the
/// migrated schema. Set TILL_PG to a connection string to enable it (CI does).
/// Without it these tests return early.
/// </summary>
public class PostgresParityTests : ParityScenarioTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TILL_PG");

    protected override async Task<ITillRepository?> CreateRepositoryAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using (var cmd = dataSource.CreateCommand(
            "truncate till_closings, till_movements, till_floats, tills restart identity cascade"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        return new PostgresTillRepository(dataSource);
    }
}
