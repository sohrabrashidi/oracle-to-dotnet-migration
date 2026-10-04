namespace TillManagement.Core;

public enum TillStatus
{
    Open,
    Balanced,
    Short,
    Over,
}

public enum MovementType
{
    In,
    Out,
}

public sealed record Till(long Id, string BranchCode, string Cashier, DateTimeOffset OpenedAt, TillStatus Status);

public sealed record Movement(long TillId, string Currency, MovementType Type, decimal Amount, string? Reference);

public sealed record ClosingLine(string Currency, decimal OpeningFloat, decimal Expected, decimal Counted, decimal Variance);

public sealed record ClosingResult(long TillId, TillStatus Status, IReadOnlyList<ClosingLine> Lines);
