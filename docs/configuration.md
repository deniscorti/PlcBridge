# Configurazione

La configurazione è caricata da:
1. `appsettings.json` (default committato)
2. `appsettings.{Environment}.json`
3. `appsettings.Local.json` (gitignorato, override locali)
4. Variabili d'ambiente con prefisso `BRIDGE_`

---

## Esempio: DataProvider

Sorgente dati pura. Legge da ADS e/o riceve UDP, espone via WS/REST.

```jsonc
{
  "Bridge": {
    "Mode": "DataProvider",
    "Http": { "Port": 5080 },
    "WebSocket": {
      "Path": "/ws",
      "MaxConnections": 100
    },
    "Auth": {
      "ApiKey": "provider-key-123",
      "JwtSecret": "shared-secret-for-jwt"
    },
    "DataSources": [
      {
        "Id": "linea1",
        "Inputs": [
          {
            "Type": "Ads",
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
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "Tags": [
              { "Name": "vibration", "Offset": 0, "Length": 12, "DataKind": "Telemetry" }
            ]
          }
        ]
      },
      {
        "Id": "pressa-nord",
        "Inputs": [
          {
            "Type": "Ads",
            "Host": "192.168.0.20",
            "AmsNetId": "5.23.40.2.1.1",
            "Port": 851,
            "Tags": [
              { "Name": "pressure",  "Address": "MAIN.fPressure",  "DataKind": "Telemetry", "PollMs": 200 },
              { "Name": "cycleEnd", "Address": "MAIN.bCycleEnd", "DataKind": "Event" }
            ]
          }
        ]
      }
    ]
  },
  "Serilog": { "MinimumLevel": "Information" }
}
```

**Note**:
- Un DataSource può avere più Input (ADS + UDP coesistenti)
- I nomi dei tag devono essere univoci all'interno del DataSource
- Nessun buffer, nessuna persistenza

---

## Esempio: DataService

Buffer in memoria + persistenza Parquet. Si connette al DataProvider (o legge direttamente).

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5080 },
    "WebSocket": {
      "Path": "/ws",
      "MaxConnections": 500,
      "MaxSubscriptionsPerClient": 200,
      "MaxQueriesPerMinute": 60,
      "MaxMessageQueueSize": 10000
    },
    "Auth": {
      "ApiKey": "service-key-456",
      "JwtSecret": "shared-secret-for-jwt"
    },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "DiskPath": "./data",
      "ArchivePath": "./data/archives",
      "FileFormat": "Parquet"
    },
    "CommandTimeoutMs": 5000,
    "HeartbeatIntervalMs": 10000,
    "HeartbeatMaxMissed": 3,

    // Sorgenti da cui questo nodo riceve dati (si connette come client WS)
    "Sources": [
      {
        "Id": "linea1",
        "DataSource": "linea1",
        "Url": "ws://192.168.0.50:5080/ws",
        "ApiKey": "provider-key-123",
        "SubscribeTags": "ALL",
        "ForwardIntervalMs": 2000,
        "Compression": "none",
        "BatchMode": false
      }
    ],

    // Destinazioni UDP per stream live (DataService invia, DataServer riceve)
    "UdpDestinations": [
      {
        "Id": "server-main",
        "Host": "dataserver.example.com",
        "Port": 9200,
        "Sources": ["linea1", "pressa-nord"],
        "Enabled": true,
        "DownsampleMs": 2000,
        "MaxPacketBytes": 1400
      },
      {
        "Id": "server-backup",
        "Host": "dataserver-backup.local",
        "Port": 9200,
        "Sources": ["linea1"],
        "Enabled": false
      }
    ],

    // Oppure input diretti (senza DataProvider intermedio)
    "DataSources": [
      {
        "Id": "pressa-nord",
        "Inputs": [
          {
            "Type": "Ads",
            "Host": "192.168.0.20",
            "AmsNetId": "5.23.40.2.1.1",
            "Port": 851,
            "Tags": [
              { "Name": "pressure",  "Address": "MAIN.fPressure",  "DataKind": "Telemetry", "PollMs": 200 },
              { "Name": "cycleEnd", "Address": "MAIN.bCycleEnd", "DataKind": "Event" }
            ]
          }
        ]
      }
    ]
  },
  "Serilog": { "MinimumLevel": "Information" }
}
```

**Note**:
- `Sources` = connessione WS ad altri bridge (ogni source diventa un DataSource locale)
- `DataSources` = input diretti (ADS/UDP), come nel DataProvider
- Possono coesistere: alcuni DataSource da bridge remoti, altri da input diretti
- `ForwardIntervalMs` = downsampling telemetria per stream WS
- `UdpDestinations` = invio stream live via UDP binario, attivabili/disattivabili a runtime via comando
- Lo stesso stream può essere inviato sia via UDP che via WS — il DataServer deduplica via msgId

---

## Esempio: DataServer

Aggregatore. Si connette a uno o più DataService/DataProvider. Buffer grande, replay, caricamento archivi.

```jsonc
{
  "Bridge": {
    "Mode": "DataServer",
    "Http": { "Port": 5080 },
    "WebSocket": {
      "Path": "/ws",
      "MaxConnections": 2000,
      "MaxSubscriptionsPerClient": 500,
      "MaxQueriesPerMinute": 120,
      "MaxMessageQueueSize": 50000,
      "CompactFlushMs": 50
    },
    "Auth": {
      "ApiKey": "server-key-789",
      "JwtSecret": "shared-secret-for-jwt"
    },
    "Buffer": {
      "InMemoryMinutes": 300,
      "ChunkDurationMin": 5,
      "PersistToDisk": false,
      "ArchivePath": "./data/archives"
    },
    "CommandTimeoutMs": 10000,
    "HeartbeatIntervalMs": 10000,
    "HeartbeatMaxMissed": 3,

    // Selective live subscription: il DataServer sottoscrive upstream solo i canali visualizzati dai client
    "SelectiveSubscription": true,   // default true per DataServer
    "BackfillMinutes": 10,           // minuti di storia recuperati al primo subscribe di un canale

    // Sorgenti WS (comandi, chunk transfer, fallback stream)
    "Sources": [
      {
        "Id": "bridge-linea1",
        "DataSource": "linea1",
        "Url": "wss://bridge-service.example.com:5080/ws",
        "ApiKey": "service-key-456",
        "SubscribeTags": "ALL",
        "ForwardIntervalMs": 2000,
        "Compression": "brotli",
        "BatchMode": true,
        "ChunkSync": true,
        "ChunkSyncMaxBandwidthKbps": 100
      },
      {
        "Id": "bridge-pressa",
        "DataSource": "pressa-nord",
        "Url": "wss://bridge-service.example.com:5080/ws",
        "ApiKey": "service-key-456",
        "SubscribeTags": "ALL",
        "ForwardIntervalMs": 5000,
        "Compression": "brotli",
        "BatchMode": true,
        "ChunkSync": true,
        "ChunkSyncMaxBandwidthKbps": 50
      }
    ],

    // Ricezione stream UDP (stessa porta per tutti i DataSource)
    "UdpReceiver": {
      "ListenPort": 9200,
      "Sources": [
        { "DataSource": "linea1", "Enabled": true },
        { "DataSource": "pressa-nord", "Enabled": true }
      ]
    }
  },
  "Serilog": { "MinimumLevel": "Information" }
}
```

**Note**:
- Buffer a 300 minuti (5 ore) per storico più ampio
- `PersistToDisk: false` — non produce Parquet, li carica dal DataService
- `ChunkSync: true` — abilita trasferimento chunk full in background
- `ChunkSyncMaxBandwidthKbps` — limita la banda usata dal trasferimento background (i chunk immediate ignorano il limite)
- Compressione Brotli + batch per connessione internet WS
- `UdpReceiver` — porta unica per ricevere stream UDP da tutti i DataSource. Ogni source è attivabile/disattivabile a runtime via comando
- UDP e WS possono essere attivi contemporaneamente — deduplicazione automatica via msgId
- `CommandTimeoutMs` più alto per catena con più hop
- `SelectiveSubscription: true` — il DataServer non sottoscrive ALL ma solo i canali che i client richiedono (risparmia banda upstream)
- `BackfillMinutes: 10` — al primo subscribe di un canale, recupera N minuti di storia dall'upstream
- `CompactFlushMs: 50` — intervallo di accumulo per push compatti (più basso = meno latenza, più alto = più batching)

---

## Variabili d'ambiente (esempi)
```
BRIDGE__Mode=DataServer
BRIDGE__Http__Port=5080
BRIDGE__Auth__ApiKey=supersecret
BRIDGE__Buffer__InMemoryMinutes=300
```

---

## Client Web Vue 3

Il client web in `clients/web/` si configura tramite variabili d'ambiente Vite (prefisso `VITE_`).

Creare un file `clients/web/.env.local` (gitignorato):

```
VITE_BRIDGE_WS_URL=ws://localhost:5080/ws
VITE_BRIDGE_API_KEY=server-key-789
```

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `VITE_BRIDGE_WS_URL` | `ws://localhost:5080/ws` | URL WebSocket del Bridge |
| `VITE_BRIDGE_API_KEY` | (vuoto) | API key per autenticazione WS |

In **sviluppo** (`npm run dev`), Vite proxya le richieste `/api/*` e `/ws/*` verso `http://localhost:5080` (configurato in `vite.config.ts`).

In **produzione** (`npm run build` → `dist/`), servire i file statici con qualsiasi web server. Il client si connette direttamente all'URL specificato in `VITE_BRIDGE_WS_URL`.

---

## Strumenti di test

### UdpSimulator

```bash
dotnet run --project tools/Bridge.Tools.UdpSimulator -- [host] [port] [intervalMs] [tagCount]
```

| Parametro | Default | Descrizione |
|-----------|---------|-------------|
| `targetHost` | `127.0.0.1` | IP destinazione |
| `targetPort` | `9100` | Porta UDP |
| `intervalMs` | `200` | Intervallo tra pacchetti |
| `tagCount` | `10` | Numero tag per pacchetto |

### WebClient (debug)

```bash
dotnet run --project tools/Bridge.Tools.WebClient -- [--bridge http://localhost:5080]
# Dashboard su http://localhost:5180
```
