using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Bridge.Core.Model;
using Bridge.InterBridge.Protocol;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.UdpReceiver;

/// <summary>
/// Receives inter-bridge UDP packets on a single port for all DataSources.
/// Handles fragmentation reassembly and msgId deduplication.
/// </summary>
public sealed class UdpBridgeReceiver : IDisposable
{
    private readonly int _port;
    private readonly ILogger _logger;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // sourceId CRC32 → source name mapping
    private readonly Dictionary<uint, string> _sourceMap = new();
    // sourceId CRC32 → tagId CRC32 → tag name
    private readonly Dictionary<uint, Dictionary<uint, string>> _tagMaps = new();
    // Source enablement
    private readonly ConcurrentDictionary<string, bool> _enabledSources = new(StringComparer.OrdinalIgnoreCase);

    // Deduplication: sourceId → sliding window of seen msgIds
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _seenMsgIds = new();
    private const int MaxDeduplicationWindow = 10000;

    // Fragment reassembly: (sourceId, groupSeq, timestamp) → fragments
    private readonly ConcurrentDictionary<(uint, ushort, long), FragmentGroup> _fragments = new();

    /// <summary>Fired when a fully reassembled tag value is received.</summary>
    public event Action<TagValue>? OnValue;

    public UdpBridgeReceiver(int port, ILogger logger)
    {
        _port = port;
        _logger = logger;
    }

    public void RegisterSource(string sourceName, IReadOnlyDictionary<string, Tag> tags, bool enabled = true)
    {
        var sourceId = UdpProtocol.Crc32(sourceName);
        _sourceMap[sourceId] = sourceName;
        _enabledSources[sourceName] = enabled;

        var tagMap = new Dictionary<uint, string>();
        foreach (var (name, _) in tags)
            tagMap[Crc32.Compute(name)] = name;
        _tagMaps[sourceId] = tagMap;
    }

    public void SetSourceEnabled(string source, bool enabled) => _enabledSources[source] = enabled;

    public Task StartAsync(CancellationToken ct = default)
    {
        _udp = new UdpClient(_port);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        _logger.LogInformation("UDP inter-bridge receiver listening on port {Port}", _port);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _udp?.Close();
        if (_receiveTask is not null)
            try { await _receiveTask; } catch { }
    }

    /// <summary>Check if a msgId has already been seen (for WS/UDP deduplication).</summary>
    public bool IsDuplicate(string source, uint msgId)
    {
        var sourceId = UdpProtocol.Crc32(source);
        var seen = _seenMsgIds.GetOrAdd(sourceId, _ => new HashSet<uint>());
        lock (seen)
        {
            return seen.Contains(msgId);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                ProcessPacket(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing UDP inter-bridge packet");
            }
        }
    }

    private void ProcessPacket(byte[] data)
    {
        if (!UdpProtocol.TryReadHeader(data, out var header)) return;

        // Check source is known and enabled
        if (!_sourceMap.TryGetValue(header.SourceId, out var sourceName)) return;
        if (_enabledSources.TryGetValue(sourceName, out var enabled) && !enabled) return;

        if (header.IsFragmented)
        {
            HandleFragment(data, header, sourceName);
            return;
        }

        ParsePayload(data.AsSpan(UdpProtocol.HeaderSize), header, sourceName);
    }

    private void HandleFragment(byte[] data, UdpPacketHeader header, string sourceName)
    {
        var key = (header.SourceId, header.GroupSeq, header.TimestampUs);
        var group = _fragments.GetOrAdd(key, _ => new FragmentGroup(header.FragTotal));

        group.AddFragment(header.FragIdx, data);

        if (group.IsComplete)
        {
            _fragments.TryRemove(key, out _);
            // Reassemble and parse all fragments
            foreach (var fragData in group.GetOrderedFragments())
            {
                if (UdpProtocol.TryReadHeader(fragData, out var fragHeader))
                    ParsePayload(fragData.AsSpan(UdpProtocol.HeaderSize), fragHeader, sourceName);
            }
        }

        // Clean stale fragment groups (older than 5 seconds)
        CleanStaleFragments();
    }

    private void ParsePayload(ReadOnlySpan<byte> payload, UdpPacketHeader header, string sourceName)
    {
        if (!_tagMaps.TryGetValue(header.SourceId, out var tagMap)) return;

        var kind = header.KindBits switch
        {
            1 => DataKind.Event,
            2 => DataKind.Alarm,
            _ => DataKind.Telemetry
        };

        var ts = DateTimeOffset.FromUnixTimeMilliseconds(header.TimestampUs / 1000);
        var offset = 0;
        var recordIdx = 0u;

        while (offset + 5 <= payload.Length)
        {
            var tagId = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            offset += 4;
            var valueType = payload[offset++];

            var (value, bytesRead) = DecodeValue(payload[offset..], valueType);
            if (bytesRead < 0) break;
            offset += bytesRead;

            var msgId = header.MsgIdBase + recordIdx;
            recordIdx++;

            // Deduplication
            var seen = _seenMsgIds.GetOrAdd(header.SourceId, _ => new HashSet<uint>());
            lock (seen)
            {
                if (!seen.Add(msgId)) continue; // duplicate
                if (seen.Count > MaxDeduplicationWindow)
                {
                    // Trim oldest (approximation — remove first half)
                    var toRemove = seen.Take(seen.Count / 2).ToList();
                    foreach (var r in toRemove) seen.Remove(r);
                }
            }

            if (!tagMap.TryGetValue(tagId, out var tagName)) continue;

            OnValue?.Invoke(new TagValue
            {
                Source = sourceName,
                Tag = tagName,
                Kind = kind,
                Value = value,
                Timestamp = ts,
                MsgId = msgId
            });
        }
    }

    private static (object? value, int bytesRead) DecodeValue(ReadOnlySpan<byte> data, byte valueType)
    {
        return valueType switch
        {
            UdpProtocol.TypeFloat32 when data.Length >= 4 =>
                ((double)BinaryPrimitives.ReadSingleLittleEndian(data), 4),
            UdpProtocol.TypeFloat64 when data.Length >= 8 =>
                (BinaryPrimitives.ReadDoubleLittleEndian(data), 8),
            UdpProtocol.TypeInt32 when data.Length >= 4 =>
                ((double)BinaryPrimitives.ReadInt32LittleEndian(data), 4),
            UdpProtocol.TypeBool when data.Length >= 1 =>
                (data[0] != 0, 1),
            _ => (null, -1)
        };
    }

    private void CleanStaleFragments()
    {
        var nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        foreach (var key in _fragments.Keys.ToList())
        {
            if (nowUs - key.Item3 > 5_000_000) // 5 seconds
                _fragments.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _cts?.Dispose();
    }
}

internal sealed class FragmentGroup
{
    private readonly byte[] ?[] _fragments;
    private int _received;

    public FragmentGroup(byte totalFragments)
    {
        _fragments = new byte[totalFragments][];
    }

    public void AddFragment(byte idx, byte[] data)
    {
        if (idx < _fragments.Length && _fragments[idx] is null)
        {
            _fragments[idx] = data;
            Interlocked.Increment(ref _received);
        }
    }

    public bool IsComplete => _received == _fragments.Length;

    public IEnumerable<byte[]> GetOrderedFragments() => _fragments.Where(f => f is not null)!;
}
