using System.Globalization;
using System.Text.Json;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Parquet;
using Parquet.Schema;
using Parquet.Data;

namespace Bridge.Storage.Parquet;

/// <summary>
/// Writes/reads sealed chunks as Parquet files.
/// New format: one file per (source, dataKind, chunk), with tags as columns.
/// Filename: {source}_{kind}_{fromTs}_{toTs}_{firstMsgId}_{lastMsgId}.parquet
/// Schema: timestamp_ms (long, unix milliseconds), msg_id (int), [tag1] (double?), [tag2] (double?), ...
/// </summary>
public sealed class ParquetStorage
{
    private readonly string _diskPath;
    private readonly string _archivePath;

    public ParquetStorage(string diskPath, string archivePath)
    {
        _diskPath = diskPath;
        _archivePath = archivePath;
        Directory.CreateDirectory(_diskPath);
        Directory.CreateDirectory(_archivePath);
    }

    /// <summary>Write a sealed chunk to Parquet — one file per DataKind present in the chunk.
    /// Returns the list of file names written.</summary>
    public async Task<List<string>> WriteChunkAsync(Chunk chunk, bool archive = false)
    {
        var dir = archive ? _archivePath : _diskPath;
        var files = new List<string>();

        foreach (var kind in new[] { DataKind.Telemetry, DataKind.Event, DataKind.Alarm })
        {
            var values = chunk.GetValuesByKind(kind);
            if (values.Count == 0) continue;

            var fileName = await WriteKindFileAsync(dir, chunk, kind, values);
            files.Add(fileName);
        }

        return files;
    }

    private static async Task<string> WriteKindFileAsync(string dir, Chunk chunk, DataKind kind, IReadOnlyList<TagValue> values)
    {
        // Group by timestamp to build rows — multiple tags at the same timestamp
        // become columns of the same row. msg_id tracks the max msgId per row.
        var rows = new SortedDictionary<long, (uint maxMsgId, Dictionary<string, object?> tags)>();
        var tagNames = new LinkedHashSet<string>();

        foreach (var v in values)
        {
            var tsMs = v.Timestamp.ToUnixTimeMilliseconds();

            if (!rows.TryGetValue(tsMs, out var row))
            {
                row = (v.MsgId, new Dictionary<string, object?>());
                rows[tsMs] = row;
            }
            else if (v.MsgId > row.maxMsgId)
            {
                row = (v.MsgId, row.tags);
                rows[tsMs] = row;
            }
            row.tags[v.Tag] = v.Value;
            tagNames.Add(v.Tag);
        }

        var tagList = tagNames.ToList();
        var rowCount = rows.Count;

        // Build schema: timestamp_ms, msg_id, then one nullable double column per tag
        var fields = new List<Field>
        {
            new DataField<long>("timestamp_ms"),
            new DataField<int>("msg_id")
        };

        foreach (var tag in tagList)
            fields.Add(new DataField<double?>(tag));

        var schema = new ParquetSchema(fields);

        // Build column arrays
        var timestamps = new long[rowCount];
        var msgIds = new int[rowCount];
        var tagColumns = new double?[tagList.Count][];
        for (int t = 0; t < tagList.Count; t++)
            tagColumns[t] = new double?[rowCount];

        int rowIdx = 0;
        foreach (var (tsUs, (maxMsgId, tagValues)) in rows)
        {
            timestamps[rowIdx] = tsUs;
            msgIds[rowIdx] = (int)maxMsgId;

            for (int t = 0; t < tagList.Count; t++)
            {
                if (tagValues.TryGetValue(tagList[t], out var val) && val is not null)
                    tagColumns[t][rowIdx] = ConvertToDouble(val);
                else
                    tagColumns[t][rowIdx] = null;
            }
            rowIdx++;
        }

        // Derive msgId range from actual data
        uint firstMsg = values.Min(v => v.MsgId);
        uint lastMsg = values.Max(v => v.MsgId);
        var fromTs = chunk.FromTs;
        var toTs = chunk.ToTs;

        var fileName = $"{chunk.Source}_{kind.ToString().ToLowerInvariant()}" +
                       $"_{fromTs:yyyy-MM-dd_HH-mm-ss}_{toTs:yyyy-MM-dd_HH-mm-ss}" +
                       $"_{firstMsg}_{lastMsg}.parquet";
        var path = Path.Combine(dir, fileName);

        using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var group = writer.CreateRowGroup();

        await group.WriteColumnAsync(new DataColumn(schema.DataFields[0], timestamps));
        await group.WriteColumnAsync(new DataColumn(schema.DataFields[1], msgIds));

        for (int t = 0; t < tagList.Count; t++)
            await group.WriteColumnAsync(new DataColumn(schema.DataFields[t + 2], tagColumns[t]));

        return fileName;
    }

    private static double ConvertToDouble(object val) => val switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        bool b => b ? 1.0 : 0.0,
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.Number => je.GetDouble(),
            JsonValueKind.True => 1.0,
            JsonValueKind.False => 0.0,
            _ => double.NaN
        },
        _ => double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : double.NaN
    };

    /// <summary>Read a Parquet file into TagValues. Derives source, kind, and time range from filename.</summary>
    public static async Task<IReadOnlyList<TagValue>> ReadFileAsync(string filePath)
    {
        var parsed = ParseFileName(Path.GetFileNameWithoutExtension(filePath));
        if (parsed is null)
            return await ReadLegacyFileAsync(filePath);

        var (source, kind) = parsed.Value;

        using var stream = File.OpenRead(filePath);
        using var reader = await ParquetReader.CreateAsync(stream);
        var schema = reader.Schema;

        var result = new List<TagValue>();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var groupReader = reader.OpenRowGroupReader(g);

            var tsCol = (await groupReader.ReadColumnAsync(schema.DataFields[0])).Data.Cast<long>().ToArray();
            var msgCol = (await groupReader.ReadColumnAsync(schema.DataFields[1])).Data.Cast<int>().ToArray();

            // Columns 2..N are tag columns
            for (int c = 2; c < schema.DataFields.Length; c++)
            {
                var tagName = schema.DataFields[c].Name;
                var data = (await groupReader.ReadColumnAsync(schema.DataFields[c])).Data;
                var values = data.Cast<double?>().ToArray();

                for (int i = 0; i < tsCol.Length; i++)
                {
                    if (values[i] is null) continue;

                    result.Add(new TagValue
                    {
                        Source = source,
                        Tag = tagName,
                        Kind = kind,
                        Value = values[i]!.Value,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsCol[i]),
                        MsgId = (uint)msgCol[i]
                    });
                }
            }
        }

        return result;
    }

    /// <summary>Read specific tags from a Parquet file without loading all columns.</summary>
    public static async Task<IReadOnlyList<TagValue>> ReadFileAsync(string filePath, string[]? tags,
        DateTimeOffset? fromTs = null, DateTimeOffset? toTs = null)
    {
        if (tags is null or { Length: 0 })
            return await ReadFileAsync(filePath);

        var parsed = ParseFileName(Path.GetFileNameWithoutExtension(filePath));
        if (parsed is null)
            return await ReadLegacyFileAsync(filePath);

        var (source, kind) = parsed.Value;
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        long? fromMs = fromTs?.ToUnixTimeMilliseconds();
        long? toMs = toTs?.ToUnixTimeMilliseconds();

        using var stream = File.OpenRead(filePath);
        using var reader = await ParquetReader.CreateAsync(stream);
        var schema = reader.Schema;

        var result = new List<TagValue>();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var groupReader = reader.OpenRowGroupReader(g);

            var tsCol = (await groupReader.ReadColumnAsync(schema.DataFields[0])).Data.Cast<long>().ToArray();
            var msgCol = (await groupReader.ReadColumnAsync(schema.DataFields[1])).Data.Cast<int>().ToArray();

            for (int c = 2; c < schema.DataFields.Length; c++)
            {
                var tagName = schema.DataFields[c].Name;
                if (!tagSet.Contains(tagName)) continue;

                var values = (await groupReader.ReadColumnAsync(schema.DataFields[c])).Data.Cast<double?>().ToArray();

                for (int i = 0; i < tsCol.Length; i++)
                {
                    if (values[i] is null) continue;
                    if (fromMs.HasValue && tsCol[i] < fromMs.Value) continue;
                    if (toMs.HasValue && tsCol[i] > toMs.Value) continue;

                    result.Add(new TagValue
                    {
                        Source = source,
                        Tag = tagName,
                        Kind = kind,
                        Value = values[i]!.Value,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsCol[i]),
                        MsgId = (uint)msgCol[i]
                    });
                }
            }
        }

        return result;
    }

    /// <summary>Read legacy format (source,tag,kind,value_json,timestamp_ms,msg_id).</summary>
    private static async Task<IReadOnlyList<TagValue>> ReadLegacyFileAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = await ParquetReader.CreateAsync(stream);
        var schema = reader.Schema;

        var result = new List<TagValue>();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var groupReader = reader.OpenRowGroupReader(g);
            var srcCol = (await groupReader.ReadColumnAsync(schema.DataFields[0])).Data.Cast<string>().ToArray();
            var tagCol = (await groupReader.ReadColumnAsync(schema.DataFields[1])).Data.Cast<string>().ToArray();
            var kindCol = (await groupReader.ReadColumnAsync(schema.DataFields[2])).Data.Cast<string>().ToArray();
            var valCol = (await groupReader.ReadColumnAsync(schema.DataFields[3])).Data.Cast<string>().ToArray();
            var tsCol = (await groupReader.ReadColumnAsync(schema.DataFields[4])).Data.Cast<long>().ToArray();
            var msgCol = (await groupReader.ReadColumnAsync(schema.DataFields[5])).Data.Cast<int>().ToArray();

            for (int i = 0; i < srcCol.Length; i++)
            {
                object? value = null;
                try { value = JsonSerializer.Deserialize<object>(valCol[i]); } catch { value = valCol[i]; }

                result.Add(new TagValue
                {
                    Source = srcCol[i],
                    Tag = tagCol[i],
                    Kind = Enum.Parse<DataKind>(kindCol[i], true),
                    Value = value,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsCol[i]),
                    MsgId = (uint)msgCol[i]
                });
            }
        }

        return result;
    }

    /// <summary>List Parquet files available, optionally filtered by source and/or kind.</summary>
    public IReadOnlyList<ParquetFileInfo> ListFiles(string? source = null, DataKind? kind = null, bool includeArchives = true)
    {
        var files = new List<ParquetFileInfo>();
        ScanDir(_diskPath, files);
        if (includeArchives) ScanDir(_archivePath, files);

        if (source is not null)
            files.RemoveAll(f => !f.FileName.StartsWith(source + "_", StringComparison.OrdinalIgnoreCase));

        if (kind is not null)
        {
            var kindStr = "_" + kind.Value.ToString().ToLowerInvariant() + "_";
            files.RemoveAll(f => !f.FileName.Contains(kindStr, StringComparison.OrdinalIgnoreCase));
        }

        files.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.Ordinal));
        return files;
    }

    /// <summary>List files whose time range overlaps [from, to].</summary>
    public IReadOnlyList<ParquetFileInfo> ListFilesInRange(string source, DataKind kind,
        DateTimeOffset from, DateTimeOffset to, bool includeArchives = true)
    {
        var allFiles = ListFiles(source, kind, includeArchives);
        var result = new List<ParquetFileInfo>();

        foreach (var f in allFiles)
        {
            var range = ParseTimeRange(f.FileName);
            if (range is null) { result.Add(f); continue; } // can't parse → include to be safe
            if (range.Value.from < to && range.Value.to > from)
                result.Add(f);
        }

        return result;
    }

    /// <summary>Get full path for a file.</summary>
    public string? ResolvePath(string fileName)
    {
        var path = Path.Combine(_diskPath, fileName);
        if (File.Exists(path)) return path;
        path = Path.Combine(_archivePath, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Parse source and kind from filename: {source}_{kind}_{from}_{to}_{firstMsg}_{lastMsg}.parquet</summary>
    public static (string source, DataKind kind)? ParseFileName(string nameWithoutExt)
    {
        // Format: source_kind_yyyy-MM-dd_HH-mm-ss_yyyy-MM-dd_HH-mm-ss_firstMsg_lastMsg
        // The kind is the second segment, immediately after source.
        // Source name can't contain underscores in practice (validated at registration).
        foreach (var k in new[] { DataKind.Telemetry, DataKind.Event, DataKind.Alarm })
        {
            var kindStr = "_" + k.ToString().ToLowerInvariant() + "_";
            var idx = nameWithoutExt.IndexOf(kindStr, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var source = nameWithoutExt[..idx];
                return (source, k);
            }
        }
        return null;
    }

    public static (DateTimeOffset from, DateTimeOffset to)? ParseTimeRange(string fileName)
    {
        // {source}_{kind}_{yyyy-MM-dd_HH-mm-ss}_{yyyy-MM-dd_HH-mm-ss}_{firstMsg}_{lastMsg}.parquet
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        foreach (var k in new[] { "telemetry", "event", "alarm" })
        {
            var kindStr = "_" + k + "_";
            var idx = nameWithoutExt.IndexOf(kindStr, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var rest = nameWithoutExt[(idx + kindStr.Length)..];
            // rest = "yyyy-MM-dd_HH-mm-ss_yyyy-MM-dd_HH-mm-ss_firstMsg_lastMsg"
            // Date format: yyyy-MM-dd_HH-mm-ss (19 chars)
            if (rest.Length < 39) return null; // 19 + 1 + 19

            var fromStr = rest[..19].Replace('_', ' ').Replace('-', '-');
            var toStr = rest[20..39].Replace('_', ' ').Replace('-', '-');

            if (DateTimeOffset.TryParseExact(fromStr, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var fromTs) &&
                DateTimeOffset.TryParseExact(toStr, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var toTs))
            {
                return (fromTs, toTs);
            }
        }
        return null;
    }

    public static (uint firstMsgId, uint lastMsgId)? ParseMsgIdRange(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        // Last two segments after the date ranges are msgId values
        var parts = nameWithoutExt.Split('_');
        if (parts.Length < 2) return null;

        if (uint.TryParse(parts[^1], out var last) && uint.TryParse(parts[^2], out var first))
            return (first, last);

        return null;
    }

    private static void ScanDir(string dir, List<ParquetFileInfo> list)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.parquet"))
        {
            var info = new FileInfo(file);
            list.Add(new ParquetFileInfo(info.Name, file, info.Length));
        }
    }
}

public sealed record ParquetFileInfo(string FileName, string FullPath, long SizeBytes);

/// <summary>Preserves insertion order while deduplicating — used for tag column ordering.</summary>
internal sealed class LinkedHashSet<T> where T : notnull
{
    private readonly HashSet<T> _set;
    private readonly List<T> _list = new();

    public LinkedHashSet() => _set = new();
    public LinkedHashSet(IEqualityComparer<T> comparer) => _set = new(comparer);

    public bool Add(T item)
    {
        if (!_set.Add(item)) return false;
        _list.Add(item);
        return true;
    }

    public List<T> ToList() => new(_list);
    public int Count => _list.Count;
}
