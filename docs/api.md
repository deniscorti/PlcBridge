# API

## Autenticazione

Autenticazione unificata per REST e WS:
- **REST**: header `Authorization: Bearer <token>` oppure `X-Api-Key: <key>`
- **WebSocket**: query string `?token=<token>` oppure `?apiKey=<key>`
- `/health` è esente da autenticazione.

---

## REST (HTTP)

Base URL: `http://<host>:<port>/api`

### Risorse e DataSource

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| GET    | `/health`                                     | Health check + mode + uptime      | Tutte |
| GET    | `/sources`                                    | Elenco DataSource disponibili     | Tutte |
| GET    | `/sources/{source}/tags`                      | Elenco tag nel DataSource         | Tutte |
| GET    | `/sources/{source}/tags/{name}`               | Lettura valore corrente           | Tutte |
| POST   | `/sources/{source}/tags/{name}`               | Scrittura valore. Body: `{ "value": ... }` | Tutte |

### Query per intervallo temporale

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| GET    | `/sources/{source}/telemetry`                 | Dati telemetria (`?tags=t1,t2&from=...&to=...&limit=1000`) | DataService, DataServer |
| GET    | `/sources/{source}/events`                    | Eventi (`?tags=t1,t2&from=...&to=...&limit=1000`) | DataService, DataServer |
| GET    | `/sources/{source}/alarms`                    | Allarmi (`?from=...&to=...&active=true&acknowledged=false&limit=1000`) | DataService, DataServer |

### Allarmi

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| POST   | `/sources/{source}/alarms/{tag}/ack`          | Riconosce un allarme              | Tutte |
| POST   | `/sources/{source}/alarms/ack`                | Riconosce tutti gli allarmi del source. Body: `{ "tags": ["t1","t2"] }` o `{ "tags": ["ALL"] }` | Tutte |

### Comandi

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| POST   | `/bridge/command`                             | Comanda il bridge: `start` / `stop` / `archive` / `clear` / `setChunkDuration`. Body: `{ "command": "...", "params": {...} }` | Tutte |
| POST   | `/sources/{source}/command`                   | Comando verso sorgente (relay nella catena). Body: `{ "command": "...", "params": {...} }` | Tutte |
| GET    | `/bridge/settings`                            | Restituisce parametri runtime modificabili (chunkDurationMin, ecc.) | DataService, DataServer |

### Archivi e export

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| GET    | `/archives`                                   | Elenco archivi Parquet (`?source=linea1`) | DataService, DataServer |
| POST   | `/archives/load`                              | Carica archivio in memoria. Body: `{ "source": "...", "file": "..." }` | DataServer |
| GET    | `/sources/{source}/export`                    | Export dati come file scaricabile. Query: `?tags=t1,t2&from=...&to=...&kind=telemetry&format=csv` | DataService, DataServer |
| GET    | `/sources/{source}/chunks`                    | Elenco chunk con stato (`?quality=live&quality=full`) | DataService, DataServer |
| GET    | `/sources/{source}/chunks/{chunkId}/download` | Download chunk come file Parquet  | DataService, DataServer |
| GET    | `/sources/{source}/chunks/{chunkId}`          | Dettaglio chunk (metadati, qualità, record count) | DataService, DataServer |

### Metriche e stato

| Metodo | Path                                          | Descrizione                       | Modalità |
|--------|-----------------------------------------------|-----------------------------------|----------|
| GET    | `/status`                                     | Stato completo: mode, sources, buffer, connessioni | Tutte |
| GET    | `/metrics`                                    | Metriche operative in JSON        | Tutte |

#### Esempio risposta `/status`
```jsonc
{
  "mode": "DataServer",
  "uptime": 3600,
  "sources": [
    { "id": "linea1", "connected": true,
      "buffer": {
        "oldestTs": "2026-05-24T09:30:00Z",
        "newestTs": "2026-05-24T14:30:00Z",
        "chunkCount": 58,
        "totalRecords": 450000,
        "oldestMsgId": 98000,
        "newestMsgId": 103000
      }
    }
  ]
}
```

#### Esempio risposta `/metrics`
```jsonc
{
  "uptime": 3600,
  "mode": "DataServer",
  "sources": {
    "linea1": {
      "connected": true,
      "rttMs": 45,
      "messagesPerSec": 250,
      "lastMsgId": 103000
    }
  },
  "buffer": { "totalChunks": 58, "totalRecords": 450000, "usedMb": 120,
    "chunksLive": 3, "chunksFull": 52, "chunksLoaded": 3 },
  "chunkSync": { "pendingTransfers": 1, "completedTransfers": 52, "avgTransferMs": 1200 },
  "clients": { "wsConnections": 12, "activeSubscriptions": 340 },
  "disk": { "parquetFiles": 24, "totalSizeMb": 850, "lastFlush": "2026-05-24T14:25:00Z" }
}
```

---

## WebSocket

Endpoint: `ws://<host>:<port>/ws?token=<token>` oppure `ws://<host>:<port>/ws?apiKey=<key>`

Ogni messaggio client→server porta un campo `id` (opzionale) per correlare la risposta.

### Messaggi client → server

```jsonc
// === Sottoscrizioni ===
{ "op": "subscribe",   "id": "s1", "source": "linea1", "tags": ["temperature", "pressure"] }
{ "op": "subscribe",   "id": "s2", "source": "linea1", "tags": ["ALL"] }           // tutto il DataSource
{ "op": "subscribe",   "id": "s3", "tags": ["ALL"] }                                // tutti i DataSource
{ "op": "subscribe",   "id": "s4", "source": "linea1", "kinds": ["alarm"] }         // tutti gli allarmi di un source
{ "op": "subscribe",   "id": "s5", "kinds": ["alarm"] }                              // tutti gli allarmi di tutti i source
{ "op": "subscribe",   "id": "s6", "source": "linea1", "tags": ["ALL"], "since": 100230 }  // con recovery (inter-bridge)
{ "op": "subscribe",   "id": "s7", "source": "linea1", "tags": ["ALL"], "downsampleMs": 2000 }  // telemetria a 2s
{ "op": "subscribe",   "id": "s8", "source": "linea1", "tags": ["ALL"],
  "downsampleMs": 2000, "compression": "brotli", "batchMode": true }                // inter-bridge ottimizzato

// === Compact push mode (risparmio banda fino a 13×) ===
{ "op": "subscribe",   "id": "s9", "source": "linea1",
  "tags": ["temp1", "temp2", "press1"], "compact": true }                           // abilita push compatto posizionale

{ "op": "unsubscribe", "id": "u1", "source": "linea1", "tags": ["temperature"] }
{ "op": "unsubscribe", "id": "u2", "source": "linea1", "tags": ["ALL"] }
{ "op": "unsubscribe", "id": "u3" }                                                  // tutto

// === Lettura/Scrittura diretta ===
{ "op": "read",  "id": "r1", "source": "linea1", "tag": "temperature" }
{ "op": "write", "id": "w1", "source": "linea1", "tag": "setpoint", "value": 42.0 }

// === Query per intervallo temporale ===
{ "op": "queryTelemetry", "id": "qt1", "source": "linea1", "tags": ["temperature", "pressure"], "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T11:00:00Z", "limit": 1000 }
{ "op": "queryTelemetry", "id": "qt2", "source": "linea1", "tags": ["ALL"], "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T10:05:00Z" }
{ "op": "queryEvents",    "id": "qe1", "source": "linea1", "tags": ["startButton"], "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T11:00:00Z" }
{ "op": "queryEvents",    "id": "qe2", "source": "linea1", "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T11:00:00Z" }
{ "op": "queryAlarms",    "id": "qa1", "source": "linea1", "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T11:00:00Z" }
{ "op": "queryAlarms",    "id": "qa2", "source": "linea1", "active": true, "acknowledged": false }
{ "op": "archives",       "id": "a1",  "source": "linea1" }

// === Allarmi: acknowledge ===
{ "op": "ackAlarm", "id": "ak1", "source": "linea1", "tag": "overtemp" }
{ "op": "ackAlarm", "id": "ak2", "source": "linea1", "tags": ["ALL"] }

// === Comandi bridge ===
{ "op": "bridgeCommand", "id": "bc1", "command": "start" }
{ "op": "bridgeCommand", "id": "bc2", "command": "archive", "params": { "source": "linea1" } }
{ "op": "bridgeCommand", "id": "bc3", "command": "setChunkDuration", "params": { "minutes": 2 } }
{ "op": "bridgeCommand", "id": "bc4", "command": "getSettings" }

// === Comandi sorgente (relay nella catena) ===
{ "op": "sourceCommand", "id": "sc1", "source": "linea1", "command": "reset", "params": { "axis": 1 } }

// === Replay (solo DataServer) ===
{ "op": "replay", "id": "rp1", "source": "linea1", "from": "2026-05-24T10:00:00Z", "to": "2026-05-24T10:30:00Z", "speed": 10 }
{ "op": "replayStop", "id": "rp2" }

// === Info ===
{ "op": "getSources", "id": "gs1" }
{ "op": "getStatus",  "id": "st1" }
{ "op": "ping" }
```

### Messaggi server → client

```jsonc
// === Dati push (dopo subscribe) ===
{ "type": "telemetry", "source": "linea1", "tag": "temperature", "value": 23.4, "ts": "2026-05-24T10:30:01.123Z", "msgId": 100234 }
{ "type": "telemetry", "source": "linea1", "tag": "vibration", "value": [0.1, 0.3, 0.2], "ts": "...", "msgId": 100235 }
{ "type": "event",     "source": "linea1", "tag": "startButton", "value": true, "ts": "...", "msgId": 100236 }
{ "type": "alarm",     "source": "linea1", "tag": "overtemp", "active": true, "acknowledged": false, "severity": "critical", "ts": "...", "msgId": 100237 }
{ "type": "alarm",     "source": "linea1", "tag": "overtemp", "active": true, "acknowledged": true, "ts": "..." }  // dopo ack
{ "type": "alarm",     "source": "linea1", "tag": "overtemp", "active": false, "acknowledged": false, "ts": "..." }  // rientrato

// === Compact push mode ===
// Risposta al subscribe con compact:true — il layout definisce l'ordine posizionale dei tag
{ "type": "response", "id": "s9", "ok": true, "compact": true,
  "layout": { "source": "linea1", "layoutId": 1, "tags": ["temp1", "temp2", "press1"] } }

// Push batch compatto (ogni ~50ms) — v[i] corrisponde a tags[i] nel layout, null = nessun aggiornamento
{ "type": "d", "l": 1, "ts": "2026-05-24T10:30:01.123Z", "m": 100234, "v": [23.5, null, 1.02] }
{ "type": "d", "l": 1, "ts": "2026-05-24T10:30:01.173Z", "m": 100237, "v": [23.6, 18.2, null] }

// Layout update (inviato quando il client aggiunge/toglie tag dalla sottoscrizione)
{ "type": "layoutUpdate", "source": "linea1", "layoutId": 2, "tags": ["temp1", "press1", "flow1"] }

// === Batch compresso (inter-bridge) ===
// Inviato come WS binary frame; qui mostrato pre-compressione:
{ "type": "telemetryBatch", "source": "linea1", "compressed": "brotli", "count": 45,
  "samples": [
    { "tag": "temperature", "v": 23.4, "ts": "...", "msgId": 100234 },
    { "tag": "pressure",    "v": 1.02, "ts": "...", "msgId": 100235 }
  ]
}

// === Risposte correlate via "id" ===
{ "type": "response", "id": "s1", "ok": true }
{ "type": "response", "id": "r1", "ok": true, "value": 23.4, "ts": "..." }
{ "type": "response", "id": "w1", "ok": true }
{ "type": "response", "id": "w1", "ok": false, "error": "WRITE_DENIED", "message": "Tag is read-only" }
{ "type": "response", "id": "ak1", "ok": true }

// === Risposte comandi bridge ===
{ "type": "response", "id": "bc3", "ok": true, "applied": { "chunkDurationMin": 2 }, "note": "Effective from next chunk" }
{ "type": "response", "id": "bc4", "ok": true, "settings": {
    "chunkDurationMin": 2,
    "inMemoryMinutes": 60,
    "persistToDisk": true,
    "udpDestinations": [{ "id": "server-main", "enabled": true }, { "id": "server-backup", "enabled": false }]
}}

// === Risposte query temporali ===
// quality: "full" (dati completi), "live" (downsampliati), "mixed" (alcuni chunk full, altri live), "loaded" (da archivio)
{ "type": "response", "id": "qt1", "ok": true, "kind": "telemetry", "quality": "full", "data": [
    { "tag": "temperature", "values": [{ "v": 23.1, "ts": "..." }, { "v": 23.4, "ts": "..." }] },
    { "tag": "pressure",    "values": [{ "v": 1.01, "ts": "..." }, { "v": 1.02, "ts": "..." }] }
], "from": "...", "to": "...", "truncated": false }

{ "type": "response", "id": "qe1", "ok": true, "kind": "event", "quality": "full", "data": [
    { "tag": "startButton", "events": [{ "value": true, "ts": "..." }, { "value": false, "ts": "..." }] }
], "from": "...", "to": "...", "truncated": false }

{ "type": "response", "id": "qa1", "ok": true, "kind": "alarm", "quality": "full", "data": [
    { "tag": "overtemp", "alarms": [{ "active": true, "acknowledged": false, "severity": "critical", "ts": "..." }, { "active": false, "ts": "..." }] }
], "from": "...", "to": "...", "truncated": false }

// Dati downsampliati (chunk full non ancora ricevuto dal DataService)
{ "type": "response", "id": "qt1", "ok": true, "kind": "telemetry", "quality": "live", "data": [...],
  "from": "...", "to": "..." }

// Se il range richiesto eccede il buffer disponibile
{ "type": "response", "id": "qt1", "ok": true, "kind": "telemetry", "quality": "mixed", "data": [...],
  "from": "...", "to": "...", "truncated": true, "availableFrom": "2026-05-24T10:15:00Z" }

// === Archivi ===
{ "type": "response", "id": "a1", "ok": true, "archives": [
    { "file": "linea1_2026-05-24_10-00_100000.parquet", "from": "...", "to": "...", "size": 1048576 }
]}

// === Risultato comandi sorgente ===
{ "type": "response", "id": "sc1", "ok": true, "result": { "status": "done" } }
{ "type": "response", "id": "sc1", "ok": false, "error": "timeout", "segment": "DataService→DataProvider" }

// === Info sources e status ===
{ "type": "response", "id": "gs1", "ok": true, "sources": [
    { "id": "linea1", "tags": ["temperature", "pressure", "startButton", "overtemp"],
      "buffer": { "oldestTs": "2026-05-24T09:30:00Z", "newestTs": "2026-05-24T10:30:00Z",
                  "chunkCount": 12, "totalRecords": 45000, "oldestMsgId": 98000, "newestMsgId": 103000 }
    }
]}
{ "type": "response", "id": "st1", "ok": true, "mode": "DataServer", "uptime": 3600,
  "sources": { "linea1": { "connected": true, "rttMs": 45 } },
  "buffer": { "linea1": { "usedMb": 120, "records": 500000 } },
  "clients": { "wsConnections": 12, "activeSubscriptions": 340 } }

// === Stato connessione (push, non richiesto) ===
{ "type": "connection", "source": "linea1", "state": "connected" }
{ "type": "connection", "source": "linea1", "state": "disconnected", "reason": "timeout" }

// === Replay ===
{ "type": "telemetry", "source": "linea1", "tag": "temperature", "value": 23.4, "ts": "2026-05-24T10:00:00.500Z", "msgId": 100000, "replay": true }
{ "type": "event",     "source": "linea1", "tag": "startButton", "value": true, "ts": "2026-05-24T10:00:01.200Z", "msgId": 100001, "replay": true }
{ "type": "alarm",     "source": "linea1", "tag": "overtemp", "active": true, "acknowledged": false, "ts": "2026-05-24T10:00:03.000Z", "msgId": 100002, "replay": true }
{ "type": "replayEnd", "id": "rp1" }

// === Chunk transfer inter-bridge ===
// DataService notifica chunk disponibile
{ "type": "chunkReady", "source": "linea1", "chunkId": "c-20260524-1000",
  "fromTs": "2026-05-24T10:00:00Z", "toTs": "2026-05-24T10:05:00Z",
  "firstMsgId": 100000, "lastMsgId": 100120, "sizeBytes": 52400, "records": 120 }

// DataServer richiede chunk (background o prioritario)
{ "op": "chunkRequest", "id": "cr1", "source": "linea1", "chunkId": "c-20260524-1000" }

// DataService trasferisce chunk compresso (WS binary frame tra start e end)
{ "type": "chunkTransferStart", "source": "linea1", "chunkId": "c-20260524-1000",
  "compressed": "brotli", "sizeCompressed": 8200, "sizeOriginal": 52400 }
// ...binary frame(s) con dati compressi...
{ "type": "chunkTransferEnd", "source": "linea1", "chunkId": "c-20260524-1000", "ok": true }

// === Recovery inter-bridge (per gap nello stream live) ===
{ "type": "recoveryStart", "source": "linea1", "fromMsgId": 100230, "toMsgId": 100234 }
// ...sequenza di messaggi telemetry/event/alarm normali...
{ "type": "recoveryEnd", "source": "linea1", "lastMsgId": 100234 }
{ "type": "recoveryFailed", "source": "linea1", "requestedFrom": 99000, "availableFrom": 100000, "reason": "buffer_rotated" }

// === Pong e errori ===
{ "type": "pong" }
{ "type": "error", "id": "r1", "code": "TAG_NOT_FOUND", "message": "Tag 'foo' not found in source 'linea1'" }
{ "type": "error", "code": "AUTH_FAILED", "message": "Invalid token" }
{ "type": "error", "code": "RATE_LIMIT", "message": "Max queries per minute exceeded" }
```
