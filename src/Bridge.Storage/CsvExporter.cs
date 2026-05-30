using System.Globalization;
using System.Text;
using System.Text.Json;
using Bridge.Core.Model;

namespace Bridge.Storage;

/// <summary>
/// Exports TagValues to CSV format.
/// </summary>
public static class CsvExporter
{
    public static async Task WriteAsync(Stream stream, IReadOnlyList<TagValue> values)
    {
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("Source,Tag,Kind,Value,Timestamp,MsgId");

        foreach (var v in values)
        {
            var valueStr = v.Value switch
            {
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                bool b => b.ToString(),
                _ => JsonSerializer.Serialize(v.Value)
            };
            // Escape commas in value
            if (valueStr.Contains(','))
                valueStr = $"\"{valueStr}\"";

            await writer.WriteLineAsync(
                $"{v.Source},{v.Tag},{v.Kind},{valueStr},{v.Timestamp:o},{v.MsgId}");
        }
    }
}
