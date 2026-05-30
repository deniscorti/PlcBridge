using System.Collections.Concurrent;
using Bridge.Core.Model;

namespace Bridge.Core.Services;

/// <summary>
/// Manages ISA-18.2 alarm states across all DataSources.
/// </summary>
public sealed class AlarmService
{
    // Key: "source:tag"
    private readonly ConcurrentDictionary<string, AlarmInfo> _alarms = new();

    /// <summary>Fired when an alarm state changes.</summary>
    public event Action<AlarmInfo>? OnAlarmChanged;

    public void ProcessValue(TagValue value)
    {
        if (value.Kind != DataKind.Alarm) return;

        var key = $"{value.Source}:{value.Tag}";
        var active = value.Value is true or 1 or 1.0;

        var alarm = _alarms.GetOrAdd(key, _ => new AlarmInfo
        {
            Source = value.Source,
            Tag = value.Tag
        });

        var changed = alarm.Active != active;
        alarm.Active = active;
        alarm.LastChangeTs = value.Timestamp;

        // If alarm returns to inactive and was acknowledged → resolved, remove
        if (!active && alarm.Acknowledged)
        {
            _alarms.TryRemove(key, out _);
        }

        if (changed) OnAlarmChanged?.Invoke(alarm);
    }

    public void Acknowledge(string source, string tag)
    {
        var key = $"{source}:{tag}";
        if (_alarms.TryGetValue(key, out var alarm))
        {
            alarm.Acknowledged = true;
            alarm.LastChangeTs = DateTimeOffset.UtcNow;

            // If returned and now acked → resolved
            if (!alarm.Active)
                _alarms.TryRemove(key, out _);

            OnAlarmChanged?.Invoke(alarm);
        }
    }

    public void AcknowledgeAll(string source)
    {
        foreach (var (key, alarm) in _alarms)
        {
            if (!alarm.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            alarm.Acknowledged = true;
            alarm.LastChangeTs = DateTimeOffset.UtcNow;
            if (!alarm.Active)
                _alarms.TryRemove(key, out _);
            OnAlarmChanged?.Invoke(alarm);
        }
    }

    public IReadOnlyList<AlarmInfo> GetActiveAlarms(string? source = null)
    {
        var alarms = _alarms.Values.AsEnumerable();
        if (source is not null)
            alarms = alarms.Where(a => a.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        return alarms.ToList();
    }
}
