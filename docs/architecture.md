# Architettura

## Vista a livelli

```
+-----------------------------------------------+
|              PlcBridge.Host                   |
|  - Program.cs / Worker                        |
|  - HTTP endpoints (Minimal API)               |
|  - WebSocket endpoint + hub                   |
|  - DI, configurazione, logging                |
+----------------------+------------------------+
                       |
+----------------------v------------------------+
|              PlcBridge.Core                   |
|  - Modello di dominio (Plc, Tag, Value)       |
|  - Interfacce: IPlcDriver, ITagCache,         |
|    ISubscriptionBroker                        |
|  - Servizi applicativi                        |
+----------------------+------------------------+
                       |
+----------------------v------------------------+
|              PlcBridge.Plc                    |
|  - Driver concreti: S7, Modbus, OpcUa, ...    |
+-----------------------------------------------+

PlcBridge.Contracts: DTO condivisi tra Host e client (HTTP/WS)
```

## Flusso lettura tag (REST)
1. Client → `GET /api/plcs/{id}/tags/{name}`
2. Host risolve `ITagCache` → se valore fresco, ritorna dal cache.
3. Altrimenti `IPlcDriver.ReadAsync` → aggiorna cache → risponde.

## Flusso sottoscrizione (WebSocket)
1. Client apre `ws://host/ws`.
2. Invia messaggio `{ "op": "subscribe", "tags": ["plc1.temp", ...] }`.
3. `SubscriptionBroker` registra la sessione.
4. Su variazione tag, broker pubblica messaggio `{ "type":"value", ... }`.

## Componenti cross-cutting
- **Configuration**: `appsettings.json` + env vars + `IOptionsMonitor`.
- **Logging**: Serilog (console + file JSON).
- **Telemetry**: OpenTelemetry (metrics + tracing).
- **Auth**: API key middleware su REST; handshake token su WS.
