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
    "Plcs": [
      {
        "Id": "plc1",
        "Driver": "S7",
        "Host": "192.168.0.10",
        "Rack": 0,
        "Slot": 1,
        "Tags": [
          { "Name": "temperature", "Address": "DB10.DBD0", "Type": "Real", "PollMs": 500 },
          { "Name": "setpoint",    "Address": "DB10.DBD4", "Type": "Real", "PollMs": 1000 }
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
