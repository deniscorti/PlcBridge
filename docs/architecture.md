# Architettura

## Vista a livelli

```
+-----------------------------------------------+
| Bridge.DataProvider / DataService / DataServer|
|  - Program.cs (entry point per modalità)      |
|  - appsettings.json (config completa)         |
+----------------------+------------------------+
                       |
+----------------------v------------------------+
|              Bridge.Host (library)            |
|  - HTTP endpoints (Minimal API)               |
|  - WebSocket endpoint + hub                   |
|  - Auth, middleware, configuration classes     |
+----------------------+------------------------+
                       |
+----------------------v------------------------+
|              Bridge.Core                      |
|  - Modello di dominio (DataSource, Tag, Value)|
|  - Interfacce: IDataInput, ITagCache,         |
|    ISubscriptionBroker, IChunkStore           |
|  - Servizi applicativi                        |
+----------------------+------------------------+
                       |
+----------------------v------------------------+
|              Bridge.Inputs                    |
|  - AdsInput: lettura/scrittura via ADS/AMS    |
|  - UdpInput: ricezione pacchetti UDP custom   |
|  (coesistono nella stessa istanza)            |
+-----------------------------------------------+

Bridge.Contracts: DTO condivisi tra Host e client (HTTP/WS)
Bridge.InterBridge: logica connessione tra nodi (WS client, UDP sender/receiver, chunk transfer)
Bridge.Storage: persistenza Parquet, export CSV
```

### Struttura progetti

```
Bridge.sln
├── src/
│   ├── Bridge.Core/                 // Dominio + interfacce + servizi
│   │   ├── Model/                   // DataSource, Tag, TagValue, AlarmState, DataKind
│   │   ├── Abstractions/            // IDataInput, IChunkStore, ISubscriptionBroker, ICommandRelay
│   │   ├── Buffer/                  // Chunk, ChunkedRingBuffer, ChunkMetadata
│   │   └── Services/                // SourceManager, QueryService, AlarmService
│   │
│   ├── Bridge.Inputs/               // Input concreti
│   │   ├── Ads/                     // AdsInput : IDataInput
│   │   ├── Udp/                     // UdpInput : IDataInput (ricezione da PLC)
│   │   └── Mock/                    // MockInput : IDataInput (test senza PLC)
│   │
│   ├── Bridge.Contracts/            // DTO condivisi REST/WS
│   │   ├── Rest/                    // Request/Response REST
│   │   └── Ws/                      // Messaggi WS (op + type)
│   │
│   ├── Bridge.InterBridge/          // Comunicazione tra nodi
│   │   ├── WsClient/               // Client WS verso altro bridge
│   │   ├── UdpSender/              // Sender UDP inter-bridge
│   │   ├── UdpReceiver/            // Receiver UDP inter-bridge
│   │   ├── ChunkTransfer/          // Chunk sync background
│   │   └── Protocol/               // Protocollo binario UDP, serializzazione
│   │
│   ├── Bridge.Storage/              // Persistenza
│   │   └── Parquet/                 // Lettura/scrittura Parquet, export CSV
│   │
│   ├── Bridge.Host/                 // Libreria condivisa (endpoints, WS, auth, config)
│   │   ├── Endpoints/               // REST Minimal API
│   │   ├── WebSockets/              // WS handler
│   │   ├── Auth/                    // Middleware auth
│   │   └── Configuration/           // BridgeOptions e classi config
│   │
│   ├── Bridge.DataProvider/         // Exe: sorgente dati (PLC → WS/REST)
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   ├── Bridge.DataService/          // Exe: buffer + persistenza Parquet
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   └── Bridge.DataServer/           // Exe: aggregatore, analisi offline
│       ├── Program.cs
│       └── appsettings.json
│
├── tools/
│   ├── Bridge.Tools.UdpSimulator/   // Simulatore pacchetti UDP (test senza PLC)
│   └── Bridge.Tools.WebClient/      // Dashboard HTML statica per debug REST/WS
│
├── tests/
│   └── Bridge.Core.Tests/           // 13 unit test xUnit (DataSource, SubscriptionBroker, MockInput)
│
└── clients/
    └── web/                          // Client Vue 3 + uPlot (produzione)
        ├── src/composables/          // useBridgeWs, useSubscription, useTimeSeriesBuffer, useQuery
        ├── src/stores/               // connectionStore (Pinia)
        ├── src/components/           // UPlotChart, TagSelector, SourceCard, layout
        ├── src/pages/                // Dashboard, Source, Query, Archives, Settings
        └── src/lib/                  // CompactLayoutTracker (decode compact push)
```

### Librerie esterne

| Necessità | Libreria | Note |
|-----------|----------|------|
| Parquet read/write | **Parquet.Net** | Leggero, API semplice, sufficiente per il caso d'uso |
| ADS Beckhoff | **Beckhoff.TwinCAT.Ads** | NuGet ufficiale Beckhoff |
| Brotli | **System.IO.Compression** | Built-in .NET 8, zero dipendenze |
| JSON | **System.Text.Json** | Built-in, source generators per performance |
| JWT | **Microsoft.AspNetCore.Authentication.JwtBearer** | Built-in ASP.NET Core |

## Concetto di Source (DataSource)

La **Source** (classe `DataSource` nel codice) e' l'unita' logica fondamentale del sistema. Ogni source:
- Ha un nome univoco (es. `"linea1"`, `"pressa-nord"`)
- Raggruppa un insieme di tag (telemetria, eventi, allarmi)
- Ha una sorgente dati configurata (ADS, UDP, o connessione WS ad altro bridge)
- I nomi dei tag devono essere univoci all'interno della source
- Lo stesso nome di tag puo' esistere in source diverse

Ogni modalita' operativa (DataProvider, DataService, DataServer) lavora con una o piu' source. Il DataProvider le definisce in `DataSources[]` con i relativi input. DataService e DataServer le scoprono automaticamente dall'upstream con `AutoDiscovery: true`, oppure le specificano in `Tags[]` con `AutoDiscovery: false`.

## Flusso lettura tag (REST)

`GET /api/sources/{source}/tags/{name}`

Il comportamento varia in base alla modalità:

| Modalità | Flusso |
|----------|--------|
| **DataProvider** | Legge direttamente dal driver (ADS read / ultimo valore UDP ricevuto). Nessun buffer. |
| **DataService** | Legge dal chunk attivo in memoria (ultimo valore noto). Se non disponibile, legge dal driver. |
| **DataServer** | Legge dal chunk attivo in memoria (ultimo valore ricevuto dalla sorgente). |

## Flusso sottoscrizione (WebSocket)
1. Client apre `ws://host/ws`.
2. Invia `{ "op": "subscribe", "source": "linea1", "tags": ["temperature", "pressure"] }`.
   - Se `source` omesso → sottoscrizione a tutte le source disponibili.
   - Se `tags: ["ALL"]` → tutti i tag della source.
3. `SubscriptionBroker` registra la sessione.
4. Su variazione tag, broker pubblica messaggio push al client.

## Tipologie di dati

Il sistema gestisce tre categorie distinte di dati:

| Tipo | Descrizione | Strategia |
|------|-------------|-----------|
| **Telemetria** | Valori numerici campionati (sensori, misure). Possono arrivare come singolo valore o array. | Polling/push continuo, buffer circolare in memoria, persistenza su disco. |
| **Eventi** | Cambi di stato discreti (bottone premuto, fine ciclo). Non ha senso trasmettere continuamente 0/1. | Notifica solo on-change, salvataggio con timestamp. |
| **Allarmi** | Simili a eventi ma legati a condizioni anomale. Gestione a 4 stati (ISA-18.2). | Notifica immediata, acknowledge operatore, log dedicato. |

I dati vengono mantenuti separati sia nel buffer in memoria (chunk separati per tipo) che nella persistenza su disco.

## Gestione allarmi (ISA-18.2)

Gli allarmi seguono un modello a 4 quadranti:

| Stato | active | acknowledged | Descrizione |
|-------|--------|-------------|-------------|
| **Nuovo** | true | false | Allarme scattato, nessuno lo ha visto |
| **Riconosciuto** | true | true | Allarme attivo, operatore consapevole |
| **Rientrato non ack** | false | false | Condizione rientrata, ma non ancora riconosciuto |
| **Risolto** | false | true | Rientrato e riconosciuto → rimosso dalla lista attivi |

```
     scatta               rientra
  ──────────► ATTIVO ──────────────► RIENTRATO
              (non ack)              (non ack)
                 │                      │
            ack  │                 ack  │
                 ▼                      ▼
              ATTIVO                 RISOLTO
              (ack)               (rimosso)
```

L'acknowledge può essere inviato dal client via WS (`ackAlarm`) o REST. L'ack viene inoltrato nella catena come un comando.

## Modalità operative (RF-16)

Il bridge supporta tre configurazioni distinte, combinabili in una topologia a cascata.
Ogni nodo è contemporaneamente un **server** (espone WS/REST su `Http.Port`) e opzionalmente un **client** (si collega via WS a un nodo upstream tramite `Sources[].Url`). Ogni nodo deve avere una **porta diversa** (`Http.Port`) per evitare conflitti di bind.

```
DataProvider :5080  ← solo server (legge da PLC)
DataService  :5081  ← server + client WS verso :5080
DataServer   :5082  ← server + client WS verso :5081
```

Il consumatore a valle si connette come client WS e si sottoscrive.

```
  ADS/UDP          Bridge-1              Bridge-2                       Bridge-3             Client
 ─────────►  [DataProvider]  ◄──sub──  [DataService]  ──UDP live──►  [DataServer]  ◄──sub── REST/WS
              server WS/REST      server WS/REST   ◄──sub WS──  server WS/REST
                                                    ──chunk WS─►
              ◄─── comandi (WS) ──────────────────────────────────────────────────── comandi ───
```

Il DataService può inviare dati al DataServer tramite **due canali paralleli**:
- **UDP**: stream live best-effort, bassa latenza, per dashboard real-time
- **WS**: comandi, query, chunk transfer, recovery — canale affidabile

Entrambi possono essere attivi contemporaneamente. Il DataServer deduplica i messaggi tramite `msgId`: se un dato arriva prima via UDP e poi via WS (o viceversa), il duplicato viene scartato.

Le connessioni WS inter-bridge usano lo stesso protocollo dei client, con l'aggiunta di messaggi specifici (recovery, heartbeat, chunk transfer). Con `AutoDiscovery: true` il nodo scopre automaticamente tutti i tag dall'upstream; con `AutoDiscovery: false` si specificano i tag esplicitamente nell'array `Tags`.

| Modalità | Ruolo | Caratteristiche |
|----------|-------|-----------------|
| **DataProvider** | Sorgente dati pura | Legge da ADS e/o riceve UDP (coesistono). Espone via WS/REST senza caching ne' persistenza. Esegue comandi ricevuti e ritorna la risposta. E' solo server — non si connette a nessuno. Ogni source e' definita in `DataSources[]` con i relativi input (ADS, UDP o entrambi). |
| **DataService** | Bridge intermedio | Si connette come client WS al DataProvider (o legge direttamente via ADS/UDP). Buffer circolare in memoria (configurabile, es. 1h) + salvataggio incrementale su file Parquet a frequenza piena. Espone come server WS/REST — il DataServer (o client finali) si sottoscrivono a lui. Applica downsampling sulla telemetria per i sottoscrittori che lo richiedono. |
| **DataServer** | Aggregatore/storico | Si connette come client WS a uno o più DataService/DataProvider. Può caricare archivi Parquet salvati in precedenza. Espone come server WS/REST per i client finali. Inoltra comandi ricevuti verso la sorgente a cui è connesso. |

Il cambio di modalità operativa richiede un riavvio del servizio (niente hot-reload).

### Indipendenza dall'ordine di avvio

I tre nodi (DataProvider, DataService, DataServer) possono essere avviati in **qualsiasi ordine**. Ogni nodo e' resiliente all'assenza temporanea degli altri:

- **Connessione iniziale**: se l'upstream non e' raggiungibile, il client WS entra in un loop di reconnect con backoff esponenziale (1s → 30s max). Alla riconnessione vengono riavviati receive loop e heartbeat.
- **Discovery ritardata**: se l'upstream e' raggiungibile ma non ha ancora scoperto i tag (es. il simulatore UDP non ha ancora inviato metadata), il `getSources` restituisce una lista vuota. Quando i tag vengono scoperti, l'upstream invia un messaggio push `sourcesChanged` a tutti i client WS connessi. Il client ri-esegue automaticamente `getSources` e registra i nuovi tag.
- **Propagazione a catena**: la notifica `sourcesChanged` si propaga lungo tutta la catena (DataProvider → DataService → DataServer). Ogni nodo, ricevendo la notifica, aggiorna le proprie source/tag e ri-notifica i propri client WS a valle.
- **Sottoscrizioni**: l'`UpstreamSubscriptionAggregator` (DataServer) registra i client WS per le source scoperte dinamicamente, cosi' che le sottoscrizioni dei client finali vengano inoltrate correttamente upstream.
- **UDP inter-bridge**: il canale UDP e' intrinsecamente resiliente — il sender invia mapping periodici (ogni 10s), quindi il receiver scopre i tag appena i pacchetti mapping arrivano, indipendentemente dall'ordine di avvio.

```
Esempio: avvio in ordine inverso

1. DataServer parte         → retry connessione WS verso DataService
2. DataService parte        → retry connessione WS verso DataProvider
3. DataProvider parte       → connessione WS stabilita lungo la catena
4. Simulatore UDP parte     → invia metadata packet
5. DataProvider             → auto-discovery tag → broadcast "sourcesChanged"
6. DataService              → riceve "sourcesChanged" → ri-discovery → broadcast "sourcesChanged"
7. DataServer               → riceve "sourcesChanged" → ri-discovery → pronto per i client
```

### Protocollo inter-bridge: WS

Le connessioni WS tra bridge usano lo **stesso protocollo WebSocket** dei client, con queste convenzioni:
- Il consumatore (DataService o DataServer) si connette come client WS alla sorgente
- Alla connessione invia `getSources` per scoprire le source e i tag disponibili sull'upstream
- Invia `subscribe` con `source` e `tags` specifici (il campo `source` e' il nome della source scoperta via `getSources`)
- Se `source` omesso nel subscribe → sottoscrizione a tutte le source
- Se `tags: ["ALL"]` → tutti i tag della source
- Puo' specificare `downsampleMs` per richiedere telemetria a frequenza ridotta
- Puo' inviare `subscribe` con `since: <msgId>` per richiedere recovery dei messaggi persi
- La sorgente risponde con `recoveryStart` → messaggi normali → `recoveryEnd`, oppure `recoveryFailed` se il buffer e' gia' stato ruotato
- La sorgente invia `sourcesChanged` (push, senza richiesta) quando nuove source o tag vengono registrati. Il client ri-esegue `getSources` per aggiornare la propria lista

Con `SelectiveSubscription: true` (default nel DataServer), le sottoscrizioni upstream vengono gestite dall'`UpstreamSubscriptionAggregator`: il DataServer sottoscrive solo i canali che i suoi client stanno attivamente visualizzando. Con `SelectiveSubscription: false` (DataService), il nodo sottoscrive tutto al momento della connessione.

Il downsampling e' applicato **dalla sorgente** (il server) verso il sottoscrittore che lo richiede, secondo la configurazione `ForwardIntervalMs` del consumatore. Questo e' gestito lato server perche' e' il server a sapere la frequenza reale dei dati.

### Protocollo inter-bridge: UDP live stream

In alternativa (o in aggiunta) al canale WS, il DataService può inviare lo stream live via UDP verso il DataServer. UDP offre latenza inferiore e meno overhead — ideale per dati real-time dove la perdita occasionale di pacchetti è accettabile (il chunk transfer recupera tutto).

**Caratteristiche**:
- Il DataService è il **sender** (invia a una o più destinazioni configurate)
- Il DataServer è il **receiver** (ascolta su una singola porta per tutte le sorgenti)
- Piu' source possono condividere la stessa porta UDP — il protocollo include l'identificativo della sorgente (CRC32)
- Il DataServer può ricevere contemporaneamente da WS e UDP e deduplica via `msgId`
- Le destinazioni UDP si attivano/disattivano tramite comandi REST/WS senza riavvio

**Protocollo binario UDP**:

Il protocollo è binario e compatto, **completamente autosufficiente** — non richiede WS o REST per la risoluzione dei nomi. Ogni pacchetto UDP ha un MTU-safe di max **1400 byte** per garantire compatibilità internet (no frammentazione IP).

Il protocollo prevede due tipi di messaggio: **Mapping** (risoluzione nomi) e **Data** (valori tag).

#### Header comune (24 byte)

```
┌─────────────────────────────────────────────────────────┐
│                    UDP Packet Header (24 byte)           │
├──────────┬──────────┬──────────┬──────────┬─────────────┤
│ Magic(2) │ Ver(1)   │ Flags(1) │ SourceId │ Timestamp   │
│ 0xBD01   │ 0x01     │ see below│ (4 byte) │ (8 byte)    │
│          │          │          │ CRC32    │ Unix ms     │
├──────────┴──────────┴──────────┼──────────┴─────────────┤
│ GroupSeq (2 byte)              │ FragIdx(1) FragTot(1)   │
│ sequenza del gruppo di msg     │ 0/1 = no frag           │
├────────────────────────────────┴────────────────────────┤
│ MsgIdBase (4 byte) = msgId del primo record nel pkt     │
└─────────────────────────────────────────────────────────┘

Flags (1 byte):
  bit 0-1: tipo dato (00=telemetry, 01=event, 10=alarm)
  bit 2:   fragmented (1 = pacchetto parte di un gruppo frammentato)
  bit 3:   compressed (1 = payload compresso)
  bit 4:   metadata (riservato)
  bit 5:   mapping  (1 = pacchetto mapping nomi, 0 = pacchetto dati)
  bit 6-7: riservati
```

#### Messaggio Mapping (Flags bit 5 = 1, FlagMapping = 0x20)

Il sender invia periodicamente (default ogni 10s) uno o piu' pacchetti mapping che contengono le corrispondenze CRC32 → nome per source e tag. Il receiver **non puo' processare pacchetti dati** finche' non ha ricevuto il mapping completo — il canale UDP e' quindi autosufficiente.

Se i tag sono troppi per un singolo pacchetto, vengono divisi in piu' pacchetti. Ogni pacchetto porta `packetIdx` e `packetTotal` per consentire al receiver di collezionarli, ordinarli e processarli solo quando sono tutti arrivati.

```
┌──────────────────────────────────────────────────────────┐
│  Header (24 byte) — Flags = 0x20 (FlagMapping)          │
│  SourceId = CRC32 del nome source                        │
│  GroupSeq = identifica il gruppo di mapping packets       │
│  MsgIdBase = 0 (non usato per mapping)                   │
├──────────────────────────────────────────────────────────┤
│  Payload mapping:                                        │
│                                                          │
│  ┌─────────────┐                                         │
│  │ packetIdx(1)│  indice di questo pacchetto (0-based)   │
│  │ packetTot(1)│  numero totale pacchetti nel gruppo      │
│  ├─────────────┤                                         │
│  │ srcNameLen(2)│  lunghezza nome source (UTF-8 bytes)   │
│  │ srcName(N)   │  nome source in chiaro                 │
│  ├──────────────┤                                        │
│  │ tagCount(2)  │  numero tag IN QUESTO pacchetto        │
│  ├──────────────┤                                        │
│  │ Per ogni tag:                                         │
│  │  ┌──────────┬───────────┬──────────────┐              │
│  │  │ CRC32(4) │ nameLen(2)│ name(N)      │              │
│  │  └──────────┴───────────┴──────────────┘              │
│  │  ... ripetuto tagCount volte                          │
│  └───────────────────────────────────────────────────────┘
│                                                          │
│ Tutti i campi numerici sono little-endian.                │
│ I nomi sono stringhe UTF-8.                              │
└──────────────────────────────────────────────────────────┘
```

**Flusso mapping**:

```
DataService (sender)                     DataServer (receiver, porta 9200)
  │                                          │
  │ ── mapping pkt 0/3 (source + tag 0-49)  ──► │ bufferizza
  │ ── mapping pkt 1/3 (tag 50-99)          ──► │ bufferizza
  │ ── mapping pkt 2/3 (tag 100-142)        ──► │ tutti ricevuti → processa
  │                                          │   registra source "linea1"
  │                                          │   registra 142 tag (CRC→nome)
  │                                          │
  │ ── data pkt (telemetry) ─────────────────► │ ora puo' risolvere CRC→nome
  │                                          │
  │     ... 10 secondi dopo ...              │
  │ ── mapping pkt 0/3 (refresh) ───────────► │ aggiorna (noop se invariato,
  │ ── mapping pkt 1/3                 ──────► │  registra nuovi tag se aggiunti)
  │ ── mapping pkt 2/3                 ──────► │
```

Se il receiver riceve un pacchetto dati con un SourceId sconosciuto (mapping non ancora arrivato), lo scarta silenziosamente. Il prossimo ciclo di mapping risolve la situazione.

Se un pacchetto mapping del gruppo si perde (UDP e' best-effort), il gruppo parziale viene scartato dopo 30 secondi. Il prossimo ciclo periodico (10s) rinvia tutto.

**Esempio con pochi tag** (tutto in 1 pacchetto):

```
Header: Magic=0xBD01 Ver=1 Flags=0x20 SourceId=CRC32("linea1") ...
Payload:
  00           <- packetIdx = 0
  01           <- packetTotal = 1  (un solo pacchetto)
  06 00        <- srcNameLen = 6
  6C 69 6E 65 61 31  <- "linea1" (UTF-8)
  03 00        <- tagCount = 3
  A1 B2 C3 D4  <- CRC32("temperature")
  0B 00        <- nameLen = 11
  74 65 6D 70 65 72 61 74 75 72 65  <- "temperature"
  E5 F6 07 18  <- CRC32("pressure")
  08 00        <- nameLen = 8
  70 72 65 73 73 75 72 65  <- "pressure"
  ...
```

#### Messaggio Data (Flags bit 5 = 0, bit 0-1 = tipo dato)

I pacchetti dati trasportano i valori dei tag. Usano CRC32 per identificare i tag — il receiver li risolve in nomi tramite la tabella costruita dal mapping ricevuto in precedenza.

```
┌─────────────────────────────────────────────────────────┐
│  Header (24 byte) — Flags bit 0-1 = tipo dato           │
│  SourceId = CRC32 del nome source                        │
│  MsgIdBase = msgId del primo record nel pacchetto        │
├──────────────────────────────────────────────────────────┤
│  Payload dati (max 1376 byte):                           │
│                                                          │
│  N record, ciascuno:                                     │
│  ┌────────┬──────────┬──────────────────────┐            │
│  │TagId(4)│ValueType │Value (variabile)     │            │
│  │CRC32   │(1 byte)  │                      │            │
│  └────────┴──────────┴──────────────────────┘            │
│                                                          │
│  ValueType: 0x01=float32(4B), 0x02=float64(8B),         │
│             0x03=int32(4B),   0x04=bool(1B),             │
│             0x05=float32[],   0x06=float64[]             │
│  Per array: prefisso count(2 byte) + N valori            │
│                                                          │
│  MsgId di ogni record = MsgIdBase + indice nel pacchetto │
└──────────────────────────────────────────────────────────┘
```

**SourceId**: CRC32 del nome source. Il receiver lo risolve tramite il mapping ricevuto via UDP.

**TagId**: CRC32 (4 byte) del nome tag. Con 4 byte la probabilita' di collisione e' trascurabile anche con migliaia di tag (~0.00002% con 1000 tag). Il receiver risolve il mapping tramite la tabella costruita dai pacchetti mapping UDP.

**MsgId**: uint32, contatore incrementale per source. Il `MsgIdBase` nel header + offset posizionale del record nel pacchetto determinano il msgId di ogni record. A 1000 msg/sec dura ~49 giorni. Al rollover (wrap a 0) il DataServer rileva il wrap tramite il salto all'indietro e continua normalmente.

**Timestamp**: nel header del pacchetto, in millisecondi Unix UTC. Tutti i record nel pacchetto condividono lo stesso timestamp. Se i record hanno timestamp diversi → pacchetti separati.

**Frammentazione** (pacchetti dati):

Se i dati da inviare per un timestamp superano 1376 byte (es. tanti tag o array grandi):
- Il DataService li suddivide in N pacchetti con stesso `GroupSeq` e `Timestamp`
- `FragIdx` = indice del frammento (0-based), `FragTot` = totale frammenti
- Il DataServer riassembla i frammenti con stesso `GroupSeq` + `Timestamp` prima di processarli
- Se un frammento manca → il DataServer scarta il gruppo parziale (best-effort) — il chunk transfer recupera

```
Esempio: 60 tag telemetria allo stesso timestamp = ~360 byte → 1 pacchetto (no frag)
Esempio: 300 tag telemetria = ~1800 byte → 2 pacchetti (GroupSeq=42, Frag 0/2 e 1/2)
```

#### Sequenza di avvio del canale UDP

```
1. Sender avvia MappingBroadcast (periodico, ogni 10s)
2. Sender invia mapping packet(s)       → receiver costruisce tabelle CRC→nome
3. Sender inizia a inviare data packets → receiver puo' risolvere e processare
4. Ogni 10s: sender re-invia mapping    → receiver aggiorna (nuovi tag, noop se invariato)
```

Il canale UDP e' completamente autosufficiente: non richiede WS ne' REST per funzionare. Se il DataServer riceve sia dati UDP che WS, deduplica tramite `msgId`.

#### Deduplicazione

Il DataServer mantiene un set di `msgId` recenti (sliding window). Se un dato arriva via UDP con msgId=100234 e poi lo stesso arriva via WS (o viceversa), il duplicato viene scartato.

```
DataService                                  DataServer (porta 9200)
  │                                              │
  │ ──── UDP packet (linea1, telemetry) ──────► │ riceve, msgId=100234
  │ ──── UDP packet (linea1, event) ──────────► │ riceve, msgId=100235
  │                                              │
  │ ──── WS batch (linea1, telemetry) ────────► │ msgId=100234 gia' visto → skip
  │                                              │ msgId=100236 nuovo → inserisce
```

**Attivazione/disattivazione a runtime**:

Le destinazioni UDP sono configurate ma possono essere attivate/disattivate via comando:

```jsonc
// Attiva stream UDP verso una destinazione
POST /api/bridge/command
{ "command": "enableUdpStream", "source": "linea1", "destinationId": "server-main" }

// Disattiva
{ "command": "disableUdpStream", "source": "linea1", "destinationId": "server-main" }

// Stesso via WS
{ "op": "bridgeCommand", "id": "bc3", "command": "enableUdpStream",
  "params": { "source": "linea1", "destinationId": "server-main" } }
```

Il DataServer può attivare/disattivare la ricezione dei suoi stream:

```jsonc
POST /api/bridge/command
{ "command": "enableUdpReceive", "source": "linea1" }
{ "command": "disableUdpReceive", "source": "linea1" }
```

### Heartbeat inter-bridge

Le connessioni inter-bridge mantengono un heartbeat periodico per rilevare degradazione prima della disconnessione:

- Il consumatore invia `ping` periodico (ogni `HeartbeatIntervalMs`, default 10s)
- La sorgente risponde con `pong` + stato
- Il consumatore misura il RTT e lo espone come metrica
- Se N pong consecutivi mancano → riconnessione automatica
- Il RTT viene esposto via metriche e via API `/metrics`

```
Consumer                          Source
  │── ping ──────────────────────►│
  │◄─────────────────── pong ─────│  (RTT misurato)
  │                               │
  │── ping ──────────────────────►│
  │          ... timeout ...      │  (pong mancato, counter++)
  │                               │
  │── ping ──────────────────────►│
  │          ... timeout ...      │  (N pong mancati → reconnect)
```

### Downsampling del flusso dati (RF-18)

Il DataService applica downsampling **solo sulla telemetria** verso i sottoscrittori inter-bridge che lo richiedono (configurato via `ForwardIntervalMs`). Eventi e allarmi vengono sempre inoltrati immediatamente senza downsampling. I client finali ricevono sempre alla frequenza piena.

### Message ID (RF-19)

Ogni messaggio porta un **MsgId** (uint32) incrementale **per source**. Il MsgId è assegnato dalla prima sorgente che genera il dato (DataProvider o DataService con input diretto) e mantenuto lungo tutta la catena.

A 1000 msg/sec il MsgId dura ~49 giorni prima del rollover. Al wrap (overflow → 0) il DataServer rileva il salto all'indietro e continua normalmente la deduplicazione e il tracking.

### Flusso dati DataService → DataServer: due canali

La comunicazione tra DataService e DataServer opera su **due canali paralleli** con scopi diversi:

```
DataService                                              DataServer
  │                                                          │
  │ ═══ CANALE 1: Stream live (bassa latenza) ═══════════► │
  │  telemetria downsampliata + eventi/allarmi immediati     │  → chunk "live" (downsampliati)
  │  (via UDP e/o WS)                                        │
  │                                                          │
  │ ─── CANALE 2: Chunk transfer (background, WS) ───────► │
  │  chunk sealed completi, compressi, a bassa priorità      │  → sostituisce chunk "live"
  │                                                          │     con dati full-fidelity
```

**Canale 1 — Stream live**:
- Telemetria downsampliata via UDP (best-effort) e/o WS batch compresso
- Eventi e allarmi sempre immediati
- Serve la dashboard real-time del client
- I dati arrivano nel DataServer come chunk "live" (qualità ridotta per telemetria)

**Canale 2 — Chunk transfer in background**:
- Appena un chunk viene sealed nel DataService, viene trasferito al DataServer via WS
- Trasferimento compresso (Brotli), a bassa priorità di banda
- Il DataServer **sostituisce** il chunk live downsampliato con il chunk completo ricevuto
- Da quel momento in poi, le query su quel range temporale restituiscono dati full-fidelity
- Nessuna perdita di dati: tutto ciò che il DataService ha campionato arriva nel DataServer

```
Esempio timeline:

t=0     DataService: chunk 10:00-10:05 sealed, inizia transfer background
t=0     DataServer:  ha chunk 10:00-10:05 LIVE (downsampliato)
t=1s    Client:      queryTelemetry(10:00-10:05) → riceve dati downsampliati (quality: "live") ✓
t=3s    DataServer:  riceve chunk completo 10:00-10:05 → sostituisce il live
t=5s    Client:      queryTelemetry(10:00-10:05) → riceve dati full-fidelity (quality: "full") ✓
```

Il DataServer risponde **sempre immediatamente** con i dati che ha, senza mai attendere o richiedere chunk al DataService per servire una query. Il campo `quality` nella risposta informa il client sulla fidelity dei dati ricevuti. Il chunk transfer in background migliora progressivamente la qualità — ma è trasparente al client.

### Gestione chunk di confine

Quando il DataServer riceve un chunk Full mentre il chunk Live per lo stesso range è ancora attivo, controlla se il chunk Live contiene record con timestamp *dopo* il `toTs` del chunk Full. Se sì, quei record vengono spostati nel chunk successivo prima della sostituzione. Questo gestisce il caso edge in cui dati live arrivano durante il trasferimento del chunk full.

### Stato dei chunk nel DataServer

Ogni chunk nel DataServer ha un attributo di qualità:

| Stato | Descrizione |
|-------|-------------|
| `Live` | Dati dal canale 1 (downsampliati per telemetria, completi per eventi/allarmi) |
| `Full` | Chunk completo ricevuto dal canale 2 — sostituisce il chunk Live |
| `Loaded` | Chunk caricato da file Parquet (import manuale) |

Il client può sapere la qualità dei dati nelle risposte:
```jsonc
{ "type": "response", "id": "qt1", "ok": true, "kind": "telemetry",
  "data": [...], "from": "...", "to": "...",
  "quality": "full" }   // "live" | "full" | "loaded" | "mixed"
```

### Protocollo chunk transfer

```jsonc
// 1. DataService → tutti i client WS (notifica chunk disponibile, broadcast su OnChunkSealed)
{ "type": "chunkReady", "source": "linea1", "chunkId": "c-20260524-1000",
  "fromTs": "2026-05-24T10:00:00Z", "toTs": "2026-05-24T10:05:00Z",
  "firstMsgId": 100000, "lastMsgId": 100120, "records": 120 }

// 2. DataServer → DataService (richiesta trasferimento — WS op message)
{ "op": "chunkRequest", "id": "cr1", "source": "linea1", "chunkId": "c-20260524-1000" }

// 3. DataService → DataServer (risposta con tutti i record del chunk)
{ "type": "response", "id": "cr1", "ok": true,
  "chunkId": "c-20260524-1000", "source": "linea1",
  "fromTs": "2026-05-24T10:00:00Z", "toTs": "2026-05-24T10:05:00Z",
  "firstMsgId": 100000, "lastMsgId": 100120,
  "records": [
    { "source": "linea1", "tag": "temperature", "kind": "telemetry", "value": 23.4, "ts": "...", "msgId": 100000 },
    { "source": "linea1", "tag": "pressure", "kind": "telemetry", "value": 1.02, "ts": "...", "msgId": 100001 },
    ...
  ]
}
```

**Flusso completo:**
1. DataService: chunk sealed → flush Parquet (formato wide) + broadcast `chunkReady` a tutti i client WS
2. DataServer: riceve `chunkReady` → accoda in ChunkTransferService (priorità: chunk recenti prima)
3. DataServer → DataService: invia `chunkRequest` via WS
4. DataService: legge il chunk sealed dal buffer → risponde con array `records[]`
5. DataServer: deserializza i record → crea chunk Full → `ReplaceChunk` (Live→Full) → flush Parquet

### Compressione inter-bridge

**Stream live (canale 1 — WS)**:

La telemetria downsampliata viene accumulata in micro-batch (ogni `ForwardIntervalMs`) e inviata compressa. Eventi e allarmi sempre singoli e immediati.

```jsonc
// Batch telemetria (pre-compressione, inviato come WS binary)
{
  "type": "telemetryBatch",
  "source": "linea1",
  "compressed": "brotli",
  "count": 45,
  "samples": [
    { "tag": "temperature", "v": 23.4, "ts": "...", "msgId": 100234 },
    { "tag": "pressure",    "v": 1.02, "ts": "...", "msgId": 100235 }
  ]
}
```

**Chunk transfer (canale 2)**:

I chunk sealed vengono compressi con Brotli (compressione massima, non real-time) e trasferiti come binary frame. Il rapporto di compressione è tipicamente migliore perché il chunk è più grande e il compressore ha più contesto.

**Negoziazione alla connessione**:

Il consumatore comunica le preferenze nella subscribe:
```jsonc
{ "op": "subscribe", "id": "s1", "source": "linea1", "tags": ["ALL"],
  "downsampleMs": 2000,
  "compression": "brotli",
  "batchMode": true,
  "chunkSync": true           // abilita canale 2 (chunk transfer background)
}
```

`chunkSync: true` abilita il trasferimento chunk in background. Se `false` o assente → solo stream live (comportamento standard per client normali).

**Stima banda** (scenario tipico: 100 tag telemetria, campionamento 100ms):

| Canale | Banda |
|--------|-------|
| Stream live (downsample 2s + Brotli) | ~15-25 KB/min |
| Chunk transfer background (Brotli max, 5min chunk) | ~80-120 KB per chunk → ~16-24 KB/min |
| **Totale** | **~30-50 KB/min** per avere dati full-fidelity nel DataServer |

### Risoluzione query temporali

Quando un nodo riceve una query per intervallo, la risolve **localmente** nei propri chunk, selezionando la sorgente interna corretta in base al tipo richiesto:

```
queryTelemetry → cerca nei chunk telemetria
queryEvents    → cerca nei chunk eventi
queryAlarms    → cerca nei chunk allarmi
```

Il DataServer risponde sempre immediatamente con i dati che ha. La risposta include il campo `quality` per informare il client:
- `"full"` — tutti i chunk coinvolti sono full-fidelity
- `"live"` — tutti downsampliati (chunk transfer non ancora completato)
- `"mixed"` — alcuni chunk full, altri ancora live
- `"loaded"` — dati caricati da archivio Parquet

Il client non deve attendere né ri-chiedere — il chunk transfer in background migliora la qualità nel tempo in modo trasparente.

Ogni chunk mantiene i tre tipi separati. La risoluzione per range temporale sfrutta i metadati `fromTs`/`toTs` di ogni chunk per saltare quelli fuori range (O(chunk coinvolti)).

Se il range richiesto eccede il buffer disponibile, la risposta indica `truncated: true` e `availableFrom` con il timestamp più vecchio disponibile.

### Catena comandi bidirezionale (RF-17)

I comandi dal client seguono la catena inversa al flusso dati:

```
Client → DataServer → DataService → DataProvider → ADS/UDP
         ◄── risposta ◄── risposta ◄── risposta ──┘
```

Ogni nodo agisce come proxy trasparente: inoltra il comando upstream, attende la risposta e la restituisce downstream.

Ogni hop ha un **timeout configurabile**. Se un nodo non risponde entro il timeout, il nodo chiamante restituisce un errore al client indicando quale segmento della catena ha fallito (es. `"error": "timeout", "segment": "DataService→DataProvider"`).

L'acknowledge allarmi segue lo stesso percorso dei comandi.

## Buffer in memoria: Chunked Ring Buffer

DataService e DataServer mantengono i dati in memoria organizzati in **chunk temporali** (trunk) di durata configurabile.

```
Buffer circolare (es. DataServer, 5h, chunk da 5 min = ~60 chunk)

 ┌──────────┐  ┌──────────┐  ┌──────────┐       ┌──────────┐  ┌──────────┐
 │ Chunk 0  │→ │ Chunk 1  │→ │ Chunk 2  │→ ... →│ Chunk 58 │→ │ Chunk 59 │
 │ 10:00-05 │  │ 10:05-10 │  │ 10:10-15 │       │ 14:50-55 │  │ 14:55-00 │ ◄── attivo
 │ msgId    │  │ msgId    │  │ msgId    │       │ msgId    │  │ msgId    │
 │ 1000-1120│  │ 1121-1250│  │ 1251-1380│       │ ...      │  │ ...      │
 └──────────┘  └──────────┘  └──────────┘       └──────────┘  └──────────┘
       ▲ quando il buffer è pieno, il chunk più vecchio viene rimosso
```

Ogni chunk contiene:
- **Metadati**: `fromTs`, `toTs`, `firstMsgId`, `lastMsgId`, `source`, `recordCount`
- **Dati separati per tipo**: tre liste interne (telemetria, eventi, allarmi)
- **Stato**: `Open` (chunk attivo, in scrittura) o `Sealed` (completo, immutabile)
- **Qualità** (solo DataServer): `Live`, `Full`, `Loaded`

### Vantaggi dei chunk

| Operazione | Senza chunk | Con chunk |
|-----------|-------------|-----------|
| Query `telemetry?from=X&to=Y` | Scansione lineare di tutti i record | Skip dei chunk fuori range, scansione solo dei chunk coinvolti |
| Recovery `since: msgId` | Serializzazione record per record | Invio chunk interi (sealed) + solo parziale dal chunk attivo |
| Flush su Parquet | Logica complessa di batching | 1 chunk sealed = 1 file Parquet |
| Eviction (buffer pieno) | Rimozione record singoli + compattazione | Drop del chunk più vecchio in O(1) |
| Memoria (.NET GC) | Migliaia di piccoli oggetti, GC pressure | Array contigui per chunk, pochi oggetti large |

### Dimensionamento

| Modalità | Buffer tipico | Chunk consigliato | N° chunk |
|----------|--------------|-------------------|----------|
| **DataService** | 30-60 min | 5 min | 6-12 |
| **DataServer** | 4-5 ore | 5 min | 48-60 |

La durata del chunk è configurabile e **modificabile a runtime** via comando WS/REST senza riavvio. Questo permette di fare tuning in base alle condizioni di rete e carico:
- Chunk più piccoli (es. 1-2 min) = trasferimento più frequente, recovery più rapido, granularità migliore su eviction
- Chunk più grandi (es. 10-15 min) = meno overhead di metadati, compressione migliore, meno operazioni di flush
- Il valore di default (5 minuti) è un buon compromesso iniziale

La modifica prende effetto al prossimo seal del chunk attivo — il chunk corrente mantiene la durata con cui è stato creato.

### Ciclo di vita di un chunk

```
1. Nuovo chunk creato (Open) quando:
   - Il chunk attivo supera la durata configurata (ChunkDurationMin)
   - Primo avvio del servizio

2. Chunk sealed quando:
   - Scade la finestra temporale → diventa immutabile
   - Pronto per flush su Parquet (DataService)
   - Pronto per invio bulk (recovery inter-bridge)

3. Chunk rimosso quando:
   - Il buffer supera InMemoryMinutes → il chunk più vecchio viene eliminato
   - Dopo flush su Parquet avvenuto con successo (solo DataService, opzionale)
```

## Persistenza su disco — Formato Parquet Wide

Il sistema salva i dati su disco in formato Parquet con schema **wide/pivoted**: un file separato per ogni combinazione di (source, DataKind, chunk), con i tag come colonne.

### Naming dei file

```
{source}_{kind}_{fromTs}_{toTs}_{firstMsgId}_{lastMsgId}.parquet
```

Esempio:
```
linea1_telemetry_2026-06-02_10-00-00_2026-06-02_10-05-00_100000_100120.parquet
linea1_event_2026-06-02_10-00-00_2026-06-02_10-05-00_100121_100125.parquet
linea1_alarm_2026-06-02_10-00-00_2026-06-02_10-05-00_100126_100128.parquet
```

Dal nome si ricavano: source, tipo dato, intervallo temporale, range msgId — senza bisogno di aprire il file.

### Schema Parquet (telemetria)

```
timestamp_us  (long)      — timestamp in microsecondi Unix UTC
msg_id        (int)        — message ID
temperature   (double?)    — valore tag (null = nessun dato per questo timestamp)
pressure      (double?)    — valore tag
flow_rate     (double?)    — ...
```

Ogni riga corrisponde a un timestamp+msgId. I tag senza valore per un dato timestamp hanno `null` — Parquet li comprime a quasi zero overhead. La compressione colonnare di Parquet è molto efficiente su colonne omogenee di double (delta encoding + ZSTD).

### Vantaggi rispetto al formato long

| Aspetto | Long format (vecchio) | Wide format (attuale) |
|---------|----------------------|----------------------|
| **Schema** | source, tag, kind, value_json, ts, msg_id | ts, msg_id, tag1, tag2, ... |
| **Valori** | Serializzati come JSON string | Tipi nativi double |
| **Compressione** | Scarsa (stringhe ripetute) | Ottima (colonne double omogenee) |
| **Query per tag** | Scansione di tutte le righe | Column pruning: legge solo le colonne richieste |
| **Separazione tipi** | Tutto mischiato in un file | Un file per DataKind → schema ottimale per tipo |

### Chi scrive Parquet

Solo il **DataService** (`PersistToDisk: true`): scrive quando un chunk viene sealed (evento `OnChunkSealed`). Un chunk da 5 minuti produce fino a 3 file (uno per kind presente: telemetry, event, alarm).

Il **DataServer salva automaticamente** i chunk ricevuti via chunk transfer come file Parquet in `ParquetArchivePath`. Questo garantisce che i dati siano disponibili anche dopo un riavvio. Il DataServer può anche caricare file Parquet aggiuntivi (prodotti dal DataService o copiati manualmente) tramite le API `/archives/load` e `/archives/load-range`.

### Caricamento archivi (DataServer)

Il DataServer puo' caricare file Parquet come chunk `Loaded` in memoria:
- **Per singolo file**: `POST /archives/load` con nome file e tag opzionali
- **Per intervallo temporale**: `POST /archives/load-range` con source, kind, from, to — il sistema trova automaticamente i file Parquet che coprono l'intervallo richiesto e carica solo le colonne (tag) richieste

Questo permette al DataServer di ricostruire lo storico per sessioni di analisi offline: il client chiede un intervallo, il DataServer carica i Parquet corrispondenti e li serve come chunk Loaded tramite le stesse API di query usate per i dati live.

### Compattazione chunk

`POST /archives/compact` unisce tutti i chunk Parquet di un intervallo temporale in un unico file per source+kind. I dati vengono deduplicati (per tag+timestamp, si tiene il msgId piu' alto), ordinati cronologicamente, e scritti in un unico Parquet. I file originali vengono spostati in una sottocartella `.compacted/`.

### Dati storici (HistoricalDataPath)

Il DataServer puo' essere configurato con un `HistoricalDataPath` — una cartella che contiene file Parquet storici (prodotti dal DataService o copiati manualmente). Le API disponibili:

- **`POST /archives/scan-historical`**: scansiona la cartella, legge solo lo schema dei file (nomi colonne = tag) senza caricare dati. Ritorna un manifest con source, kind, tag, range, numero file.
- **`POST /archives/load-historical`**: carica i file nell'intervallo richiesto nel buffer in memoria come chunk Loaded, e registra source/tag nel SourceManager cosi' che appaiano in `getSources`.
- **`POST /sources/{source}/load-channels`** e **WS `loadChannels`**: legge dati direttamente dai Parquet nel `HistoricalDataPath` **senza caricarli nel ring buffer**. Supporta downsampling min-max per ridurre i dati trasferiti.

### Downsampling min-max (load-channels)

Quando il client specifica un parametro `resolution` (es. 1000), il server applica un downsampling **min-max bucketing** che preserva i picchi:

1. Divide l'intervallo `[from, to]` in `resolution` bucket temporali uguali
2. Per ogni bucket con > 4 punti: emette first, min, max, last (in ordine temporale)
3. Per bucket con <= 4 punti: emette tutti
4. Output: max `4 x resolution` punti per tag

Vantaggi rispetto a LTTB:
- Garantisce di preservare min e max assoluti per ogni intervallo — nessun spike perso
- Piu' semplice e prevedibile per dati industriali
- Il client WebClient usa `resolution = larghezza_chart * 1.5` per adattarsi alla viewport

La risposta include `totalPoints` e `returnedPoints` per informare il client sul livello di compressione, e `downsampled: true/false`. L'API e' predisposta per lazy loading futuro con campi `hasMore` e `nextPage`.

### Retrocompatibilita'

Il reader Parquet riconosce automaticamente il vecchio formato long (6 colonne: source, tag, kind, value_json, timestamp_us, msg_id) e lo legge correttamente. I nuovi file vengono scritti esclusivamente nel formato wide.

## Export dati

Oltre alle query temporali via REST/WS, il sistema supporta export diretto di dati in formati scaricabili:

- **Export CSV/Parquet**: endpoint REST che restituisce un file con i dati filtrati per source, tag, intervallo e tipo.
- **Download chunk**: possibilità di scaricare uno o più chunk sealed come file Parquet, utile per trasferimento bulk o analisi offline.

## Replay (DataServer)

Il DataServer supporta una modalità **replay**: il client richiede un intervallo temporale e una velocità di riproduzione, e il server invia i dati storici come stream push WS, rispettando la sequenza temporale originale (accelerata o rallentata).

Questo consente a un client web che già visualizza dati live di riusare lo stesso codice per analisi post-incidente, senza modifiche al rendering.

```
Client                                  DataServer
  │── replay(10:00-10:30, speed=10x) ──►│
  │                                      │ legge chunk 10:00-10:05
  │◄── telemetry (ts=10:00:00.5) ───────│ (replay=true)
  │◄── event    (ts=10:00:01.2) ────────│
  │◄── alarm    (ts=10:00:03.0) ────────│
  │◄── telemetry (ts=10:00:05.0) ───────│
  │     ...                              │
  │◄── replayEnd ───────────────────────│
```

## Selective Live Subscription (RF-31)

In modalità DataServer, non è necessario ricevere tutto lo stream live da ogni DataService. I dati non sottoscritti arrivano comunque via chunk transfer — ma con latenza maggiore.

### Concetto

Il **DataServer sottoscrive upstream solo i canali che i suoi client stanno attivamente visualizzando**. Quando nessun client guarda un canale, quel canale non viene inviato live — risparmiando banda.

### Componenti

- **UpstreamSubscriptionAggregator**: si interpone tra il `SubscriptionBroker` locale e i `BridgeWsClient` verso l'upstream. Mantiene un ref-count per tag per ogni source:
  - Tag 0→1: subscribe upstream + backfill storia recente
  - Tag 1→0: unsubscribe upstream
  - Se qualsiasi client chiede "ALL": short-circuit a ALL upstream
- **Debounce 80ms**: i cambi rapidi (es. un client che sottoscrive 20 tag in sequenza) vengono accumulati in un unico messaggio upstream
- **Backfill**: al primo subscribe di un tag, viene richiesta la telemetria degli ultimi N minuti (configurabile, default 10) e iniettata nel buffer locale
- **Reconnect**: al riconnessione del WsClient, l'aggregator ri-invia l'intero set corrente di tag sottoscritti

### Flusso

```
Client A → subscribe ["temp1","temp2"] su plc1
Client B → subscribe ["temp2","temp3"] su plc1

Aggregator ref-count: { temp1:1, temp2:2, temp3:1 }
→ Upstream subscribe: [temp1, temp2, temp3]

Client A si disconnette:
Aggregator: { temp2:1, temp3:1 }
→ Upstream unsubscribe: [temp1]
```

### Configurazione

- `SelectiveSubscription: true` (default, attivo solo in DataServer mode)
- `BackfillMinutes: 10`

## Compact Push Protocol (RF-32)

Per stream ad alta frequenza con molti canali, il formato verbose (un messaggio JSON per valore con nomi tag ripetuti) spreca banda. Il protocollo compatto usa un approccio a due fasi.

### Fase 1: Handshake

Il client manda subscribe con `"compact": true`:
```jsonc
{"op":"subscribe", "source":"plc1", "tags":["temp1","temp2","press1"], "compact":true}
```

Il server risponde con un **layout** posizionale:
```jsonc
{"type":"response", "ok":true, "compact":true,
 "layout":{"source":"plc1", "layoutId":1, "tags":["temp1","temp2","press1"]}}
```

### Fase 2: Push batch (ogni ~50ms)

Il server accumula tutti i valori arrivati nella finestra di flush (configurabile, default 50ms), poi manda **un solo messaggio** con array posizionale:

```jsonc
{"type":"d", "l":1, "ts":"2026-05-27T14:30:00Z", "m":4821,
 "v":[23.5, 18.2, null]}
```

- `type: "d"` = compact data batch
- `l` = layoutId (per verificare che client e server siano allineati)
- `v[i]` = valore per tags[i] del layout. `null` = nessun aggiornamento in questo tick
- `ts` = timestamp più recente nel batch
- `m` = msgId più alto nel batch

### Cambio sottoscrizioni

Se il client aggiunge o toglie tag, il server invia un `layoutUpdate` con il nuovo layout:
```jsonc
{"type":"layoutUpdate", "source":"plc1", "layoutId":2,
 "tags":["temp1","press1","flow1"]}
```

### Stima risparmio

Con 200 tag a 10Hz:
- Verbose: 200 messaggi × ~80 byte = ~16 KB/tick → **160 KB/s**
- Compact batch: 1 messaggio × ~1.2 KB → **12 KB/s**
- **~13× meno banda, ~200× meno messaggi WS**

### Retrocompatibilità

Chi non manda `compact: true` riceve i push verbose individuali — nessun breaking change.

## Chunk Transfer con Priorità (RF-33)

Il `ChunkTransferService` mantiene una coda ordinata per timestamp decrescente (chunk più recenti prima). Quando il DataService notifica chunk sealed, il DataServer li scarica partendo dai più recenti, così i dati più importanti per la visualizzazione sono disponibili prima.

## Input extensibili (RF-34)

L'architettura input è basata su factory pattern per facilitare l'integrazione di nuovi protocolli.

### Interfacce

- **`IDataInput`**: contratto per ogni driver. Include:
  - `Protocol` (string) — nome del protocollo ("udp", "ads", "modbus", "mock")
  - `Capabilities` (flag enum) — cosa supporta il driver:
    - `Receive` — può ricevere/push dati
    - `Read` — può leggere un tag on-demand
    - `Write` — può scrivere un tag
    - `Subscribe` — supporta subscription push (vs. polling)
    - `BatchRead` — può leggere più tag in una volta
    - `Browse` — può enumerare i tag disponibili dalla sorgente
  - `OnConnectionChanged` event — stato connessione
- **`IDataInputFactory`**: crea istanze `IDataInput` da `IDataInputConfig`
- **`IDataInputConfig`**: marker per config specifiche di protocollo (es. `UdpInputConfig` con porta e protocollo)

### InputRegistry

Registro centrale in `Program.cs`:
```csharp
var inputRegistry = new InputRegistry();
inputRegistry.Register(new MockInputFactory());
inputRegistry.Register(new UdpInputFactory());
// Futuri:
// inputRegistry.Register(new ModbusInputFactory());
// inputRegistry.Register(new AdsInputFactory());
// inputRegistry.Register(new OpcUaInputFactory());
```

Aggiungere un nuovo protocollo richiede: implementare `IDataInput`, `IDataInputFactory`, `IDataInputConfig`, registrare la factory. Zero modifiche al codice di bootstrap.

## Input supportati

| Input | Protocollo | Capabilities | Note |
|-------|-----------|--------------|------|
| **ADS (Beckhoff)** | ADS/AMS TCP | Receive, Read, Write, Subscribe | Lettura/scrittura variabili, notifiche on-change native. Da implementare (Fase 6). |
| **UDP Receiver** | UDP custom-v1 | Receive, Read | Ricezione pacchetti UDP binari con CRC32 tagId. |
| **Mock** | In-memory | Receive, Read | Genera dati fake (sine wave, eventi/allarmi casuali) per testing senza PLC. |
| **Modbus** | Modbus TCP/RTU | Receive, Read, Write, BatchRead | Da implementare. |
| **OPC-UA** | OPC-UA | Receive, Read, Write, Subscribe, Browse | Da implementare. |

I tipi di input possono coesistere nella stessa istanza e nella stessa source. L'architettura è estensibile tramite il pattern IDataInputFactory + InputRegistry.

## Componenti cross-cutting
- **Configuration**: `appsettings.json` + env vars + `IOptionsMonitor`.
- **Logging**: Serilog (console + file JSON).
- **Telemetry**: OpenTelemetry (metrics + tracing).
- **Auth**: autenticazione unificata per REST e WS (API key o JWT, stessa configurazione). Il token può essere passato via header `Authorization: Bearer <token>` o `X-Api-Key: <key>` su REST, e via query string `?token=<token>` su WS.
- **Rate limiting**: limiti configurabili per connessione WS (max sottoscrizioni, max query/min, max coda messaggi).

## Fasi di implementazione

```
Fase 1 — Verticale base (DataProvider con MockInput) ✅ COMPLETATA
├── Core: DataSource, Tag, TagValue, DataKind
├── Inputs: MockInput (genera dati fake)
├── Contracts: DTO WS + REST
├── Host: REST endpoints (/sources, /tags)
├── Host: WS handler (subscribe, push, read, write)
├── Config: appsettings con Mode=DataProvider
├── Tools: UdpSimulator, WebClient
└── Test: smoke test end-to-end (REST + WS)

Fase 2 — DataService ✅ COMPLETATA
├── Core: Chunk, ChunkedRingBuffer, seal, eviction
├── Storage: flush Parquet (formato wide, un file per source+kind+chunk), export CSV
├── InterBridge: WsClient (connessione al DataProvider)
├── Host: query temporali (queryTelemetry/Events/Alarms)
├── Host: comandi bridge (start/stop/archive/clear)
├── Config: Mode=DataService con Sources[] (AutoDiscovery) e DataSources[]
└── Test: buffer, query range, flush Parquet

Fase 3 — Input UDP ✅ COMPLETATA
├── Inputs: UdpInput (ricezione pacchetti UDP da PLC)
├── Test con UdpSimulator: catena completa DataProvider(UDP) → DataService
└── Test: parsing pacchetti, riconnessione, multi-tag

Fase 4 — DataServer base ✅ COMPLETATA
├── InterBridge: aggregazione multi-source
├── InterBridge: chunk transfer background (con priorità recenti)
├── Host: caricamento archivi Parquet
├── Config: Mode=DataServer
└── Test: chunk transfer, multi-source, catena completa con UDP

Fase 5 — Features avanzate ✅ COMPLETATA
├── InterBridge: UDP sender/receiver inter-bridge + protocollo binario
├── Core: allarmi ISA-18.2 (ack, 4 stati)
├── Host: rate limiting, metriche /metrics
├── Host: settings runtime (setChunkDuration)
├── InterBridge: compressione Brotli batch
├── Auth: JWT + API key unificata
├── Selective live subscription (UpstreamSubscriptionAggregator + backfill)
├── Compact push protocol (layout posizionale + batch accumulator)
├── Chunk transfer con priorità (chunk recenti prima)
├── Input extensibility (IDataInputFactory + InputRegistry)
└── Test: deduplicazione UDP/WS, allarmi, rate limit

Fase 6 — Web Client Vue 3
├── Vite + Vue 3 + TypeScript + uPlot + Pinia + Vue Router
├── Composables: useBridgeWs (WS + reconnect), useSubscription (compact/verbose),
│   useTimeSeriesBuffer (ring buffer uPlot-aligned), useQuery (storico + chunks)
├── Store: connectionStore (Pinia — stato, sources, info bridge)
├── Componenti: UPlotChart (wrapper auto-resize), TagSelector, SourceCard, QualityBadge
├── Layout: AppSidebar (navigazione + sources), StatusBar (connessione + RTT)
├── Pagine: Dashboard (griglia sources), Source (live chart), Query (storico),
│   Archives (tabella chunks), Settings
├── Dark theme industriale, code-split per route
└── Build: 0 errori TS, bundle ~103KB + ~24KB uPlot (gzipped)

Fase 7 — Input ADS
├── Inputs: AdsInput (Beckhoff TwinCAT)
├── Lettura/scrittura variabili, notifiche on-change
└── Test: integrazione con PLC simulato o reale

Fase 8 — Replay, export e polish
├── Host: replay mode
├── Host: export avanzato (download chunk)
├── Docs: API reference completa
└── Test: integration test full-chain
```
