namespace TillManagement.Core;

/// <summary>
/// The legacy package raised ORA-20001..20004. The APEX screens matched on those
/// numbers, so the new code keeps the same codes. That let the old and new UI show
/// identical messages while both ran side by side.
/// </summary>
public enum TillErrorCode
{
    TillAlreadyOpen = 20001,
    TillNotOpen = 20002,
    InsufficientCash = 20003,
    BadCount = 20004,
}

public sealed class TillException(TillErrorCode code, string message) : Exception(message)
{
    public TillErrorCode Code { get; } = code;
}
