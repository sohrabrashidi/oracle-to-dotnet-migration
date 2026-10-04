using Npgsql;
using TillManagement.Core;

namespace TillManagement.Postgres;

public sealed class PostgresTillRepository(NpgsqlDataSource dataSource) : ITillRepository
{
    public async Task<ITillSession> BeginAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        return new Session(connection, transaction);
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction) : ITillSession
    {
        private bool _committed;

        public async Task<bool> CashierHasOpenTillAsync(string cashier, CancellationToken ct)
        {
            await using var cmd = Command("select exists (select 1 from tills where cashier = $1 and status = 'OPEN')", cashier);
            return (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<long> InsertTillAsync(string branchCode, string cashier, CancellationToken ct)
        {
            await using var cmd = Command(
                "insert into tills (branch_code, cashier) values ($1, $2) returning till_id",
                branchCode,
                cashier);

            try
            {
                return (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Lost the race against another session: same error the PL/SQL check would give.
                throw new TillException(TillErrorCode.TillAlreadyOpen, $"Cashier {cashier} already has an open till");
            }
        }

        public async Task InsertFloatAsync(long tillId, string currency, decimal amount, CancellationToken ct)
        {
            await using var cmd = Command(
                "insert into till_floats (till_id, currency, amount) values ($1, $2, $3)",
                tillId,
                currency,
                amount);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<TillStatus?> LockTillAsync(long tillId, CancellationToken ct)
        {
            await using var cmd = Command("select status from tills where till_id = $1 for update", tillId);
            var status = await cmd.ExecuteScalarAsync(ct) as string;
            return status is null ? null : Enum.Parse<TillStatus>(status, ignoreCase: true);
        }

        public async Task<decimal> GetFloatAsync(long tillId, string currency, CancellationToken ct)
        {
            await using var cmd = Command(
                "select coalesce((select amount from till_floats where till_id = $1 and currency = $2), 0)",
                tillId,
                currency);
            return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<decimal> GetNetMovementsAsync(long tillId, string currency, CancellationToken ct)
        {
            // DECODE(movement_type, 'IN', amount, 'OUT', -amount) -> CASE
            await using var cmd = Command(
                """
                select coalesce(sum(case movement_type when 'IN' then amount when 'OUT' then -amount end), 0)
                from till_movements
                where till_id = $1 and currency = $2
                """,
                tillId,
                currency);
            return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<IReadOnlyList<string>> GetCurrenciesUsedAsync(long tillId, CancellationToken ct)
        {
            await using var cmd = Command(
                """
                select currency from till_floats where till_id = $1
                union
                select currency from till_movements where till_id = $1
                order by 1
                """,
                tillId);

            var result = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }

        public async Task InsertMovementAsync(Movement movement, CancellationToken ct)
        {
            await using var cmd = Command(
                """
                insert into till_movements (till_id, currency, movement_type, amount, reference)
                values ($1, $2, $3, $4, $5)
                """,
                movement.TillId,
                movement.Currency,
                movement.Type == MovementType.In ? "IN" : "OUT",
                movement.Amount,
                (object?)movement.Reference ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task InsertClosingLineAsync(long tillId, ClosingLine line, CancellationToken ct)
        {
            await using var cmd = Command(
                """
                insert into till_closings (till_id, currency, opening_float, expected, counted, variance)
                values ($1, $2, $3, $4, $5, $6)
                """,
                tillId,
                line.Currency,
                line.OpeningFloat,
                line.Expected,
                line.Counted,
                line.Variance);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task SetStatusAsync(long tillId, TillStatus status, CancellationToken ct)
        {
            await using var cmd = Command(
                "update tills set status = $2, closed_at = now() where till_id = $1",
                tillId,
                status.ToString().ToUpperInvariant());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync();
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }

        private NpgsqlCommand Command(string sql, params object[] args)
        {
            var cmd = new NpgsqlCommand(sql, connection, transaction);
            foreach (var arg in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
            }

            return cmd;
        }
    }
}
