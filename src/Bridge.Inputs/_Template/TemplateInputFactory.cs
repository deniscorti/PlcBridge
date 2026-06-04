using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs._Template;

// ── Configuration ──

/// <summary>
/// Protocol-specific configuration. Add properties for your protocol's needs.
/// These map to the JSON config under DataSources[].Inputs[].
/// </summary>
public sealed class TemplateInputConfig : IDataInputConfig
{
    public string Type => "template";

    // TODO: Add your protocol-specific settings. Examples:
    //
    // For UDP-based:
    //   public int ListenPort { get; init; } = 9100;
    //
    // For TCP/connection-based:
    //   public string Host { get; init; } = "localhost";
    //   public int Port { get; init; } = 502;
    //   public int ReconnectDelayMs { get; init; } = 3000;
    //
    // For serial:
    //   public string ComPort { get; init; } = "COM1";
    //   public int BaudRate { get; init; } = 9600;
    //
    // For poll-based:
    //   public int PollIntervalMs { get; init; } = 1000;
}

// ── Factory ──

/// <summary>
/// Factory that creates TemplateInput instances from configuration.
/// Register this in Program.cs:
///   inputRegistry.Register(new TemplateInputFactory());
/// </summary>
public sealed class TemplateInputFactory : IDataInputFactory
{
    /// <summary>Must match the "Type" value in appsettings.json.</summary>
    public string Protocol => "template";

    public IDataInput Create(DataSource source, IDataInputConfig config)
    {
        var cfg = config as TemplateInputConfig ?? new TemplateInputConfig();
        return new TemplateInput(source, cfg);
    }
}

// ── Example appsettings.json usage ──
//
// "DataSources": [
//   {
//     "Id": "my-plc",
//     "Inputs": [
//       {
//         "Type": "template",          ← matches Factory.Protocol
//         "Host": "192.168.0.10",      ← mapped to TemplateInputConfig
//         "Port": 502,
//         "Tags": [
//           { "Name": "temperature", "Address": "40001", "DataKind": "Telemetry", "PollMs": 500 },
//           { "Name": "alarm_high",  "Address": "10001", "DataKind": "Alarm" }
//         ]
//       }
//     ]
//   }
// ]
//
// ── Registration in Program.cs ──
//
// var inputRegistry = new InputRegistry();
// inputRegistry.Register(new MockInputFactory());
// inputRegistry.Register(new UdpInputFactory());
// inputRegistry.Register(new TemplateInputFactory());  // ← add this line
