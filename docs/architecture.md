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

## Tipologie di dati

Il sistema gestisce tre categorie distinte di dati provenienti dal PLC:

| Tipo | Descrizione | Strategia |
|------|-------------|-----------|
| **Telemetria** | Valori numerici campionati (sensori, misure). Possono arrivare come singolo valore o array. | Polling/push continuo, buffer circolare in memoria, persistenza su disco. |
| **Eventi** | Cambi di stato discreti (bottone premuto, fine ciclo). Non ha senso trasmettere continuamente 0/1. | Notifica solo on-change, salvataggio con timestamp. |
| **Allarmi** | Simili a eventi ma legati a condizioni anomale o campi specifici del PLC. | Notifica immediata, log dedicato, separazione dallo stream telemetria. |

I dati vengono mantenuti separati sia nel buffer in memoria che nella persistenza su disco.

## Buffer in memoria e persistenza

- **Ring buffer in-memory**: mantiene gli ultimi N minuti (configurabile) per ogni categoria. Consente query rapide su dati recenti via REST/WS (es. `historyData`).
- **Persistenza su disco**: i dati vengono scritti periodicamente su file (o DB locale) per consentire ricostruzione storica in caso di riavvio o necessità di analisi post-mortem.

## Driver supportati

| Driver | Protocollo | Note |
|--------|-----------|------|
| **ADS (Beckhoff)** | ADS/AMS TCP | Lettura/scrittura variabili, notifiche on-change native. |
| **UDP Receiver** | UDP custom | Il PLC invia pacchetti UDP con stato/valori dei segnali secondo un protocollo da definire. PlcBridge ascolta e decodifica lo stream. |

L'architettura è estensibile: ogni driver implementa `IPlcDriver`.

## Componenti cross-cutting
- **Configuration**: `appsettings.json` + env vars + `IOptionsMonitor`.
- **Logging**: Serilog (console + file JSON).
- **Telemetry**: OpenTelemetry (metrics + tracing).
- **Auth**: API key middleware su REST; handshake token su WS.
