# API

## REST (HTTP)

Base URL: `http://<host>:<port>/api`

| Metodo | Path                                | Descrizione                       |
|--------|-------------------------------------|-----------------------------------|
| GET    | `/health`                           | Health check                      |
| GET    | `/plcs`                             | Elenco PLC configurati            |
| GET    | `/plcs/{id}`                        | Dettaglio PLC + stato connessione |
| GET    | `/plcs/{id}/tags`                   | Elenco tag                        |
| GET    | `/plcs/{id}/tags/{name}`            | Lettura valore corrente           |
| POST   | `/plcs/{id}/tags/{name}`            | Scrittura valore (`{ "value": ... }`) |

### Autenticazione
Header `X-Api-Key: <key>` su tutte le rotte tranne `/health`.

## WebSocket

Endpoint: `ws://<host>:<port>/ws?token=<jwt>`

### Messaggi client → server
```json
{ "op": "subscribe",   "tags": ["plc1.temperature", "plc1.pressure"] }
{ "op": "unsubscribe", "tags": ["plc1.temperature"] }
{ "op": "write",       "tag": "plc1.setpoint", "value": 42.0 }
{ "op": "read",        "tag": "plc1.temperature" }
```

### Messaggi server → client
```json
{ "type": "value",  "tag": "plc1.temperature", "value": 23.4, "ts": "..." }
{ "type": "event",  "kind": "connection", "plcId": "plc1", "state": "down" }
{ "type": "ack",    "op": "write", "tag": "plc1.setpoint", "ok": true }
{ "type": "error",  "code": "TAG_NOT_FOUND", "message": "..." }
```
