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

    public string DestinationId { get; }
    public bool Enabled { get; set; }

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

    /// <summary>Send a batch of tag values as UDP packets.</summary>
    public async Task SendBatchAsync(IReadOnlyList<TagValue> values, DateTimeOffset timestamp)
    {
        if (!Enabled || values.Count == 0) return;

        var tsUs = timestamp.ToUnixTimeMilliseconds() * 1000;
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
            await SendGroupAsync(items, kindFlag, tsUs, firstMsgId);
        }
    }

    /// <summary>Send a single tag value.</summary>
    public async Task SendAsync(TagValue value)
    {
        if (!Enabled) return;
        await SendBatchAsync([value], value.Timestamp);
    }

    private async Task SendGroupAsync(List<TagValue> values, byte kindFlag, long tsUs, uint msgIdBase)
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

            UdpProtocol.WriteHeader(buf, flags, _sourceId, tsUs, seq, (byte)i, fragTotal, msgIdBase);

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

    public void Dispose() => _udp.Dispose();
}
