using System.Net;
using System.Net.Sockets;
using System.Text;
using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs.Udp;

/// <summary>
/// Receives UDP packets from a PLC or simulator and produces TagValues.
/// Parses the "custom-v1" binary protocol matching the UdpSimulator tool.
/// Supports auto-discovery via metadata packets (flag 0x10): the sender
/// periodically broadcasts DataSource/Tag definitions so no manual tag
/// configuration is needed in appsettings.json.
/// </summary>
public sealed class UdpInput : IDataInput
{
    private readonly DataSource _source;
    private readonly int _port;
    private readonly string _protocol;
    private readonly bool _autoDiscovery;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Reverse lookup: CRC32(tagName) → Tag
    private readonly Dictionary<uint, Tag> _tagIdMap = new();

    // For auto-discovery: resolved sources by name
    private readonly Dictionary<string, DataSource> _discoveredSources = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, DataSource>? _sourceResolver;

    public string SourceId => _source.Id;
    public string Protocol => "udp";
    public InputCapabilities Capabilities => InputCapabilities.Receive | InputCapabilities.Read;
    public bool IsConnected { get; private set; }
    public event Action<TagValue>? OnValue;
    public event Action<string, bool>? OnConnectionChanged;
    public event Action<DataSource>? OnSourceDiscovered;

    public UdpInput(DataSource source, int port, string protocol = "custom-v1", bool autoDiscovery = false)
    {
        _source = source;
        _port = port;
        _protocol = protocol;
        _autoDiscovery = autoDiscovery;
    }

    public void SetSourceResolver(Func<string, DataSource> resolver) => _sourceResolver = resolver;

    public Task StartAsync(CancellationToken ct = default)
    {
        // Build reverse tagId lookup from pre-configured tags
        foreach (var (_, tag) in _source.Tags)
            _tagIdMap[tag.TagId] = tag;

        _udp = new UdpClient(_port);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsConnected = true;
        OnConnectionChanged?.Invoke(SourceId, true);
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke(SourceId, false);
        if (_cts is not null) await _cts.CancelAsync();
        _udp?.Close();
        if (_receiveTask is not null)
            try { await _receiveTask; } catch (OperationCanceledException) { }
    }

    public Task<object?> ReadTagAsync(string tag, CancellationToken ct = default)
    {
        // UDP is receive-only; return last known value
        var last = _source.GetLastValue(tag);
        return Task.FromResult(last?.Value);
    }

    public Task WriteTagAsync(string tag, object value, CancellationToken ct = default)
    {
        throw new NotSupportedException("UDP input is read-only.");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var data = result.Buffer;

                if (data.Length >= 4 && (data[3] & 0x10) != 0)
                    ParseMetadataPacket(data);
                else
                    ParsePacket(data);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception)
            {
                // Log and continue — don't crash on malformed packets
            }
        }
    }

    /// <summary>
    /// Parse metadata packet (flag 0x10). Binary format:
    ///   Header (22 bytes): standard header with FlagMetadata set
    ///   Payload:
    ///     [1] source count
    ///     Per source:
    ///       [1] name length
    ///       [N] name (UTF-8)
    ///       [2] tag count
    ///       Per tag:
    ///         [1] name length
    ///         [N] name (UTF-8)
    ///         [1] DataKind (0=Telemetry, 1=Event, 2=Alarm)
    /// </summary>
    private void ParseMetadataPacket(byte[] data)
    {
        if (!_autoDiscovery || _sourceResolver is null) return;
        if (data.Length < 23) return;
        if (data[0] != 0xBD || data[1] != 0x01) return;

        var offset = 22;

        if (offset >= data.Length) return;
        var sourceCount = data[offset++];

        for (int s = 0; s < sourceCount && offset < data.Length; s++)
        {
            var nameLen = data[offset++];
            if (offset + nameLen > data.Length) return;
            var sourceName = Encoding.UTF8.GetString(data, offset, nameLen);
            offset += nameLen;

            if (offset + 2 > data.Length) return;
            var tagCount = BitConverter.ToUInt16(data, offset);
            offset += 2;

            DataSource? ds = null;
            var isNew = false;
            if (!_discoveredSources.TryGetValue(sourceName, out ds))
            {
                ds = _sourceResolver(sourceName);
                _discoveredSources[sourceName] = ds;
                isNew = true;
            }

            for (int t = 0; t < tagCount && offset < data.Length; t++)
            {
                var tagNameLen = data[offset++];
                if (offset + tagNameLen > data.Length) return;
                var tagName = Encoding.UTF8.GetString(data, offset, tagNameLen);
                offset += tagNameLen;

                if (offset >= data.Length) return;
                var kindByte = data[offset++];
                var kind = kindByte switch
                {
                    1 => DataKind.Event,
                    2 => DataKind.Alarm,
                    _ => DataKind.Telemetry
                };

                var tag = new Tag { Name = tagName, Kind = kind };
                if (ds.TryRegisterTag(tag))
                    _tagIdMap[tag.TagId] = tag;
            }

            // Fire discovery event after tags are registered
            if (isNew)
                OnSourceDiscovered?.Invoke(ds);
        }
    }

    /// <summary>
    /// Parse custom-v1 binary packet format:
    ///   Header (22 bytes):
    ///     [2] magic 0xBD 0x01
    ///     [1] version
    ///     [1] flags (bits 0-1: kind)
    ///     [4] sourceId (CRC32)
    ///     [8] timestamp (unix microseconds)
    ///     [4] sequence number
    ///     [2] tag count
    ///   Per tag (9 bytes for float32):
    ///     [4] tagId (CRC32)
    ///     [1] value type
    ///     [4+] value
    /// </summary>
    private void ParsePacket(byte[] data)
    {
        if (data.Length < 22) return;

        // Verify magic
        if (data[0] != 0xBD || data[1] != 0x01) return;

        var flags = data[3];
        var kindBits = flags & 0x03;
        var kind = kindBits switch
        {
            0 => DataKind.Telemetry,
            1 => DataKind.Event,
            2 => DataKind.Alarm,
            _ => DataKind.Telemetry
        };

        var packetSourceId = BitConverter.ToUInt32(data, 4);

        var timestampUs = BitConverter.ToInt64(data, 8);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampUs / 1000);

        var tagCount = BitConverter.ToUInt16(data, 20);

        // Resolve which DataSource this packet belongs to
        DataSource? resolvedSource = null;
        if (_autoDiscovery)
        {
            foreach (var ds in _discoveredSources.Values)
            {
                if (Crc32.Compute(ds.Id) == packetSourceId) { resolvedSource = ds; break; }
            }
        }
        resolvedSource ??= _source;

        var offset = 22;
        for (int i = 0; i < tagCount && offset + 5 <= data.Length; i++)
        {
            var tagId = BitConverter.ToUInt32(data, offset);
            offset += 4;

            var valueType = data[offset];
            offset += 1;

            object? value = null;
            switch (valueType)
            {
                case 0x01: // float32
                    if (offset + 4 > data.Length) return;
                    value = (double)BitConverter.ToSingle(data, offset);
                    offset += 4;
                    break;
                case 0x02: // float64
                    if (offset + 8 > data.Length) return;
                    value = BitConverter.ToDouble(data, offset);
                    offset += 8;
                    break;
                case 0x03: // int32
                    if (offset + 4 > data.Length) return;
                    value = (double)BitConverter.ToInt32(data, offset);
                    offset += 4;
                    break;
                case 0x04: // bool
                    if (offset + 1 > data.Length) return;
                    value = data[offset] != 0;
                    offset += 1;
                    break;
                case 0x05: // float32 array
                    if (offset + 2 > data.Length) return;
                    var count32 = BitConverter.ToUInt16(data, offset);
                    offset += 2;
                    if (offset + count32 * 4 > data.Length) return;
                    var arr32 = new double[count32];
                    for (int j = 0; j < count32; j++) { arr32[j] = BitConverter.ToSingle(data, offset); offset += 4; }
                    value = arr32;
                    break;
                case 0x06: // float64 array
                    if (offset + 2 > data.Length) return;
                    var count64 = BitConverter.ToUInt16(data, offset);
                    offset += 2;
                    if (offset + count64 * 8 > data.Length) return;
                    var arr64 = new double[count64];
                    for (int j = 0; j < count64; j++) { arr64[j] = BitConverter.ToDouble(data, offset); offset += 8; }
                    value = arr64;
                    break;
                default:
                    return; // Unknown type, stop parsing
            }

            if (_tagIdMap.TryGetValue(tagId, out var tag))
            {
                var tv = new TagValue
                {
                    Source = resolvedSource.Id,
                    Tag = tag.Name,
                    Kind = kind != DataKind.Telemetry ? kind : tag.Kind,
                    Value = value,
                    Timestamp = timestamp,
                    MsgId = resolvedSource.NextMsgId()
                };
                OnValue?.Invoke(tv);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _udp?.Dispose();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
