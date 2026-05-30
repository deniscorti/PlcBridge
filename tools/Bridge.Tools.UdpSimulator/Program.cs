using System.Net;
using System.Net.Sockets;
using System.Text;

// ──────────────────────────────────────────────────────────
//  Bridge UDP Simulator — Configurable
//
//  Generates sine/sawtooth/triangle curves for telemetry,
//  with configurable groups, channel count, and frequency.
//
//  Usage:
//    dotnet run -- [targetHost] [targetPort] [intervalMs] [groups]
//
//  groups format: "GroupName:count,GroupName:count,..."
//    e.g. "Temperatures:20,Pressures:10,Speeds:5"
//
//  Defaults: localhost 9100 50 "Line1:10,Line2:5"
// ──────────────────────────────────────────────────────────

var targetHost = args.Length > 0 ? args[0] : "127.0.0.1";
var targetPort = args.Length > 1 ? int.Parse(args[1]) : 9100;
var intervalMs = args.Length > 2 ? int.Parse(args[2]) : 50;
var groupsDef  = args.Length > 3 ? args[3] : "Line1:10,Line2:5";

// Parse groups
var sources = new List<SimSource>();
foreach (var part in groupsDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    var split = part.Split(':');
    var name = split[0].Trim();
    var count = split.Length > 1 ? int.Parse(split[1].Trim()) : 10;

    var tags = new List<SimTag>();
    for (var i = 1; i <= count; i++)
        tags.Add(new SimTag($"Canale{i}", 0)); // Telemetry

    // Add 1-2 events and 1 alarm per group for realism
    tags.Add(new SimTag("cycle_start", 1));
    tags.Add(new SimTag("cycle_end", 1));
    tags.Add(new SimTag("alarm_high", 2));

    sources.Add(new SimSource(name, tags));
}

var totalTags = sources.Sum(s => s.Tags.Count);
var telemetryCount = sources.Sum(s => s.Tags.Count(t => t.Kind == 0));

Console.WriteLine("Bridge UDP Simulator");
Console.WriteLine($"  Target:      {targetHost}:{targetPort}");
Console.WriteLine($"  Interval:    {intervalMs} ms ({1000.0 / intervalMs:F0} Hz)");
Console.WriteLine($"  Groups:      {sources.Count}");
Console.WriteLine($"  Total tags:  {totalTags} ({telemetryCount} telemetry)");
Console.WriteLine($"  Metadata:    every 10s");
Console.WriteLine($"  Press Ctrl+C to stop");
Console.WriteLine();

foreach (var src in sources)
{
    var telCount = src.Tags.Count(t => t.Kind == 0);
    var evtCount = src.Tags.Count(t => t.Kind == 1);
    var almCount = src.Tags.Count(t => t.Kind == 2);
    Console.WriteLine($"  [{src.Name}] {telCount} telemetry, {evtCount} events, {almCount} alarms");
}
Console.WriteLine();

using var udp = new UdpClient();
var endpoint = new IPEndPoint(IPAddress.Parse(targetHost), targetPort);

var rng = new Random(42); // Fixed seed for reproducible curves
var tick = 0u;
var cts = new CancellationTokenSource();
var lastMetadataSent = DateTimeOffset.MinValue;
const int metadataIntervalSec = 10;

// Pre-compute curve parameters per tag for variety
var curveParams = new Dictionary<string, CurveParam>();
var globalIdx = 0;
foreach (var src in sources)
    foreach (var tag in src.Tags.Where(t => t.Kind == 0))
    {
        var key = $"{src.Name}/{tag.Name}";
        curveParams[key] = new CurveParam(
            CurveType: (CurveType)(globalIdx % 3),
            BaseValue: 15.0 + (globalIdx % 7) * 5.0,    // 15-45 range
            Amplitude: 5.0 + (globalIdx % 5) * 2.0,     // 5-13 amplitude
            Period:    0.01 + (globalIdx % 11) * 0.003,  // different speeds
            Phase:     globalIdx * 0.73,                  // offset each channel
            NoiseLevel: 0.2 + (globalIdx % 3) * 0.1      // 0.2-0.4 noise
        );
        globalIdx++;
    }

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var now = DateTimeOffset.UtcNow;

        // Send metadata packet every 10 seconds
        if ((now - lastMetadataSent).TotalSeconds >= metadataIntervalSec)
        {
            var metaPacket = BuildMetadataPacket(sources, now);
            await udp.SendAsync(metaPacket, metaPacket.Length, endpoint);
            lastMetadataSent = now;
            Console.WriteLine($"  [{now:HH:mm:ss.fff}] Sent metadata ({metaPacket.Length} bytes, {sources.Count} sources, {totalTags} tags)");
        }

        // Send data packets — one per source
        foreach (var src in sources)
        {
            var packet = BuildDataPacket(src, tick, now, rng, curveParams);
            await udp.SendAsync(packet, packet.Length, endpoint);
        }

        if (tick % 200 == 0)
        {
            Console.WriteLine($"  [{now:HH:mm:ss.fff}] tick #{tick} — {sources.Count} packets sent");
        }

        tick++;
        await Task.Delay(intervalMs, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Normal shutdown
}

Console.WriteLine($"\nStopped after {tick} packets.");

// ──────────────────────────────────────────────────────────
//  Metadata packet builder (flag 0x10)
// ──────────────────────────────────────────────────────────

static byte[] BuildMetadataPacket(List<SimSource> sources, DateTimeOffset ts)
{
    var payloadSize = 1;
    foreach (var src in sources)
    {
        payloadSize += 1 + Encoding.UTF8.GetByteCount(src.Name);
        payloadSize += 2;
        foreach (var tag in src.Tags)
            payloadSize += 1 + Encoding.UTF8.GetByteCount(tag.Name) + 1;
    }

    var buf = new byte[22 + payloadSize];
    var offset = 0;

    buf[offset++] = 0xBD;
    buf[offset++] = 0x01;
    buf[offset++] = 0x01;
    buf[offset++] = 0x10; // metadata

    offset += 4; // sourceId = 0

    var usec = ts.ToUnixTimeMilliseconds() * 1000;
    BitConverter.TryWriteBytes(buf.AsSpan(offset), usec);
    offset += 8;

    offset += 4 + 2; // sequence + reserved

    buf[offset++] = (byte)sources.Count;

    foreach (var src in sources)
    {
        var nameBytes = Encoding.UTF8.GetBytes(src.Name);
        buf[offset++] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buf, offset);
        offset += nameBytes.Length;

        BitConverter.TryWriteBytes(buf.AsSpan(offset), (ushort)src.Tags.Count);
        offset += 2;

        foreach (var tag in src.Tags)
        {
            var tagBytes = Encoding.UTF8.GetBytes(tag.Name);
            buf[offset++] = (byte)tagBytes.Length;
            tagBytes.CopyTo(buf, offset);
            offset += tagBytes.Length;
            buf[offset++] = tag.Kind;
        }
    }

    return buf;
}

// ──────────────────────────────────────────────────────────
//  Data packet builder — generates curve values
// ──────────────────────────────────────────────────────────

static byte[] BuildDataPacket(SimSource source, uint seq, DateTimeOffset ts, Random rng, Dictionary<string, CurveParam> curves)
{
    var telemetryTags = source.Tags.Where(t => t.Kind == 0).ToList();
    var size = 22 + telemetryTags.Count * 9;
    var buf = new byte[size];
    var offset = 0;

    buf[offset++] = 0xBD;
    buf[offset++] = 0x01;
    buf[offset++] = 0x01;
    buf[offset++] = 0x00; // telemetry

    var sourceId = Crc32(source.Name);
    BitConverter.TryWriteBytes(buf.AsSpan(offset), sourceId);
    offset += 4;

    var usec = ts.ToUnixTimeMilliseconds() * 1000;
    BitConverter.TryWriteBytes(buf.AsSpan(offset), usec);
    offset += 8;

    BitConverter.TryWriteBytes(buf.AsSpan(offset), seq);
    offset += 4;

    BitConverter.TryWriteBytes(buf.AsSpan(offset), (ushort)telemetryTags.Count);
    offset += 2;

    foreach (var tag in telemetryTags)
    {
        var tagId = Crc32(tag.Name);
        BitConverter.TryWriteBytes(buf.AsSpan(offset), tagId);
        offset += 4;

        buf[offset++] = 0x01; // float32

        var key = $"{source.Name}/{tag.Name}";
        var cp = curves[key];
        var t = seq * cp.Period + cp.Phase;
        var noise = (rng.NextDouble() - 0.5) * 2.0 * cp.NoiseLevel;

        var value = cp.CurveType switch
        {
            CurveType.Sine     => cp.BaseValue + cp.Amplitude * Math.Sin(t) + noise,
            CurveType.Sawtooth => cp.BaseValue + cp.Amplitude * (2.0 * (t / (2 * Math.PI) - Math.Floor(t / (2 * Math.PI) + 0.5))) + noise,
            CurveType.Triangle => cp.BaseValue + cp.Amplitude * (2.0 * Math.Abs(2.0 * (t / (2 * Math.PI) - Math.Floor(t / (2 * Math.PI) + 0.5))) - 1.0) + noise,
            _                  => cp.BaseValue + noise,
        };

        BitConverter.TryWriteBytes(buf.AsSpan(offset), (float)value);
        offset += 4;
    }

    return buf;
}

static uint Crc32(string input)
{
    uint crc = 0xFFFFFFFF;
    foreach (var b in Encoding.UTF8.GetBytes(input))
    {
        crc ^= b;
        for (var j = 0; j < 8; j++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return crc ^ 0xFFFFFFFF;
}

// ──────────────────────────────────────────────────────────
//  Models
// ──────────────────────────────────────────────────────────

enum CurveType { Sine, Sawtooth, Triangle }

sealed record SimTag(string Name, byte Kind);
sealed record SimSource(string Name, List<SimTag> Tags);
sealed record CurveParam(CurveType CurveType, double BaseValue, double Amplitude, double Period, double Phase, double NoiseLevel);
