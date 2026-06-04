namespace Bridge.Core.Buffer;

public enum ChunkState
{
    /// <summary>Active chunk, accepting writes.</summary>
    Open,

    /// <summary>Completed, immutable. Ready for flush/transfer.</summary>
    Sealed
}

public enum ChunkQuality
{
    /// <summary>Data from live stream (may be downsampled for telemetry).</summary>
    Live,

    /// <summary>Full-fidelity data from chunk transfer.</summary>
    Full,

    /// <summary>Loaded from Parquet archive.</summary>
    Loaded
}
