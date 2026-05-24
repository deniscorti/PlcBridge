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
| GET    | `/plcs/{id}/tags/{name}/history`    | Storico dal buffer (query: `?minutes=5&type=telemetry`) |
| GET    | `/plcs/{id}/events`                 | Ultimi eventi (filtrabili per range temporale) |
| GET    | `/plcs/{id}/alarms`                 | Ultimi allarmi (filtrabili per range temporale) |

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
{ "op": "hisotryData",   "tags": ["plc1.temperature", "plc1.pressure"] }
```

### Messaggi server → client
```json
{ "type": "telemetry", "tag": "plc1.temperature", "value": 23.4, "ts": "..." }
{ "type": "telemetry", "tag": "plc1.vibration", "value": [0.1, 0.3, 0.2], "ts": "..." }
{ "type": "event",  "tag": "plc1.startButton", "value": true, "ts": "..." }
{ "type": "alarm",  "tag": "plc1.overtemp", "active": true, "ts": "..." }
{ "type": "connection", "plcId": "plc1", "state": "down" }
{ "type": "ack",    "op": "write", "tag": "plc1.setpoint", "ok": true }
{ "type": "history", "tag": "plc1.temperature", "values": [...], "from": "...", "to": "..." }
{ "type": "error",  "code": "TAG_NOT_FOUND", "message": "..." }
```
