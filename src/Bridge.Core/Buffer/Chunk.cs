using Bridge.Core.Model;

namespace Bridge.Core.Buffer;

/// <summary>
/// A time-bounded container for TagValues, separated by DataKind.
/// Thread-safe for concurrent add/read.
/// </summary>
public sealed class Chunk
{
    private readonly object _lock = new();
    private readonly List<TagValue> _telemetry = new();
    private readonly List<TagValue> _events = new();
    private readonly List<TagValue> _alarms = new();

    public string Source { get; }
    public string ChunkId { get; }
    public DateTimeOffset FromTs { get; }
    public DateTimeOffset ToTs { get; }
    public ChunkState State { get; private set; } = ChunkState.Open;
    public ChunkQuality Quality { get; set; } = ChunkQuality.Full;

    public uint FirstMsgId { get; private set; }
    public uint LastMsgId { get; private set; }

    public Chunk(string source, DateTimeOffset fromTs, TimeSpan duration)
    {
        Source = source;
        FromTs = fromTs;
        ToTs = fromTs + duration;
        ChunkId = $"c-{fromTs:yyyyMMdd-HHmm}";
    }

    /// <summary>Create chunk with explicit bounds (for loaded/transferred chunks).</summary>
    public Chunk(string source, DateTimeOffset fromTs, DateTimeOffset toTs, ChunkQuality quality)
    {
        Source = source;
        FromTs = fromTs;
        ToTs = toTs;
        ChunkId = $"c-{fromTs:yyyyMMdd-HHmm}";
        Quality = quality;
    }

    public int TelemetryCount { get { lock (_lock) return _telemetry.Count; } }
    public int EventCount { get { lock (_lock) return _events.Count; } }
    public int AlarmCount { get { lock (_lock) return _alarms.Count; } }
    public int RecordCount => TelemetryCount + EventCount + AlarmCount;

    /// <summary>Returns true if the value's timestamp fits within this chunk's time window.</summary>
    public bool Accepts(DateTimeOffset ts) => State == ChunkState.Open && ts >= FromTs && ts < ToTs;

    /// <summary>Add a value. Returns false if chunk is sealed or timestamp out of range.</summary>
    public bool TryAdd(TagValue value)
    {
        lock (_lock)
        {
            if (State != ChunkState.Open) return false;

            var list = value.Kind switch
            {
                DataKind.Telemetry => _telemetry,
                DataKind.Event => _events,
                DataKind.Alarm => _alarms,
                _ => _telemetry
            };

            list.Add(value);

            if (FirstMsgId == 0 || value.MsgId < FirstMsgId) FirstMsgId = value.MsgId;
            if (value.MsgId > LastMsgId) LastMsgId = value.MsgId;

            return true;
        }
    }

    /// <summary>Bulk-add values (for loading from Parquet or chunk transfer).</summary>
    public void AddRange(IEnumerable<TagValue> values)
    {
        lock (_lock)
        {
            foreach (var v in values)
            {
                var list = v.Kind switch
                {
                    DataKind.Telemetry => _telemetry,
                    DataKind.Event => _events,
                    DataKind.Alarm => _alarms,
                    _ => _telemetry
                };
                list.Add(v);

                if (FirstMsgId == 0 || v.MsgId < FirstMsgId) FirstMsgId = v.MsgId;
                if (v.MsgId > LastMsgId) LastMsgId = v.MsgId;
            }
        }
    }

    public void Seal()
    {
        lock (_lock)
        {
            State = ChunkState.Sealed;
        }
    }

    public IReadOnlyList<TagValue> GetTelemetry(string[]? tags, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return FilterByTagAndTime(_telemetry, tags, from, to);
        }
    }

    public IReadOnlyList<TagValue> GetEvents(string[]? tags, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return FilterByTagAndTime(_events, tags, from, to);
        }
    }

    public IReadOnlyList<TagValue> GetAlarms(string[]? tags, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return FilterByTagAndTime(_alarms, tags, from, to);
        }
    }

    /// <summary>Get all values (all kinds) — used for Parquet serialization.</summary>
    public IReadOnlyList<TagValue> GetAllValues()
    {
        lock (_lock)
        {
            var all = new List<TagValue>(_telemetry.Count + _events.Count + _alarms.Count);
            all.AddRange(_telemetry);
            all.AddRange(_events);
            all.AddRange(_alarms);
            all.Sort((a, b) => a.MsgId.CompareTo(b.MsgId));
            return all;
        }
    }

    /// <summary>Extract values with timestamp after cutoff (for boundary handling).</summary>
    public IReadOnlyList<TagValue> ExtractAfter(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var result = new List<TagValue>();
            ExtractAfterFrom(_telemetry, cutoff, result);
            ExtractAfterFrom(_events, cutoff, result);
            ExtractAfterFrom(_alarms, cutoff, result);
            return result;
        }
    }

    private static void ExtractAfterFrom(List<TagValue> list, DateTimeOffset cutoff, List<TagValue> result)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Timestamp > cutoff)
            {
                result.Add(list[i]);
                list.RemoveAt(i);
            }
        }
    }

    private static List<TagValue> FilterByTagAndTime(List<TagValue> list, string[]? tags, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<TagValue>();
        var tagSet = tags is { Length: > 0 } && !tags.Contains("ALL", StringComparer.OrdinalIgnoreCase)
            ? new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var v in list)
        {
            if (v.Timestamp < from || v.Timestamp > to) continue;
            if (tagSet is not null && !tagSet.Contains(v.Tag)) continue;
            result.Add(v);
        }
        return result;
    }
}
