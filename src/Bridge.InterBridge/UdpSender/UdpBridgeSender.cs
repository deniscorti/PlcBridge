using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Bridge.Core.Model;
using Bridge.InterBridge.Protocol;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.UdpSender;

/// <summary>
/// Sends live stream data via UDP from DataService to DataServer(s).
/// Implements the inter-bridge binary protocol with fragmentation support.
/// </summary>
public sealed class UdpBridgeSender : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _endpoint;
    private readonly uint _sourceId;
    private readonly string _sourceIdStr;
    private readonly ILogger _logger;
    private readonly int _maxPacketBytes;
    private ushort _groupSeq;
    private CancellationTokenSource? _mappingCts;
    private Task? _mappingTask;
    private Func<IReadOnlyDictionary<string, Tag>>? _tagsProvider;

    public string DestinationId { get; }
    public bool Enabled { get; set; }

    /// <summary>Interval between periodic mapping packets.</summary>
    public int MappingIntervalMs { get; set; } = 10_000;

    public UdpBridgeSender(string destinationId, string host, int port, string sourceId,
        int maxPacketBytes, bool enabled, ILogger logger)
    {
        DestinationId = destinationId;
        _endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _sourceIdStr = sourceId;
        _sourceId = UdpProtocol.Crc32(sourceId);
        _maxPacketBytes = maxPacketBytes;
        Enabled = enabled;
        _logger = logger;
    }

    /// <summary>
    /// Start periodic mapping broadcast. The tagsProvider returns the current tags for this source.
    /// Call this after the source tags are known (or will be discovered dynamically).
    /// Sends an initial mapping immediately, then repeats every MappingIntervalMs.
    /// </summary>
    public void StartMappingBroadcast(Func<IReadOnlyDictionary<string, Tag>> tagsProvider)
    {
        _tagsProvider = tagsProvider;
        _mappingCts = new CancellationTokenSource();
        _mappingTask = MappingLoopAsync(_mappingCts.Token);
    }

    /// <summary>
    /// Send mapping packets: source name + tag CRC→name pairs.
    /// Each packet carries: [packetIdx:1][packetTotal:1][sourceNameLen:2][sourceName:N][tagCount:2][crc:4][nameLen:2][name:N]...
    /// tagCount is the number of tags in THIS packet.
    /// The receiver collects all packetTotal packets (matched by header GroupSeq), orders by packetIdx, then processes.
    /// </summary>
    public async Task SendMappingAsync()
    {
        if (!Enabled || _tagsProvider is null) return;

        var tags = _tagsProvider();
        var sourceNameBytes = System.Text.Encoding.UTF8.GetBytes(_sourceIdStr);

        // Pre-encode all tag entries
        var tagEntries = new List<(uint crc, byte[] nameBytes)>();
        foreach (var (name, _) in tags)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            tagEntries.Add((Crc32.Compute(name), nameBytes));
        }

        // Per-packet overhead: packetIdx(1) + packetTotal(1) + sourceNameLen(2) + sourceName(N) + tagCount(2)
        var headerOverhead = 1 + 1 + 2 + sourceNameBytes.Length + 2;

        // Split tag entries into packets that fit within MaxPayloadSize
        var packetTagGroups = new List<List<(uint crc, byte[] nameBytes)>>();
        var currentGroup = new List<(uint crc, byte[] nameBytes)>();
        var currentSize = headerOverhead;

        foreach (var entry in tagEntries)
        {
            var entrySize = 4 + 2 + entry.nameBytes.Length; // crc + nameLen + name
            if (currentSize + entrySize > UdpProtocol.MaxPayloadSize && currentGroup.Count > 0)
            {
                packetTagGroups.Add(currentGroup);
                currentGroup = new List<(uint crc, byte[] nameBytes)>();
                currentSize = headerOverhead;
            }
            currentGroup.Add(entry);
            currentSize += entrySize;
        }
        // Always add at least one packet (even if 0 tags)
        packetTagGroups.Add(currentGroup);

        var packetTotal = (byte)packetTagGroups.Count;
        var tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = _groupSeq++;

        for (byte packetIdx = 0; packetIdx < packetTotal; packetIdx++)
        {
            var group = packetTagGroups[packetIdx];
            var payloadSize = headerOverhead + group.Sum(e => 4 + 2 + e.nameBytes.Length);
            var buf = new byte[UdpProtocol.HeaderSize + payloadSize];

            UdpProtocol.WriteHeader(buf, UdpProtocol.FlagMapping, _sourceId, tsMs, seq, 0, 1, 0);

            var offset = UdpProtocol.HeaderSize;

            // packetIdx + packetTotal
            buf[offset++] = packetIdx;
            buf[offset++] = packetTotal;

            // sourceName
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), (ushort)sourceNameBytes.Length);
            offset += 2;
            sourceNameBytes.CopyTo(buf, offset);
            offset += sourceNameBytes.Length;

            // tagCount (in this packet)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), (ushort)group.Count);
            offset += 2;

            // tag entries
            foreach (var (crc, nameBytes) in group)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), crc);
                offset += 4;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), (ushort)nameBytes.Length);
                offset += 2;
                nameBytes.CopyTo(buf, offset);
                offset += nameBytes.Length;
            }

            try
            {
                await _udp.SendAsync(buf, buf.Length, _endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send UDP mapping packet {Idx}/{Total} to {Dest}",
                    packetIdx + 1, packetTotal, DestinationId);
            }
        }

        _logger.LogDebug("Sent mapping for source '{Source}' ({TagCount} tags in {Packets} packets) to {Dest}",
            _sourceIdStr, tagEntries.Count, packetTotal, DestinationId);
    }

    private async Task MappingLoopAsync(CancellationToken ct)
    {
        // Send initial mapping immediately
        await SendMappingAsync();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MappingIntervalMs, ct);
                await SendMappingAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in mapping broadcast loop for {Dest}", DestinationId);
            }
        }
    }

    /// <summary>Send a batch of tag values as UDP packets.</summary>
    public async Task SendBatchAsync(IReadOnlyList<TagValue> values, DateTimeOffset timestamp)
    {
        if (!Enabled || values.Count == 0) return;

        var tsMs = timestamp.ToUnixTimeMilliseconds();
        var firstMsgId = values[0].MsgId;

        // Group by kind
        var groups = values.GroupBy(v => v.Kind);
        foreach (var group in groups)
        {
            var kindFlag = group.Key switch
            {
                DataKind.Event => UdpProtocol.FlagEvent,
                DataKind.Alarm => UdpProtocol.FlagAlarm,
                _ => UdpProtocol.FlagTelemetry
            };

            var items = group.ToList();
            await SendGroupAsync(items, kindFlag, tsMs, firstMsgId);
        }
    }

    /// <summary>Send a single tag value.</summary>
    public async Task SendAsync(TagValue value)
    {
        if (!Enabled) return;
        await SendBatchAsync([value], value.Timestamp);
    }

    private async Task SendGroupAsync(List<TagValue> values, byte kindFlag, long tsMs, uint msgIdBase)
    {
        // Estimate sizes and fragment if needed
        var records = new List<(uint tagId, byte valueType, byte[] valueBytes)>();
        foreach (var v in values)
        {
            var tagId = Core.Model.Crc32.Compute(v.Tag);
            var (vType, vBytes) = EncodeValue(v.Value);
            records.Add((tagId, vType, vBytes));
        }

        // Split into packets respecting max payload
        var packets = new List<List<(uint tagId, byte valueType, byte[] valueBytes)>>();
        var current = new List<(uint, byte, byte[])>();
        var currentSize = 0;

        foreach (var rec in records)
        {
            var recSize = 4 + 1 + rec.valueBytes.Length; // tagId + type + value
            if (currentSize + recSize > UdpProtocol.MaxPayloadSize && current.Count > 0)
            {
                packets.Add(current);
                current = new List<(uint, byte, byte[])>();
                currentSize = 0;
            }
            current.Add(rec);
            currentSize += recSize;
        }
        if (current.Count > 0) packets.Add(current);

        var fragTotal = (byte)(packets.Count > 1 ? packets.Count : 1);
        var seq = _groupSeq++;

        for (int i = 0; i < packets.Count; i++)
        {
            var flags = kindFlag;
            if (packets.Count > 1) flags |= UdpProtocol.FlagFragmented;

            var payloadSize = packets[i].Sum(r => 4 + 1 + r.valueBytes.Length);
            var buf = new byte[UdpProtocol.HeaderSize + payloadSize];

            UdpProtocol.WriteHeader(buf, flags, _sourceId, tsMs, seq, (byte)i, fragTotal, msgIdBase);

            var offset = UdpProtocol.HeaderSize;
            foreach (var (tagId, valueType, valueBytes) in packets[i])
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), tagId);
                offset += 4;
                buf[offset++] = valueType;
                valueBytes.CopyTo(buf, offset);
                offset += valueBytes.Length;
            }

            try
            {
                await _udp.SendAsync(buf, buf.Length, _endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send UDP packet to {Dest}", DestinationId);
            }
        }
    }

    private static (byte type, byte[] bytes) EncodeValue(object? value)
    {
        return value switch
        {
            double d => (UdpProtocol.TypeFloat64, BitConverter.GetBytes(d)),
            float f => (UdpProtocol.TypeFloat32, BitConverter.GetBytes(f)),
            int i => (UdpProtocol.TypeInt32, BitConverter.GetBytes(i)),
            bool b => (UdpProtocol.TypeBool, [(byte)(b ? 1 : 0)]),
            double[] arr => EncodeFloat64Array(arr),
            _ when value is IConvertible c => (UdpProtocol.TypeFloat64, BitConverter.GetBytes(c.ToDouble(null))),
            _ => (UdpProtocol.TypeFloat64, BitConverter.GetBytes(0.0))
        };
    }

    private static (byte type, byte[] bytes) EncodeFloat64Array(double[] arr)
    {
        var buf = new byte[2 + arr.Length * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)arr.Length);
        for (int i = 0; i < arr.Length; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(2 + i * 8), arr[i]);
        return (UdpProtocol.TypeFloat64Array, buf);
    }

    public void Dispose()
    {
        _mappingCts?.Cancel();
        _mappingCts?.Dispose();
        _udp.Dispose();
    }
}
