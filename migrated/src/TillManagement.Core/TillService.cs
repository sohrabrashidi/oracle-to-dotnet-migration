namespace TillManagement.Core;

/// <summary>
/// Port of the legacy PL/SQL package <c>pkg_till</c>. Method names and
/// behaviour follow the original one-to-one. The places where the port
/// deliberately differs are marked with "BEHAVIOUR CHANGE" and listed in
/// docs/case-study.md.
/// </summary>
public sealed class TillService(ITillRepository repository)
{
    /// <summary>pkg_till.c_tolerance</summary>
    public const decimal Tolerance = 0.5m;

    /// <summary>pkg_till.open_till</summary>
    public async Task<long> OpenTillAsync(
        string branchCode,
        string cashier,
        IReadOnlyDictionary<string, decimal?> floats,
        CancellationToken ct = default)
    {
        cashier = Normalise(cashier);

        await using var session = await repository.BeginAsync(ct);

        if (await session.CashierHasOpenTillAsync(cashier, ct))
        {
            throw new TillException(TillErrorCode.TillAlreadyOpen, $"Cashier {cashier} already has an open till");
        }

        var tillId = await session.InsertTillAsync(Normalise(branchCode), cashier, ct);

        // Oracle iterated an associative array in key order; keep the same order.
        foreach (var (currency, amount) in floats.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            await session.InsertFloatAsync(tillId, Normalise(currency), amount ?? 0m, ct); // NVL(x, 0)
        }

        await session.CommitAsync(ct);
        return tillId;
    }

    /// <summary>pkg_till.record_movement</summary>
    public async Task RecordMovementAsync(
        long tillId,
        string currency,
        MovementType type,
        decimal amount,
        string? reference = null,
        CancellationToken ct = default)
    {
        currency = Normalise(currency);

        // BEHAVIOUR CHANGE #1: round first, then validate. The PL/SQL compared the
        // unrounded amount with cash on hand but stored the rounded one, so paying
        // out 10.0004 from a till holding exactly 10.000 was refused.
        amount = Math.Round(amount, 3, MidpointRounding.AwayFromZero);

        await using var session = await repository.BeginAsync(ct);
        await AssertOpenAsync(session, tillId, ct);

        if (type == MovementType.Out && await CashOnHandAsync(session, tillId, currency, ct) < amount)
        {
            throw new TillException(TillErrorCode.InsufficientCash, $"Not enough {currency} in till {tillId}");
        }

        // BEHAVIOUR CHANGE #2: Oracle stores '' as NULL. Do the same explicitly
        // so reports that filter on "reference is null" keep working.
        var cleanReference = string.IsNullOrWhiteSpace(reference) ? null : reference;

        await session.InsertMovementAsync(new Movement(tillId, currency, type, amount, cleanReference), ct);
        await session.CommitAsync(ct);
    }

    /// <summary>pkg_till.cash_on_hand</summary>
    public async Task<decimal> CashOnHandAsync(long tillId, string currency, CancellationToken ct = default)
    {
        await using var session = await repository.BeginAsync(ct);
        return await CashOnHandAsync(session, tillId, Normalise(currency), ct);
    }

    /// <summary>pkg_till.close_till</summary>
    public async Task<ClosingResult> CloseTillAsync(
        long tillId,
        IReadOnlyDictionary<string, decimal?> counted,
        CancellationToken ct = default)
    {
        // BEHAVIOUR CHANGE #3: currency keys are normalised to upper case. In
        // PL/SQL p_counted('kwd') did not match a 'KWD' movement and failed with
        // "Missing count", which branch staff hit regularly.
        var counts = counted.ToDictionary(c => Normalise(c.Key), c => c.Value, StringComparer.Ordinal);

        await using var session = await repository.BeginAsync(ct);
        await AssertOpenAsync(session, tillId, ct);

        foreach (var used in await session.GetCurrenciesUsedAsync(tillId, ct))
        {
            if (!counts.ContainsKey(used))
            {
                throw new TillException(TillErrorCode.BadCount, $"Missing count for {used}");
            }
        }

        var status = TillStatus.Balanced;
        var lines = new List<ClosingLine>();

        foreach (var (currency, value) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (value is null or < 0)
            {
                throw new TillException(TillErrorCode.BadCount, $"Invalid count for {currency}");
            }

            var expected = await CashOnHandAsync(session, tillId, currency, ct);
            var variance = Math.Round(value.Value - expected, 3, MidpointRounding.AwayFromZero);
            var line = new ClosingLine(currency, await session.GetFloatAsync(tillId, currency, ct), expected, value.Value, variance);

            await session.InsertClosingLineAsync(tillId, line, ct);
            lines.Add(line);

            // SHORT wins over OVER, same as the original.
            if (variance < -Tolerance)
            {
                status = TillStatus.Short;
            }
            else if (variance > Tolerance && status == TillStatus.Balanced)
            {
                status = TillStatus.Over;
            }
        }

        await session.SetStatusAsync(tillId, status, ct);
        await session.CommitAsync(ct);

        return new ClosingResult(tillId, status, lines);
    }

    private static async Task AssertOpenAsync(ITillSession session, long tillId, CancellationToken ct)
    {
        var status = await session.LockTillAsync(tillId, ct)
            ?? throw new TillException(TillErrorCode.TillNotOpen, $"Till {tillId} does not exist");

        if (status != TillStatus.Open)
        {
            throw new TillException(TillErrorCode.TillNotOpen, $"Till {tillId} is {status.ToString().ToUpperInvariant()}");
        }
    }

    private static async Task<decimal> CashOnHandAsync(ITillSession session, long tillId, string currency, CancellationToken ct) =>
        await session.GetFloatAsync(tillId, currency, ct) + await session.GetNetMovementsAsync(tillId, currency, ct);

    private static string Normalise(string value) => value.Trim().ToUpperInvariant();
}
