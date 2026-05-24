# Configurazione

La configurazione è caricata da:
1. `appsettings.json` (default committato)
2. `appsettings.{Environment}.json`
3. `appsettings.Local.json` (gitignorato, override locali)
4. Variabili d'ambiente con prefisso `PLCBRIDGE_`

## Esempio `appsettings.json`
```json
{
  "PlcBridge": {
    "Http": { "Port": 5080 },
    "WebSocket": { "Path": "/ws", "MaxConnections": 2000 },
    "Auth": { "ApiKey": "change-me" },
    "Buffer": {
      "InMemoryMinutes": 10,
      "PersistToDisk": true,
      "DiskPath": "./data"
    },
    "Plcs": [
      {
        "Id": "plc1",
        "Driver": "Ads",
        "Host": "192.168.0.10",
        "AmsNetId": "5.23.40.1.1.1",
        "Port": 851,
        "Tags": [
          { "Name": "temperature", "Address": "MAIN.fTemperature", "DataKind": "Telemetry", "PollMs": 500 },
          { "Name": "setpoint",    "Address": "MAIN.fSetpoint",    "DataKind": "Telemetry", "PollMs": 1000 },
          { "Name": "startButton", "Address": "MAIN.bStart",       "DataKind": "Event" },
          { "Name": "overtemp",    "Address": "MAIN.bOverTemp",    "DataKind": "Alarm" }
        ]
      },
      {
        "Id": "plc2",
        "Driver": "Udp",
        "ListenPort": 9100,
        "Protocol": "custom-v1",
        "Tags": [
          { "Name": "vibration", "Offset": 0, "Length": 12, "DataKind": "Telemetry" }
        ]
      }
    ]
  },
  "Serilog": {
    "MinimumLevel": "Information"
  }
}
```

## Variabili d'ambiente (esempi)
```
PLCBRIDGE__Http__Port=5080
PLCBRIDGE__Auth__ApiKey=supersecret
```
