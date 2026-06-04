// ╔══════════════════════════════════════════════════════════════════════╗
// ║  TEMPLATE: Copy this folder and rename to implement a new input.   ║
// ║  Replace "Template" with your protocol name (e.g. "Modbus").       ║
// ║  Delete this file from the build once you have your real input.     ║
// ╚══════════════════════════════════════════════════════════════════════╝
//
// Steps:
//   1. Copy _Template/ → YourProtocol/ (e.g. Modbus/)
//   2. Rename TemplateInput → ModbusInput, etc.
//   3. Implement the protocol-specific logic in StartAsync / receive loop
//   4. Create the factory: ModbusInputFactory : IDataInputFactory
//   5. Register in Program.cs: inputRegistry.Register(new ModbusInputFactory());
//   6. Add config section in appsettings.json under DataSources[].Inputs[]
//
// Everything downstream (buffer, WS push, Parquet flush, chunk transfer,
// compact push, selective subscription) works automatically once your input
// emits TagValue via OnValue.

using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs._Template;

/// <summary>
/// Template input — copy and adapt for your protocol.
///
/// Key responsibilities:
///   - Connect to the data source (PLC, device, network socket, etc.)
///   - Parse incoming data into TagValue records
///   - Emit each TagValue via the OnValue event
///   - Handle reconnection on failure
///   - Support graceful stop
/// </summary>
public sealed class TemplateInput : IDataInput
{
    private readonly DataSource _source;
    private readonly TemplateInputConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // ── IDataInput identity ──

    public string SourceId => _source.Id;

    /// <summary>Protocol name — must match the factory's Protocol property.</summary>
    public string Protocol => "template";

    /// <summary>
    /// Declare what this input can do. Adjust flags for your protocol:
    ///   Receive    — can push data (almost always true)
    ///   Read       — can read a tag value on demand
    ///   Write      — can write a tag value to the source
    ///   Subscribe  — supports subscription-based push (vs. polling)
    ///   BatchRead  — can read multiple tags at once
    ///   Browse     — can enumerate available tags from the source
    /// </summary>
    public InputCapabilities Capabilities =>
        InputCapabilities.Receive | InputCapabilities.Read;

    public bool IsConnected { get; private set; }

    // ── Events ──

    /// <summary>Emit a TagValue here — the rest of Bridge picks it up automatically.</summary>
    public event Action<TagValue>? OnValue;

    /// <summary>Optional: notify when connection state changes.</summary>
    public event Action<string, bool>? OnConnectionChanged;

    // ── Constructor ──

    public TemplateInput(DataSource source, TemplateInputConfig config)
    {
        _source = source;
        _config = config;
    }

    // ── Lifecycle ──

    public Task StartAsync(CancellationToken ct = default)
    {
        // TODO: Initialize your connection here (open socket, connect to device, etc.)

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

        // TODO: Close your connection/socket here

        if (_receiveTask is not null)
            try { await _receiveTask; } catch (OperationCanceledException) { }
    }

    // ── On-demand read (optional, throw NotSupportedException if not supported) ──

    public Task<object?> ReadTagAsync(string tag, CancellationToken ct = default)
    {
        // Option A: return last known value from DataSource cache
        var last = _source.GetLastValue(tag);
        return Task.FromResult(last?.Value);

        // Option B: read directly from the device
        // var value = await ReadFromDevice(tag, ct);
        // return value;
    }

    // ── Write (optional) ──

    public Task WriteTagAsync(string tag, object value, CancellationToken ct = default)
    {
        // If your protocol supports writing:
        // await WriteToDevice(tag, value, ct);
        // return;

        throw new NotSupportedException($"{Protocol} input is read-only.");
    }

    // ── Receive loop — this is where the core work happens ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ┌─────────────────────────────────────────────────────┐
                // │  TODO: Replace this with your protocol's receive    │
                // │  logic. Examples:                                   │
                // │                                                     │
                // │  UDP:    var result = await udpClient.ReceiveAsync  │
                // │  TCP:    var bytes = await stream.ReadAsync(...)    │
                // │  Serial: var data = await port.ReadAsync(...)       │
                // │  Poll:   var resp = await client.ReadRegisters(...) │
                // └─────────────────────────────────────────────────────┘

                // Example: simulate receiving data every second
                await Task.Delay(1000, ct);
                var rawData = new byte[0]; // ← replace with actual received bytes

                // Parse the raw data and emit TagValues
                ParseAndEmit(rawData);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // Log and continue — don't crash on malformed packets.
                // For connection-oriented protocols, trigger reconnection here.

                // Example reconnect pattern:
                // IsConnected = false;
                // OnConnectionChanged?.Invoke(SourceId, false);
                // await ReconnectAsync(ct);
            }
        }
    }

    // ── Parse incoming data ──

    private void ParseAndEmit(byte[] data)
    {
        // ┌──────────────────────────────────────────────────────────────┐
        // │  TODO: Parse your protocol's payload here.                   │
        // │                                                              │
        // │  For each value you extract, create a TagValue and invoke    │
        // │  OnValue. That's it — everything else is automatic:          │
        // │    → SourceManager.HandleValue                               │
        // │    → BufferManager.Add (if DataService/DataServer)           │
        // │    → WS push to subscribers                                  │
        // │    → Parquet flush on chunk seal                             │
        // │    → UDP inter-bridge forward                                │
        // │    → Compact push batching                                   │
        // └──────────────────────────────────────────────────────────────┘

        // Example: you parsed tag "temperature" with value 23.5
        //
        // var tv = new TagValue
        // {
        //     Source = SourceId,
        //     Tag = "temperature",               // must match a registered tag name
        //     Kind = DataKind.Telemetry,          // or Event, Alarm
        //     Value = 23.5,                       // object: double, bool, string, double[], etc.
        //     Timestamp = DateTimeOffset.UtcNow,  // or parsed from the packet
        //     MsgId = _source.NextMsgId()         // incremental per DataSource
        // };
        // OnValue?.Invoke(tv);

        // ── Tag resolution strategies ──
        //
        // Different protocols identify tags differently. Common patterns:
        //
        // 1. CRC32 of tag name (like custom-v1 UDP):
        //    Build a reverse lookup at StartAsync:
        //      foreach (var (_, tag) in _source.Tags)
        //          _tagIdMap[tag.TagId] = tag;
        //    Then resolve: if (_tagIdMap.TryGetValue(tagId, out var tag)) ...
        //
        // 2. Fixed offset / register address (like Modbus):
        //    Map register addresses to tag names in config:
        //      Tags: [{ Name: "temp", Address: "40001" }]
        //    Then resolve by address.
        //
        // 3. String name in the packet (like OPC-UA, MQTT):
        //    Match directly against _source.Tags dictionary.
        //
        // 4. Index-based (like some binary protocols):
        //    Use array position to map to configured tag order.
    }

    // ── Cleanup ──

    public ValueTask DisposeAsync()
    {
        // TODO: Dispose your connection/socket/client here
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
