# Guida alle Configurazioni Bridge

Ogni modalità operativa ha il proprio **progetto separato**: `Bridge.DataProvider`, `Bridge.DataService`, `Bridge.DataServer`.
Ognuno si lancia con `dotnet run --project src/Bridge.DataXxx` e ha il proprio `appsettings.json`.

Ogni `appsettings.json` contiene **tutte le sezioni possibili** con un flag `Enabled` (true/false) per attivare/disattivare ogni funzionalità. Le sezioni con `Enabled: false` sono ignorate ma restano visibili come riferimento.

Questa guida elenca le combinazioni di configurazione più comuni.

## Porte: regola fondamentale

Ogni nodo è un **server HTTP/WS** su una porta propria. Se si eseguono più nodi sulla stessa macchina, ognuno **deve avere una porta `Http.Port` diversa**:

| Nodo | Porta tipica | Ruolo server | Connessione client |
|------|-------------|--------------|-------------------|
| DataProvider | 5080 | Accetta client WS/REST | Nessuna (solo server) |
| DataService | 5081 | Accetta client WS/REST | Si collega al DataProvider su :5080 |
| DataServer | 5082 | Accetta client WS/REST | Si collega al DataService su :5081 |

La porta configurata in `Http.Port` viene applicata direttamente a Kestrel. `Sources[].Url` specifica a **quale nodo upstream collegarsi** come client WS.

---

## Indice

- [DataProvider](#dataprovider)
  - [P1 — Lettura diretta da PLC (ADS)](#p1--lettura-diretta-da-plc-ads)
  - [P2 — Ricezione UDP dal PLC](#p2--ricezione-udp-dal-plc)
  - [P3 — Mock (simulazione)](#p3--mock-simulazione)
  - [P4 — Multi-input (ADS + UDP)](#p4--multi-input-ads--udp)
- [DataService](#dataservice)
  - [S1 — Da PLC via ADS + buffer + persistenza](#s1--da-plc-via-ads--buffer--persistenza)
  - [S2 — Da PLC via UDP + buffer + persistenza](#s2--da-plc-via-udp--buffer--persistenza)
  - [S2b — All-in-one per test (UDP + buffer + Parquet write-only)](#s2b--all-in-one-per-test-udp--buffer-in-memoria--parquet-write-only)
  - [S2c — All-in-one con Auto-Discovery (zero config tag)](#s2c--all-in-one-con-auto-discovery-zero-configurazione-tag)
  - [S3 — Da DataProvider upstream (WS) + persistenza](#s3--da-dataprovider-upstream-ws--persistenza)
  - [S4 — Da DataProvider upstream + forward UDP verso DataServer](#s4--da-dataprovider-upstream--forward-udp-verso-dataserver)
  - [S5 — Multi-source (PLC locale + upstream remoto)](#s5--multi-source-plc-locale--upstream-remoto)
- [DataServer](#dataserver)
  - [V1 — Da DataService via WebSocket](#v1--da-dataservice-via-websocket)
  - [V2 — Da DataService via WS + ricezione live UDP](#v2--da-dataservice-via-ws--ricezione-live-udp)
  - [V3 — Multi-source (più DataService upstream)](#v3--multi-source-più-dataservice-upstream)
  - [V4 — Da DataProvider diretto (senza DataService intermedio)](#v4--da-dataprovider-diretto-senza-dataservice-intermedio)
- [Topologie di esempio](#topologie-di-esempio)
- [Riepilogo funzionalità per modo](#riepilogo-funzionalità-per-modo)

---

## DataProvider

Il DataProvider è il punto di ingresso dati. Legge direttamente dai driver (ADS, UDP, Mock) ed espone i dati via REST + WebSocket. **Non ha buffer né persistenza**.

### P1 — Lettura diretta da PLC (ADS)

Collegamento diretto al PLC Beckhoff TwinCAT via protocollo ADS.

```jsonc
{
  "Bridge": {
    "Mode": "DataProvider",
    "Http": { "Port": 5080 },
    "WebSocket": { "Path": "/ws" },
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
              { "Name": "temperature",  "Address": "MAIN.fTemperature",  "DataKind": "Telemetry", "PollMs": 500 },
              { "Name": "startButton",  "Address": "MAIN.bStart",        "DataKind": "Event" },
              { "Name": "overtemp",     "Address": "MAIN.bOverTemp",     "DataKind": "Alarm" }
            ]
          }
        ]
      }
    ]
  }
}
```

**Flusso:** `PLC ──ADS──► DataProvider ──WS/REST──► Client`

---

### P2 — Ricezione UDP dal PLC

Il PLC invia pacchetti UDP in formato binario custom.

```jsonc
{
  "Bridge": {
    "Mode": "DataProvider",
    "Http": { "Port": 5080 },
    "DataSources": [
      {
        "Id": "pressa",
        "Inputs": [
          {
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "Tags": [
              { "Name": "pressure",   "DataKind": "Telemetry", "Offset": 0,  "Length": 4 },
              { "Name": "cycleEnd",   "DataKind": "Event",     "Offset": 4,  "Length": 1 },
              { "Name": "overpress",  "DataKind": "Alarm",     "Offset": 5,  "Length": 1 }
            ]
          }
        ]
      }
    ]
  }
}
```

**Flusso:** `PLC ──UDP──► DataProvider ──WS/REST──► Client`

> **Alternativa Auto-Discovery:** aggiungendo `"AutoDiscovery": true` all'input UDP, la sezione `Tags` può essere omessa. Il PLC/simulatore invierà periodicamente un pacchetto metadata con la struttura dei tag. Vedi [S2c](#s2c--all-in-one-con-auto-discovery-zero-configurazione-tag) per dettagli sul formato.

---

### P3 — Mock (simulazione)

Per sviluppo e test, senza PLC fisico.

```jsonc
{
  "Bridge": {
    "Mode": "DataProvider",
    "Http": { "Port": 5080 },
    "DataSources": [
      {
        "Id": "sim",
        "Inputs": [
          {
            "Type": "Mock",
            "Tags": [
              { "Name": "temperature",  "DataKind": "Telemetry", "PollMs": 500 },
              { "Name": "startButton",  "DataKind": "Event",     "PollMs": 2000 },
              { "Name": "overtemp",     "DataKind": "Alarm",     "PollMs": 5000 }
            ]
          }
        ]
      }
    ]
  }
}
```

---

### P4 — Multi-input (ADS + UDP)

Un singolo DataProvider che gestisce più input da sorgenti diverse.

```jsonc
{
  "Bridge": {
    "Mode": "DataProvider",
    "Http": { "Port": 5080 },
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
              { "Name": "temperature", "Address": "MAIN.fTemp", "DataKind": "Telemetry", "PollMs": 500 }
            ]
          },
          {
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "Tags": [
              { "Name": "pressure", "DataKind": "Telemetry", "Offset": 0, "Length": 4 }
            ]
          }
        ]
      }
    ]
  }
}
```

**Flusso:** `PLC(ADS) + PLC(UDP) ──► DataProvider ──WS/REST──► Client`

> **Alternativa Auto-Discovery:** l'input UDP può usare `"AutoDiscovery": true` per eliminare la configurazione manuale dei tag. L'input ADS continua a richiedere i tag espliciti (con indirizzo PLC).

---

## DataService

Il DataService è l'aggregatore intermedio. Bufferizza in memoria, persiste su Parquet, e può fare downsampling e forwarding verso DataServer.

### S1 — Da PLC via ADS + buffer + persistenza

Lettura diretta dal PLC con buffer e salvataggio su disco.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5080 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ParquetOutputPath": "./data/parquet"
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
              { "Name": "temperature", "Address": "MAIN.fTemp", "DataKind": "Telemetry", "PollMs": 500 },
              { "Name": "overtemp",    "Address": "MAIN.bOverTemp", "DataKind": "Alarm" }
            ]
          }
        ]
      }
    ]
  }
}
```

**Flusso:** `PLC ──ADS──► DataService [buffer 1h + Parquet] ──WS/REST──► Client`

---

### S2 — Da PLC via UDP + buffer + persistenza

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5080 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ParquetOutputPath": "./data/parquet"
    },
    "DataSources": [
      {
        "Id": "pressa",
        "Inputs": [
          {
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "Tags": [
              { "Name": "pressure", "DataKind": "Telemetry", "Offset": 0, "Length": 4 }
            ]
          }
        ]
      }
    ]
  }
}
```

**Flusso:** `PLC ──UDP──► DataService [buffer + Parquet] ──WS/REST──► Client`

> **Alternativa Auto-Discovery:** aggiungendo `"AutoDiscovery": true` all'input UDP, la sezione `Tags` può essere omessa. Vedi [S2c](#s2c--all-in-one-con-auto-discovery-zero-configurazione-tag).

---

### S2b — All-in-one per test (UDP + buffer in memoria + Parquet write-only)

Configurazione "standalone" ideale per sviluppo e test con il web client.
Il DataService riceve dal simulatore UDP, mantiene una finestra rolling in memoria (unica fonte per query e client WS), e scrive Parquet in background come archivio — **senza mai rileggere da disco**.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5080 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ReadFromDisk": false,
      "ParquetOutputPath": "./data/parquet"
    },
    "DataSources": [
      {
        "Id": "sim",
        "Inputs": [
          {
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "Tags": [
              { "Name": "temperature", "DataKind": "Telemetry", "Offset": 0, "Length": 4 },
              { "Name": "pressure",   "DataKind": "Telemetry", "Offset": 4, "Length": 4 },
              { "Name": "cycleEnd",   "DataKind": "Event",     "Offset": 8, "Length": 1 },
              { "Name": "overtemp",   "DataKind": "Alarm",     "Offset": 9, "Length": 1 }
            ]
          }
        ]
      }
    ]
  }
}
```

**Comportamento:**

| Aspetto | Dettaglio |
|---------|-----------|
| **Live stream** | I client WS ricevono i dati in tempo reale appena arrivano dal simulatore |
| **Query temporali** | Rispondono solo dalla finestra in memoria (ultimi 60 min) |
| **Parquet** | Scritto a chunk chiusi (ogni 5 min), mai riletto — solo archivio |
| **Oltre la finestra** | I dati più vecchi di 60 min escono dal buffer e non sono più queryabili |

**Flusso:**
```
Simulatore UDP ──► DataService [buffer 60min in RAM] ──WS/REST──► Web Client di test
                        │
                        └── Parquet (write-only, archivio)
```

> **Nota:** `ReadFromDisk: false` garantisce che le query temporali non tocchino mai il filesystem. Se in futuro servisse accedere ai dati archiviati, basta impostarlo a `true` o caricarli su un DataServer.

> **Consiglio:** per eliminare la configurazione manuale dei tag, usa la variante S2c con `AutoDiscovery: true`.

---

### S2c — All-in-one con Auto-Discovery (zero configurazione tag)

Come S2b ma con `AutoDiscovery: true`: il simulatore UDP invia un pacchetto di metadata ogni 10 secondi con la lista di source e tag. Il DataService li registra automaticamente — **nessun tag da configurare manualmente**.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5080 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ReadFromDisk": false,
      "ParquetOutputPath": "./data/parquet"
    },
    "DataSources": [
      {
        "Id": "udp-receiver",
        "Inputs": [
          {
            "Type": "Udp",
            "ListenPort": 9100,
            "Protocol": "custom-v1",
            "AutoDiscovery": true
          }
        ]
      }
    ]
  }
}
```

**Comportamento:**

| Aspetto | Dettaglio |
|---------|-----------|
| **Metadata** | Il simulatore invia ogni 10s un pacchetto con la struttura delle source e tag |
| **Auto-registrazione** | Al ricevere il metadata, il DataService crea le source (es. "Temperatures", "Pressures", "Production") e registra i tag con il DataKind corretto |
| **Nessun tag manuale** | La sezione `Tags` in configurazione è vuota — tutto arriva dal metadata |
| **Live stream** | Funziona identicamente a S2b dopo la prima ricezione del metadata |
| **Aggiunta tag** | Se il simulatore aggiunge nuovi tag al metadata, vengono registrati al volo |

**Flusso:**
```
Simulatore UDP ──metadata (10s)──► DataService [auto-registra Sources+Tags]
               ──data (50ms)────►             [buffer 60min] ──WS/REST──► Web Client
                                                    │
                                                    └── Parquet (write-only)
```

**Source create dal simulatore di default:**

| Source | Tag | Tipo |
|------------|-----|------|
| Temperatures | temp_zone1, temp_zone2, temp_zone3, temp_ambient | Telemetry |
| | overtemp_zone1, overtemp_zone2 | Alarm |
| Pressures | press_main, press_secondary, press_diff | Telemetry |
| | overpress | Alarm |
| Production | speed_rpm, torque_nm, cycle_count | Telemetry |
| | cycle_start, cycle_end | Event |
| | emergency_stop | Alarm |

> **Nota:** con `AutoDiscovery: true` la configurazione UDP diventa minimale (solo `ListenPort` + `Protocol`). È la modalità consigliata per sviluppo e test.

---

### S3 — Da DataProvider upstream (WS) + persistenza

Il DataService si collega come client WS a un DataProvider remoto.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5081 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ParquetOutputPath": "./data/parquet"
    },
    "Sources": [
      {
        "Id": "from-provider",
        "Url": "ws://192.168.0.50:5080/ws",
        "ApiKey": "provider-secret",
        "AutoDiscovery": true,
        "Tags": [],
        "ForwardIntervalMs": 2000,
        "BatchMode": true,
        "ChunkSync": true,
        "ChunkSyncMaxBandwidthKbps": 100
      }
    ]
  }
}
```

**Flusso:** `PLC ──► DataProvider ──WS──► DataService [buffer + Parquet] ──WS/REST──► Client`

> **Auto-Discovery WS:** con `AutoDiscovery: true`, alla connessione il nodo invia automaticamente `getSources` all'upstream per scoprire source e tag disponibili (nome + DataKind). Non serve configurare i tag manualmente — basta specificare `Url`. Con `AutoDiscovery: false`, si usa `Tags: ["tag1", "tag2"]` per una lista esplicita.

---

### S4 — Da DataProvider upstream + forward UDP verso DataServer

Il DataService riceve dati via WS e li inoltra anche via UDP (best-effort, bassa latenza) a un DataServer.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5081 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ParquetOutputPath": "./data/parquet"
    },
    "Sources": [
      {
        "Id": "from-provider",
        "Url": "ws://provider:5080/ws",
        "ApiKey": "secret",
        "ForwardIntervalMs": 2000,
        "BatchMode": true,
        "ChunkSync": true
      }
    ],
    "UdpDestinations": [
      {
        "Id": "to-server",
        "Host": "dataserver.local",
        "Port": 9200,
        "Sources": ["linea1"],
        "DownsampleMs": 2000,
        "MaxPacketBytes": 1400
      }
    ]
  }
}
```

**Flusso:**
```
PLC ──► DataProvider ──WS──► DataService ──WS/REST──► Client
                                  │
                                  ├──UDP (live)──► DataServer
                                  └──WS (chunk)──► DataServer
```

---

### S5 — Multi-source (PLC locale + upstream remoto)

Un DataService che gestisce sia un input diretto che un upstream.

```jsonc
{
  "Bridge": {
    "Mode": "DataService",
    "Http": { "Port": 5081 },
    "Buffer": {
      "InMemoryMinutes": 60,
      "ChunkDurationMin": 5,
      "PersistToDisk": true,
      "ParquetOutputPath": "./data/parquet"
    },
    "DataSources": [
      {
        "Id": "locale",
        "Inputs": [
          {
            "Type": "Ads",
            "Host": "192.168.0.10",
            "AmsNetId": "5.23.40.1.1.1",
            "Port": 851,
            "Tags": [
              { "Name": "temperature", "Address": "MAIN.fTemp", "DataKind": "Telemetry", "PollMs": 500 }
            ]
          }
        ]
      }
    ],
    "Sources": [
      {
        "Id": "from-remote",
        "Url": "ws://remote-provider:5080/ws",
        "ApiKey": "remote-key",
        "ForwardIntervalMs": 2000
      }
    ]
  }
}
```

**Flusso:**
```
PLC locale ──ADS──┐
                   ├──► DataService [buffer + Parquet] ──WS/REST──► Client
PLC remoto ──► Provider ──WS──┘
```

---

## DataServer

Il DataServer è lo storicizzatore finale. Si collega come client a uno o più DataService/DataProvider, riceve dati via WS e/o UDP, gestisce buffer ampi e supporta replay. **Salva automaticamente** i chunk ricevuti via chunk transfer come file Parquet in `ParquetArchivePath`. Può anche **caricare** archivi Parquet aggiuntivi (prodotti dal DataService o copiati manualmente) per analisi offline.

### V1 — Da DataService via WebSocket

Configurazione base: il DataServer riceve tutto via WebSocket.

```jsonc
{
  "Bridge": {
    "Mode": "DataServer",
    "Http": { "Port": 5082 },
    "Buffer": {
      "InMemoryMinutes": 300,
      "ChunkDurationMin": 5,
      "ParquetArchivePath": "./data/archives"
    },
    "SelectiveSubscription": true,
    "BackfillMinutes": 10,
    "Sources": [
      {
        "Id": "service-linea1",
        "Url": "wss://dataservice:5081/ws",
        "ApiKey": "service-key",
        "ChunkSync": true,
        "Compression": "brotli",
        "ChunkSyncMaxBandwidthKbps": 500
      }
    ]
  }
}
```

**Flusso:** `DataService ──WS (live + chunk)──► DataServer [buffer 5h] ──WS/REST──► Dashboard`

> **Auto-Discovery WS:** con `AutoDiscovery: true`, alla connessione il DataServer invia `getSources` all'upstream per scoprire automaticamente source e tag. Non serve pre-configurare i tag — il discovery avviene ad ogni (ri)connessione. Per UDP: il sender invia periodicamente pacchetti mapping con le corrispondenze CRC32 → nome, rendendo il canale UDP completamente autosufficiente.

---

### V2 — Da DataService via WS + ricezione live UDP

Doppio canale: WS per affidabilità e chunk transfer, UDP per bassa latenza. Il DataServer deduplica automaticamente via `msgId`.

```jsonc
{
  "Bridge": {
    "Mode": "DataServer",
    "Http": { "Port": 5082 },
    "Buffer": {
      "InMemoryMinutes": 300,
      "ChunkDurationMin": 5,
      "ParquetArchivePath": "./data/archives"
    },
    "SelectiveSubscription": true,
    "BackfillMinutes": 10,
    "Sources": [
      {
        "Id": "service-linea1",
        "Url": "wss://dataservice:5081/ws",
        "ApiKey": "service-key",
        "ChunkSync": true,
        "Compression": "brotli"
      }
    ],
    "UdpReceiver": {
      "ListenPort": 9200,
      "AutoDiscovery": true,
      "Tags": []
    }
  }
}
```

**Flusso:**
```
DataService ──WS (affidabile + chunk)──► DataServer ──WS/REST──► Dashboard
DataService ──UDP (live, best-effort)──┘
```

---

### V3 — Multi-source (più DataService upstream)

Un DataServer centralizzato che aggrega più linee produttive.

```jsonc
{
  "Bridge": {
    "Mode": "DataServer",
    "Http": { "Port": 5082 },
    "Buffer": {
      "InMemoryMinutes": 300,
      "ChunkDurationMin": 5,
      "ParquetArchivePath": "./data/archives"
    },
    "SelectiveSubscription": true,
    "BackfillMinutes": 10,
    "Sources": [
      {
        "Id": "service-linea1",
        "Url": "wss://service-l1:5081/ws",
        "ApiKey": "key-l1",
        "ChunkSync": true,
        "Compression": "brotli"
      },
      {
        "Id": "service-linea2",
        "Url": "wss://service-l2:5081/ws",
        "ApiKey": "key-l2",
        "ChunkSync": true,
        "Compression": "brotli"
      },
      {
        "Id": "service-pressa",
        "Url": "wss://service-pressa:5081/ws",
        "ApiKey": "key-pressa",
        "ChunkSync": true
      }
    ],
    "UdpReceiver": {
      "ListenPort": 9200,
      "AutoDiscovery": true,
      "Tags": []
    }
  }
}
```

**Flusso:**
```
Service L1 ──WS+UDP──┐
Service L2 ──WS+UDP──┼──► DataServer [buffer 5h] ──WS/REST──► Dashboard
Service P  ──WS+UDP──┘
```

---

### V4 — Da DataProvider diretto (senza DataService intermedio)

Per scenari semplici dove non serve persistenza intermedia. Il DataServer si collega direttamente a un DataProvider ma **non avrà chunk transfer né persistenza Parquet**.

```jsonc
{
  "Bridge": {
    "Mode": "DataServer",
    "Http": { "Port": 5082 },
    "Buffer": {
      "InMemoryMinutes": 300,
      "ChunkDurationMin": 5,
      "ParquetArchivePath": "./data/archives"
    },
    "SelectiveSubscription": true,
    "Sources": [
      {
        "Id": "direct-provider",
        "Url": "ws://provider:5080/ws",
        "ApiKey": "provider-key",
        "ChunkSync": false
      }
    ]
  }
}
```

**Flusso:** `PLC ──► DataProvider ──WS──► DataServer [solo buffer in-memory] ──WS/REST──► Dashboard`

> **Nota:** senza un DataService intermedio, non c'è persistenza Parquet né chunk transfer. I dati oltre la finestra di buffer vengono persi.

---

## Auto-Discovery dei tag

Il Bridge supporta tre meccanismi di auto-discovery che eliminano la necessita' di configurare manualmente i tag in `appsettings.json`. Tutti si attivano con `AutoDiscovery: true` nella sezione corrispondente.

### Via UDP Input (input diretto da PLC)

Quando `AutoDiscovery: true` e' impostato su un input UDP (`DataSources[].Inputs[]`), il mittente (simulatore o DataProvider) invia periodicamente un **pacchetto metadata** (flag `0x10`) contenente la struttura completa dei source e tag (nome + DataKind).

| Aspetto | Dettaglio |
|---------|-----------|
| **Frequenza** | Ogni 10 secondi (configurabile nel simulatore) |
| **Formato** | Binario, stesso header del protocollo custom-v1 con flag `0x10` |
| **Contenuto** | Lista di source, ognuno con i suoi tag (nome + kind) |
| **Registrazione** | I nuovi source/tag vengono registrati al volo; quelli gia' esistenti vengono ignorati |
| **Configurazione** | `"AutoDiscovery": true` nell'input UDP — nessun tag da elencare |

### Via UDP Inter-Bridge (DataService → DataServer)

Quando `AutoDiscovery: true` e' impostato su `UdpReceiver`, il **sender** (DataService) invia periodicamente pacchetti **mapping** (flag `0x20`) con le corrispondenze CRC32 → nome per source e tag. Il canale UDP e' completamente autosufficiente — non richiede WS o REST per la risoluzione dei nomi.

| Aspetto | Dettaglio |
|---------|-----------|
| **Frequenza** | Ogni 10 secondi (automatico) |
| **Formato** | Binario, header con `FlagMapping = 0x20` + payload con `packetIdx/packetTotal` |
| **Contenuto** | Nome source + lista tag (CRC32 + nome in chiaro) |
| **Multi-pacchetto** | Se i tag sono troppi per un pacchetto, vengono divisi in piu' pacchetti con `packetIdx/packetTotal` |
| **Registrazione** | Il receiver colleziona tutti i pacchetti, li ordina per `packetIdx`, registra source e tag |
| **Configurazione** | `"AutoDiscovery": true` nel `UdpReceiver` — nessun tag da elencare |

### Via WebSocket (upstream)

Quando `AutoDiscovery: true` e' impostato su `Sources[]`, alla connessione il nodo invia automaticamente **`getSources`** per ottenere la lista dei source e tag esposti dall'upstream.

| Aspetto | Dettaglio |
|---------|-----------|
| **Quando** | Ad ogni connessione e riconnessione |
| **Formato** | JSON via WebSocket (`op: "getSources"`) |
| **Risposta** | Lista di source con tag (nome + DataKind) e info buffer |
| **Registrazione** | I tag scoperti vengono registrati nel source locale |
| **Configurazione** | `"AutoDiscovery": true` in `Sources[]` |

Con `AutoDiscovery: false`, si specifica `Tags: ["tag1", "tag2"]` per una lista esplicita di tag da sottoscrivere.

### Confronto

| | UDP Input Metadata | UDP Inter-Bridge Mapping | WS getSources |
|---|---|---|---|
| **Usato da** | Input diretto da PLC | DataServer (riceve da DataService) | DataService/DataServer collegati a upstream |
| **Richiede config** | `AutoDiscovery: true` su input | `AutoDiscovery: true` su UdpReceiver | `AutoDiscovery: true` su Sources |
| **Direzione** | Push (mittente invia periodicamente) | Push (sender invia periodicamente) | Pull (il client chiede alla connessione) |
| **Formato** | Binario flag `0x10` | Binario flag `0x20` + `packetIdx/packetTotal` | JSON `op: "getSources"` |
| **Autosufficiente** | Si' | Si' (niente WS/REST necessario) | Si' |

---

## Topologie di esempio

### Topologia 1 — Singola linea completa (3 nodi)

```
PLC ──ADS──► DataProvider ──WS──► DataService ──WS+UDP──► DataServer ──► Dashboard
               :5080                 :5081                   :5082
```

### Topologia 2 — Multi-linea con server centralizzato

```
PLC L1 ──► Provider L1 ──► Service L1 ──┐
PLC L2 ──► Provider L2 ──► Service L2 ──┼──WS+UDP──► DataServer ──► Dashboard
PLC L3 ──► Provider L3 ──► Service L3 ──┘               :5082
```

### Topologia 3 — DataService diretto (2 nodi, senza Provider)

```
PLC ──ADS──► DataService [buffer + Parquet] ──WS──► DataServer ──► Dashboard
                 :5080                                  :5082
```

### Topologia 4 — Minimale (solo Provider + Client)

```
PLC ──ADS──► DataProvider ──WS/REST──► Client
                :5080
```

---

## Riepilogo funzionalità per modo

| Funzionalità                  | DataProvider | DataService | DataServer |
|-------------------------------|:---:|:---:|:---:|
| Input diretto (ADS/UDP/Mock)  | ✅  | ✅  | ❌  |
| Buffer in memoria             | ❌  | ✅  | ✅  |
| Persistenza Parquet (wide)    | ❌  | ✅  | ❌  |
| Caricamento archivi           | ❌  | ❌  | ✅  |
| Client WS upstream            | ❌  | ✅  | ✅  |
| Invio UDP stream              | ❌  | ✅  | ❌  |
| Ricezione UDP stream          | ❌  | ❌  | ✅  |
| Chunk transfer (invio)        | ❌  | ✅  | ❌  |
| Chunk transfer (ricezione)    | ❌  | ❌  | ✅  |
| Replay storico                | ❌  | ❌  | ✅  |
| Selective subscription        | ❌  | ❌  | ✅  |
| Downsampling                  | ❌  | ✅  | ✅  |
| Query temporali               | ❌  | ✅  | ✅  |
| REST API                      | ✅  | ✅  | ✅  |
| WebSocket API                 | ✅  | ✅  | ✅  |
| Gestione allarmi              | ✅  | ✅  | ✅  |
| Comandi relay                 | ✅  | ✅  | ✅  |
| Export CSV/Parquet             | ❌  | ✅  | ✅  |
