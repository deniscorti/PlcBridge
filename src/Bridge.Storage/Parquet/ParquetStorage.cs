using System.Text.Json;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Parquet;
using Parquet.Schema;
using Parquet.Data;

namespace Bridge.Storage.Parquet;

/// <summary>
/// Writes/reads sealed chunks as Parquet files.
/// Schema: Source, Tag, Kind, ValueJson, TimestampUs (long), MsgId (int).
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

    private static readonly ParquetSchema Schema = new(
        new DataField<string>("source"),
        new DataField<string>("tag"),
        new DataField<string>("kind"),
        new DataField<string>("value_json"),
        new DataField<long>("timestamp_us"),
        new DataField<int>("msg_id")
    );

    /// <summary>Write a sealed chunk to Parquet.</summary>
    public async Task WriteChunkAsync(Chunk chunk, bool archive = false)
    {
        var values = chunk.GetAllValues();
        if (values.Count == 0) return;

        var dir = archive ? _archivePath : _diskPath;
        var fileName = $"{chunk.Source}_{chunk.FromTs:yyyy-MM-dd_HH-mm}_{chunk.FirstMsgId}.parquet";
        var path = Path.Combine(dir, fileName);

        var sources = new string[values.Count];
        var tags = new string[values.Count];
        var kinds = new string[values.Count];
        var valueJsons = new string[values.Count];
        var timestamps = new long[values.Count];
        var msgIds = new int[values.Count];

        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            sources[i] = v.Source;
            tags[i] = v.Tag;
            kinds[i] = v.Kind.ToString();
            valueJsons[i] = JsonSerializer.Serialize(v.Value);
            timestamps[i] = v.Timestamp.ToUnixTimeMilliseconds() * 1000;
            msgIds[i] = (int)v.MsgId;
        }

        using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(Schema, stream);
        using var group = writer.CreateRowGroup();

        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[0], sources));
        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[1], tags));
        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[2], kinds));
        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[3], valueJsons));
        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[4], timestamps));
        await group.WriteColumnAsync(new DataColumn(Schema.DataFields[5], msgIds));
    }

    /// <summary>Read a Parquet file into TagValues.</summary>
    public static async Task<IReadOnlyList<TagValue>> ReadFileAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = await ParquetReader.CreateAsync(stream);

        var result = new List<TagValue>();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var groupReader = reader.OpenRowGroupReader(g);
            var srcCol = (await groupReader.ReadColumnAsync(Schema.DataFields[0])).Data.Cast<string>().ToArray();
            var tagCol = (await groupReader.ReadColumnAsync(Schema.DataFields[1])).Data.Cast<string>().ToArray();
            var kindCol = (await groupReader.ReadColumnAsync(Schema.DataFields[2])).Data.Cast<string>().ToArray();
            var valCol = (await groupReader.ReadColumnAsync(Schema.DataFields[3])).Data.Cast<string>().ToArray();
            var tsCol = (await groupReader.ReadColumnAsync(Schema.DataFields[4])).Data.Cast<long>().ToArray();
            var msgCol = (await groupReader.ReadColumnAsync(Schema.DataFields[5])).Data.Cast<int>().ToArray();

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
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsCol[i] / 1000),
                    MsgId = (uint)msgCol[i]
                });
            }
        }

        return result;
    }

    /// <summary>List Parquet files available, optionally filtered by source.</summary>
    public IReadOnlyList<ParquetFileInfo> ListFiles(string? source = null, bool includeArchives = true)
    {
        var files = new List<ParquetFileInfo>();
        ScanDir(_diskPath, files);
        if (includeArchives) ScanDir(_archivePath, files);

        if (source is not null)
            files.RemoveAll(f => !f.FileName.StartsWith(source, StringComparison.OrdinalIgnoreCase));

        files.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.Ordinal));
        return files;
    }

    /// <summary>Get full path for a file.</summary>
    public string? ResolvePath(string fileName)
    {
        var path = Path.Combine(_diskPath, fileName);
        if (File.Exists(path)) return path;
        path = Path.Combine(_archivePath, fileName);
        return File.Exists(path) ? path : null;
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
