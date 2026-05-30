namespace Bridge.Core.Model;

/// <summary>
/// ISA-18.2 alarm states (4-quadrant model).
/// </summary>
public enum AlarmState
{
    /// <summary>Alarm triggered, not yet acknowledged by operator.</summary>
    ActiveUnacknowledged,

    /// <summary>Alarm active, operator is aware.</summary>
    ActiveAcknowledged,

    /// <summary>Condition cleared but operator hasn't acknowledged yet.</summary>
    ReturnedUnacknowledged,

    /// <summary>Cleared and acknowledged — removed from active list.</summary>
    Resolved
}

/// <summary>
/// Runtime state for a single alarm tag.
/// </summary>
public sealed class AlarmInfo
{
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public bool Active { get; set; }
    public bool Acknowledged { get; set; }
    public string? Severity { get; set; }
    public DateTimeOffset LastChangeTs { get; set; }

    public AlarmState State => (Active, Acknowledged) switch
    {
        (true, false) => AlarmState.ActiveUnacknowledged,
        (true, true) => AlarmState.ActiveAcknowledged,
        (false, false) => AlarmState.ReturnedUnacknowledged,
        (false, true) => AlarmState.Resolved,
    };
}
