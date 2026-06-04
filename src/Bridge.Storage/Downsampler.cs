using Bridge.Core.Model;

namespace Bridge.Storage;

/// <summary>
/// Min-max bucketing downsampler for time-series data.
/// Divides the time range into N equal buckets, and for each bucket emits
/// up to 4 representative points: first, min, max, last.
/// This preserves spikes and valleys while reducing data volume.
/// </summary>
public static class Downsampler
{
    /// <summary>
    /// Downsample a sorted list of TagValues for a single tag using min-max bucketing.
    /// </summary>
    /// <param name="sorted">Values sorted by timestamp (ascending), all same tag.</param>
    /// <param name="resolution">Number of time buckets. Output: max 4 × resolution points.</param>
    /// <returns>Downsampled values preserving min/max per bucket.</returns>
    public static List<TagValue> MinMaxBucket(IReadOnlyList<TagValue> sorted, int resolution)
    {
        if (sorted.Count == 0 || resolution <= 0) return [];
        if (sorted.Count <= resolution * 4) return new List<TagValue>(sorted);

        var from = sorted[0].Timestamp.ToUnixTimeMilliseconds();
        var to = sorted[^1].Timestamp.ToUnixTimeMilliseconds();
        var range = to - from;
        if (range <= 0) return new List<TagValue>(sorted);

        var bucketSize = (double)range / resolution;
        var result = new List<TagValue>(resolution * 4);

        int idx = 0;
        for (int b = 0; b < resolution && idx < sorted.Count; b++)
        {
            var bucketStart = from + (long)(b * bucketSize);
            var bucketEnd = from + (long)((b + 1) * bucketSize);
            if (b == resolution - 1) bucketEnd = to + 1; // include last point

            // Collect points in this bucket
            int startIdx = idx;
            while (idx < sorted.Count && sorted[idx].Timestamp.ToUnixTimeMilliseconds() < bucketEnd)
                idx++;
            int endIdx = idx; // exclusive
            int count = endIdx - startIdx;

            if (count == 0) continue;

            if (count <= 4)
            {
                // Few points — emit all
                for (int i = startIdx; i < endIdx; i++)
                    result.Add(sorted[i]);
                continue;
            }

            // Find min and max by numeric value
            var first = sorted[startIdx];
            var last = sorted[endIdx - 1];
            int minIdx = startIdx, maxIdx = startIdx;
            double minVal = AsDouble(first.Value), maxVal = minVal;

            for (int i = startIdx + 1; i < endIdx; i++)
            {
                var v = AsDouble(sorted[i].Value);
                if (v < minVal) { minVal = v; minIdx = i; }
                if (v > maxVal) { maxVal = v; maxIdx = i; }
            }

            // Emit in temporal order: first, then min/max sorted by index, then last
            // Deduplicate if indices overlap
            var indices = new SortedSet<int> { startIdx, minIdx, maxIdx, endIdx - 1 };
            foreach (var i in indices)
                result.Add(sorted[i]);
        }

        return result;
    }

    /// <summary>
    /// Downsample multiple tags at once. Groups by tag, downsamples each, merges back sorted by timestamp.
    /// </summary>
    public static List<TagValue> MinMaxBucketMultiTag(IReadOnlyList<TagValue> values, int resolution)
    {
        if (values.Count == 0 || resolution <= 0) return [];

        var byTag = new Dictionary<string, List<TagValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            if (!byTag.TryGetValue(v.Tag, out var list))
            {
                list = [];
                byTag[v.Tag] = list;
            }
            list.Add(v);
        }

        var result = new List<TagValue>();
        foreach (var (_, tagValues) in byTag)
        {
            tagValues.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            result.AddRange(MinMaxBucket(tagValues, resolution));
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    private static double AsDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        bool b => b ? 1.0 : 0.0,
        _ => double.NaN
    };
}
