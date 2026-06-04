# Bridge

Servizio bridge industriale per PLC che espone dati tramite **HTTP REST API** e **WebSocket** per comandi e streaming dati in tempo reale. Supporta tre modalità operative: **DataProvider**, **DataService** e **DataServer**, combinabili in una topologia a cascata.

## Caratteristiche
- Servizio Windows / Linux daemon (hostable come Worker Service .NET 8)
- API HTTP (Minimal API) per lettura/scrittura tag e configurazione
- WebSocket per sottoscrizioni push, query temporali, comandi bidirezionali
- Driver PLC pluggable tramite `IDataInput` + factory pattern (Mock, UDP, ADS — estensibile a Modbus, OPC-UA, ecc.)
- Gestione differenziata per tipo di dato: **telemetria** (valori continui), **eventi** (cambi di stato), **allarmi** (ISA-18.2, 4 stati)
- Buffer in memoria a chunk temporali (ChunkedRingBuffer) con durata configurabile e modificabile a runtime
- Persistenza su disco in formato Parquet, export CSV
- Comunicazione inter-bridge: WS + UDP binario con deduplicazione, chunk transfer in background
- Selective live subscription: il DataServer sottoscrive all'upstream solo i canali attivamente visualizzati dai client
- Compact push protocol: subscribe con layout posizionale, poi batch di array — fino a 13× meno banda
- Chunk transfer con priorità ai più recenti
- Autenticazione unificata (API key + JWT) per REST e WS
- Rate limiting per connessione WS
- Configurazione tramite `appsettings.json` + environment variables
- Logging strutturato (Serilog) e metriche esposte via `/api/metrics`

## Struttura
```
Bridge.sln
├── src/
│   ├── Bridge.Core/            — Dominio, interfacce, buffer, servizi
│   ├── Bridge.Inputs/          — Input concreti (Mock, UDP) + InputRegistry + factory pattern
│   ├── Bridge.Contracts/       — DTO condivisi REST/WS
│   ├── Bridge.InterBridge/     — Comunicazione inter-nodo (WsClient, UDP, chunk transfer)
│   ├── Bridge.Storage/         — Persistenza Parquet, export CSV
│   └── Bridge.Host/            — Entry point, endpoint REST/WS, auth, middleware
├── tools/
│   ├── Bridge.Tools.UdpSimulator/  — Simulatore UDP per test senza PLC
│   └── Bridge.Tools.WebClient/    — Dashboard web per test WS/REST
└── tests/
    └── Bridge.Core.Tests/      — Unit test
```

## Quick start
```bash
dotnet restore
dotnet build
dotnet run --project src/Bridge.Host
```

## Test e strumenti

### Unit test
```bash
dotnet test
```
13 test xUnit in `Bridge.Core.Tests`: DataSource (tag registration, MsgId, last value), SubscriptionBroker (exact, ALL, wildcard source, kind-based, unsubscribe), MockInput (produce values, ReadTag, unknown tag).

### UDP Simulator
Genera pacchetti UDP simili a un PLC reale — utile per testare la catena senza hardware.
```bash
dotnet run --project tools/Bridge.Tools.UdpSimulator -- [host] [port] [intervalMs] [tagCount]
# Default: 127.0.0.1 9100 200 10
```
Pacchetti con header binario (magic, CRC32 sourceId, timestamp, sequence) + N tag con sine wave + rumore. Formato identico al protocollo UDP di Bridge.

### Web Client (test)
Dashboard HTML statica per test manuali WS/REST.
```bash
dotnet run --project tools/Bridge.Tools.WebClient -- [--bridge http://localhost:5080]
# Apri http://localhost:5180
```

### Web Client (Vue 3 — produzione)
Client Vue 3 + uPlot in `clients/web/`. Vedi [docs/web-client-analysis.md](docs/web-client-analysis.md).
```bash
cd clients/web
npm install
npm run dev     # http://localhost:3000 (proxy verso Bridge)
npm run build   # dist/ per produzione
```

Vedi [docs/](docs/) per architettura, API, configurazione e [docs/testing.md](docs/testing.md) per la guida completa a test e strumenti.
