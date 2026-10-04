using TillManagement.Core;

namespace TillManagement.Tests;

/// <summary>
/// Tests for the places where the port intentionally does NOT match the
/// legacy package. Each one was agreed with the business before go-live.
/// </summary>
public class BehaviourChangeTests
{
    private readonly InMemoryTillRepository _db = new();
    private readonly TillService _service;

    public BehaviourChangeTests() => _service = new TillService(_db);

    [Fact]
    public async Task Change1_payout_is_validated_after_rounding()
    {
        var till = await _service.OpenTillAsync("HQ", "sara", new Dictionary<string, decimal?> { ["KWD"] = 10.000m });

        // Legacy: 10 < 10.0004 -> ORA-20003. Port: rounds to 10.000 first and allows it.
        await _service.RecordMovementAsync(till, "KWD", MovementType.Out, 10.0004m);

        Assert.Equal(0m, await _service.CashOnHandAsync(till, "KWD"));
    }

    [Fact]
    public async Task Change2_empty_reference_is_stored_as_null()
    {
        var till = await _service.OpenTillAsync("HQ", "sara", new Dictionary<string, decimal?> { ["KWD"] = 100m });

        await _service.RecordMovementAsync(till, "KWD", MovementType.In, 5m, reference: "");

        Assert.Null(Assert.Single(_db.AllMovements).Reference);
    }

    [Fact]
    public async Task Change3_counted_currency_keys_are_case_insensitive()
    {
        var till = await _service.OpenTillAsync("HQ", "sara", new Dictionary<string, decimal?> { ["KWD"] = 100m });

        var result = await _service.CloseTillAsync(till, new Dictionary<string, decimal?> { ["kwd"] = 100m });

        Assert.Equal(TillStatus.Balanced, result.Status);
    }

    [Fact]
    public async Task Failed_call_leaves_no_partial_data()
    {
        var till = await _service.OpenTillAsync("HQ", "sara", new Dictionary<string, decimal?> { ["KWD"] = 1m });

        await Assert.ThrowsAsync<TillException>(() => _service.RecordMovementAsync(till, "KWD", MovementType.Out, 50m));

        Assert.Empty(_db.AllMovements);
    }
}
