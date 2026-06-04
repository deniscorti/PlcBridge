namespace Bridge.Core.Model;

/// <summary>
/// Categorizes the nature of a tag's data.
/// </summary>
public enum DataKind
{
    /// <summary>Continuous numeric values (sensors, measurements). Polled/pushed at regular intervals.</summary>
    Telemetry,

    /// <summary>Discrete state changes (button pressed, cycle end). Notified only on change.</summary>
    Event,

    /// <summary>Anomalous conditions with ISA-18.2 lifecycle (active/acknowledged states).</summary>
    Alarm
}
