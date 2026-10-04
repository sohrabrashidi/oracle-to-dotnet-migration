namespace TillManagement.Core;

/// <summary>
/// In-memory repository used by the parity tests. It mimics the transactional
/// behaviour that matters here: nothing is visible until commit, and a session
/// that is disposed without commit leaves no trace.
/// </summary>
public sealed class InMemoryTillRepository : ITillRepository
{
    private readonly object _gate = new();
    private long _nextTillId = 1;

    internal Dictionary<long, (Till Till, TillStatus Status)> Tills { get; } = [];

    internal Dictionary<(long, string), decimal> Floats { get; } = [];

    internal List<Movement> Movements { get; } = [];

    internal List<(long TillId, ClosingLine Line)> Closings { get; } = [];

    public IReadOnlyList<Movement> AllMovements
    {
        get
        {
            lock (_gate)
            {
                return Movements.ToList();
            }
        }
    }

    public Task<ITillSession> BeginAsync(CancellationToken ct) => Task.FromResult<ITillSession>(new Session(this));

    private sealed class Session(InMemoryTillRepository db) : ITillSession
    {
        private readonly List<Action> _pending = [];
        private readonly Dictionary<long, TillStatus> _pendingStatus = [];
        private readonly List<Movement> _pendingMovements = [];
        private readonly Dictionary<(long, string), decimal> _pendingFloats = [];

        public Task<bool> CashierHasOpenTillAsync(string cashier, CancellationToken ct)
        {
            lock (db._gate)
            {
                return Task.FromResult(db.Tills.Values.Any(t => t.Till.Cashier == cashier && t.Status == TillStatus.Open));
            }
        }

        public Task<long> InsertTillAsync(string branchCode, string cashier, CancellationToken ct)
        {
            long id;
            lock (db._gate)
            {
                id = db._nextTillId++;
            }

            var till = new Till(id, branchCode, cashier, DateTimeOffset.UtcNow, TillStatus.Open);
            _pending.Add(() => db.Tills[id] = (till, TillStatus.Open));
            _pendingStatus[id] = TillStatus.Open;
            return Task.FromResult(id);
        }

        public Task InsertFloatAsync(long tillId, string currency, decimal amount, CancellationToken ct)
        {
            _pendingFloats[(tillId, currency)] = amount;
            _pending.Add(() => db.Floats[(tillId, currency)] = amount);
            return Task.CompletedTask;
        }

        public Task<TillStatus?> LockTillAsync(long tillId, CancellationToken ct)
        {
            lock (db._gate)
            {
                if (_pendingStatus.TryGetValue(tillId, out var pending))
                {
                    return Task.FromResult<TillStatus?>(pending);
                }

                return Task.FromResult<TillStatus?>(db.Tills.TryGetValue(tillId, out var t) ? t.Status : null);
            }
        }

        public Task<decimal> GetFloatAsync(long tillId, string currency, CancellationToken ct)
        {
            if (_pendingFloats.TryGetValue((tillId, currency), out var pending))
            {
                return Task.FromResult(pending);
            }

            lock (db._gate)
            {
                return Task.FromResult(db.Floats.GetValueOrDefault((tillId, currency)));
            }
        }

        public Task<decimal> GetNetMovementsAsync(long tillId, string currency, CancellationToken ct)
        {
            lock (db._gate)
            {
                var all = db.Movements.Concat(_pendingMovements).Where(m => m.TillId == tillId && m.Currency == currency);
                return Task.FromResult(all.Sum(m => m.Type == MovementType.In ? m.Amount : -m.Amount));
            }
        }

        public Task<IReadOnlyList<string>> GetCurrenciesUsedAsync(long tillId, CancellationToken ct)
        {
            lock (db._gate)
            {
                var currencies = db.Floats.Keys.Where(k => k.Item1 == tillId).Select(k => k.Item2)
                    .Concat(db.Movements.Where(m => m.TillId == tillId).Select(m => m.Currency))
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToList();
                return Task.FromResult<IReadOnlyList<string>>(currencies);
            }
        }

        public Task InsertMovementAsync(Movement movement, CancellationToken ct)
        {
            _pendingMovements.Add(movement);
            _pending.Add(() => db.Movements.Add(movement));
            return Task.CompletedTask;
        }

        public Task InsertClosingLineAsync(long tillId, ClosingLine line, CancellationToken ct)
        {
            _pending.Add(() => db.Closings.Add((tillId, line)));
            return Task.CompletedTask;
        }

        public Task SetStatusAsync(long tillId, TillStatus status, CancellationToken ct)
        {
            _pendingStatus[tillId] = status;
            _pending.Add(() => db.Tills[tillId] = (db.Tills[tillId].Till, status));
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            lock (db._gate)
            {
                foreach (var apply in _pending)
                {
                    apply();
                }
            }

            _pending.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
