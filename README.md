# PlcBridge

Servizio bridge per PLC industriali che espone i dati tramite **HTTP REST API** e **WebSocket** per comandi e streaming dati in tempo reale.

## Caratteristiche
- Servizio Windows / Linux daemon (hostable come Worker Service .NET)
- API HTTP per lettura/scrittura tag e configurazione
- WebSocket per sottoscrizioni e push di eventi dal PLC
- Driver PLC pluggable (Siemens S7, Modbus TCP, OPC UA, ...)
- Configurazione tramite `appsettings.json` + environment variables
- Logging strutturato (Serilog) e metriche (OpenTelemetry)

## Struttura
- `src/PlcBridge.Host` — entry point, hosting, DI, API, WebSocket
- `src/PlcBridge.Core` — modello di dominio, astrazioni, servizi
- `src/PlcBridge.Plc` — implementazioni dei driver PLC
- `src/PlcBridge.Contracts` — DTO/contratti condivisi (HTTP + WS)
- `tests/` — unit & integration test
- `docs/` — documentazione di progetto
- `docker/` — Dockerfile e compose

## Quick start
```bash
dotnet restore
dotnet build
dotnet run --project src/PlcBridge.Host
```

Vedi [docs/](docs/) per architettura, API e configurazione.
