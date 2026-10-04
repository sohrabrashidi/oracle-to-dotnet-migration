namespace TillManagement.Core;

/// <summary>
/// Storage operations the service needs. Each method on <see cref="ITillSession"/>
/// runs inside the same database transaction. That is the C# equivalent of a
/// PL/SQL call that commits (or rolls back) as a whole.
/// </summary>
public interface ITillRepository
{
    Task<ITillSession> BeginAsync(CancellationToken ct);
}

public interface ITillSession : IAsyncDisposable
{
    Task<bool> CashierHasOpenTillAsync(string cashier, CancellationToken ct);

    Task<long> InsertTillAsync(string branchCode, string cashier, CancellationToken ct);

    Task InsertFloatAsync(long tillId, string currency, decimal amount, CancellationToken ct);

    /// <summary>Locks the till row (SELECT ... FOR UPDATE) and returns its status, or null if missing.</summary>
    Task<TillStatus?> LockTillAsync(long tillId, CancellationToken ct);

    Task<decimal> GetFloatAsync(long tillId, string currency, CancellationToken ct);

    Task<decimal> GetNetMovementsAsync(long tillId, string currency, CancellationToken ct);

    Task<IReadOnlyList<string>> GetCurrenciesUsedAsync(long tillId, CancellationToken ct);

    Task InsertMovementAsync(Movement movement, CancellationToken ct);

    Task InsertClosingLineAsync(long tillId, ClosingLine line, CancellationToken ct);

    Task SetStatusAsync(long tillId, TillStatus status, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}
