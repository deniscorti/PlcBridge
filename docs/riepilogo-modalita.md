# Riepilogo Modalita Operative — PlcBridge

## I tre attori

### DataProvider — Sorgente dati pura

**Ruolo**: punto di ingresso dal campo. Legge direttamente dai driver hardware (ADS, UDP, Mock) e rende i dati disponibili via REST e WebSocket.

| Aspetto | Dettaglio |
|---------|-----------|
| **Input** | ADS, UDP (custom-v1), Mock — anche combinati nello stesso DataSource |
| **Buffer** | Nessuno — i dati non vengono accumulati in memoria |
| **Persistenza** | Nessuna |
| **Server WS/REST** | Si — accetta client WS e richieste REST |
| **Client WS upstream** | No (by design) |
| **Invio UDP inter-bridge** | No (by design) |
| **Query temporali** | No — risponde con errore `NO_BUFFER` |
| **Read tag** | Si — legge direttamente dal driver |
| **Write tag** | Si — se il driver lo supporta (ADS si, UDP no, Mock no-op) |
| **Subscribe WS** | Si — push on-change ai client collegati |
| **Comandi** | Esegue direttamente (es. write ADS), non inoltra a nessuno |
| **Allarmi** | Si — gestione ISA-18.2 completa, ack ricevuti dai client |

**Quando usarlo**: vicino al PLC, in rete di campo. Serve come "traduttore" del protocollo PLC in un'API web standard.

---

### DataService — Aggregatore intermedio con persistenza

**Ruolo**: bufferizza, persiste su Parquet, fa downsampling e inoltra dati a valle. Puo leggere direttamente dai driver OPPURE collegarsi come client WS a un DataProvider.

| Aspetto | Dettaglio |
|---------|-----------|
| **Input diretto** | Si — ADS, UDP, Mock (stessi del DataProvider) tramite `DataSources[]` |
| **Client WS upstream** | Si — si collega a DataProvider o altri bridge tramite `Sources[]` |
| **Entrambi insieme** | Si — alcuni DataSource da input diretto, altri da upstream WS |
| **Buffer** | Si — ChunkedRingBuffer (configurabile, es. 60 min, chunk da 5 min) |
| **Persistenza** | Si — Parquet su chunk sealed (se `PersistToDisk: true`) |
| **Server WS/REST** | Si — accetta client finali e bridge downstream |
| **Invio UDP inter-bridge** | Si — `UdpDestinations[]` verso DataServer, con downsampling |
| **Ricezione UDP inter-bridge** | No (by design) |
| **Query temporali** | Si — dalla finestra in memoria (e opzionalmente da disco) |
| **Downsampling** | Si — applica `ForwardIntervalMs` sulla telemetria verso sottoscrittori inter-bridge |
| **Chunk transfer (invio)** | Si — notifica `chunkReady` e invia chunk sealed compressi via WS |
| **Comandi** | Inoltra upstream (verso DataProvider) se collegato, oppure esegue se ha input diretto |
| **Export** | Si — CSV e Parquet via REST |
| **Auto-Discovery UDP** | Si — con `AutoDiscovery: true` riceve metadata e registra tag automaticamente |
| **Auto-Discovery WS** | Si — invia `getSources` all'upstream alla connessione |

**Quando usarlo**: in sala server locale, vicino alla rete di campo. E il nodo che "cattura tutto" a frequenza piena e persiste su disco.

---

### DataServer — Storicizzatore e aggregatore finale

**Ruolo**: riceve dati da uno o piu DataService (o DataProvider), mantiene un buffer ampio in memoria, supporta replay e caricamento archivi Parquet.

| Aspetto | Dettaglio |
|---------|-----------|
| **Input diretto** | No (by design) — non legge da driver hardware |
| **Client WS upstream** | Si — `Sources[]` verso DataService o DataProvider |
| **Buffer** | Si — ampio (es. 5 ore, chunk da 5 min = ~60 chunk) |
| **Persistenza Parquet** | Si — il DataServer scrive Parquet (sempre abilitato) |
| **Caricamento archivi** | Si — puo importare file Parquet come chunk `Loaded` |
| **Server WS/REST** | Si — espone ai client finali (dashboard, app) |
| **Ricezione UDP inter-bridge** | Si — `UdpReceiver` su porta unica, multi-source |
| **Invio UDP inter-bridge** | No (by design) |
| **Deduplicazione** | Si — via `msgId` tra canale UDP e WS |
| **Chunk transfer (ricezione)** | Si — riceve chunk full e sostituisce chunk live |
| **Selective subscription** | Si — sottoscrive upstream solo i canali che i client richiedono |
| **Backfill** | Si — recupera N minuti di storia al primo subscribe di un canale |
| **Query temporali** | Si — con indicazione `quality` (live/full/loaded/mixed) |
| **Replay** | Si — riproduzione storica con velocita configurabile |
| **Downsampling** | Si — puo richiedere dati downsampliati all'upstream |
| **Comandi** | Inoltra upstream lungo la catena inversa |
| **Export** | Si — CSV e Parquet via REST |
| **Compact push** | Si — protocollo posizionale per client ad alta frequenza |

**Quando usarlo**: in cloud/DMZ/sede centrale. Aggrega piu linee, serve le dashboard, offre storico e replay.

---

## Matrice funzionalita

| Funzionalita | DataProvider | DataService | DataServer |
|---|:---:|:---:|:---:|
| Input diretto (ADS/UDP/Mock) | SI | SI | - |
| Buffer in memoria (chunk) | - | SI | SI |
| Persistenza Parquet (scrittura) | - | SI (opzionale) | SI (sempre) |
| Caricamento archivi Parquet | - | - | SI |
| Connessione upstream WS | - | SI | SI |
| Server WS (accetta client) | SI | SI | SI |
| Server REST | SI | SI | SI |
| Invio UDP inter-bridge | - | SI | - |
| Ricezione UDP inter-bridge | - | - | SI |
| Deduplicazione UDP/WS | - | - | SI |
| Chunk transfer (invio) | - | SI | - |
| Chunk transfer (ricezione) | - | - | SI |
| Query temporali | - | SI | SI |
| Downsampling telemetria | - | SI | SI |
| Selective subscription | - | - | SI |
| Backfill storia | - | - | SI |
| Replay storico | - | - | SI |
| Compact push protocol | SI | SI | SI |
| Gestione allarmi ISA-18.2 | SI | SI | SI |
| Comandi relay | SI (esegue) | SI (inoltra) | SI (inoltra) |
| Export CSV/Parquet | - | SI | SI |
| Read tag on-demand | SI | SI | SI |
| Write tag | SI (se driver supporta) | SI | SI (inoltra) |
| Auto-Discovery UDP | SI | SI | - |
| Auto-Discovery WS | - | SI | SI |

---

## Combinazioni di funzionamento

### 1. Catena completa a 3 nodi

```
PLC --ADS/UDP--> DataProvider --WS--> DataService --WS+UDP--> DataServer --> Dashboard
                   :5080                 :5081                   :5082
```

- DataProvider: legge dal PLC, espone WS/REST
- DataService: bufferizza, persiste Parquet, downsampla e inoltra
- DataServer: buffer ampio, chunk transfer, replay, serve i client

### 2. DataService diretto (senza DataProvider)

```
PLC --ADS/UDP--> DataService [buffer + Parquet] --WS/REST--> Client
                     :5080
```

- Il DataService legge direttamente dal PLC tramite `DataSources[].Inputs[]`
- Supporta ADS, UDP (anche con AutoDiscovery), Mock
- Il client (web o tool) si collega direttamente al DataService
- **Questa e la configurazione che stai usando con il simulatore UDP**

### 3. DataService diretto + DataServer a valle

```
PLC --UDP--> DataService [buffer + Parquet] --WS+UDP--> DataServer --> Dashboard
                 :5080                                      :5082
```

- Come sopra, ma con DataServer per storico piu ampio e aggregazione

### 4. DataProvider + DataServer (senza DataService)

```
PLC --ADS--> DataProvider --WS--> DataServer [solo buffer in-memory] --> Dashboard
                :5080                 :5082
```

- Nessuna persistenza intermedia Parquet
- Il DataServer non riceve chunk full (il DataProvider non li produce)
- I dati oltre la finestra di buffer vengono persi
- Utile per scenari semplici senza necessita di storico persistente

### 5. Solo DataProvider + Client

```
PLC --ADS/UDP--> DataProvider --WS/REST--> Client
                     :5080
```

- Il piu semplice possibile
- Nessun buffer, nessuna persistenza
- Il client riceve solo dati live (subscribe + push)
- Non puo fare query temporali

### 6. Multi-linea centralizzata

```
PLC L1 --> Provider L1 --> Service L1 --+
PLC L2 --> Provider L2 --> Service L2 --+-WS+UDP--> DataServer --> Dashboard
PLC L3 --> Provider L3 --> Service L3 --+               :5082
```

- Ogni linea ha la sua catena Provider+Service
- Un DataServer unico aggrega tutto
- Il DataServer riceve da ciascun Service su canale WS separato + UDP sulla stessa porta
- Le dashboard vedono tutte le linee come source separate

### 7. DataService misto (input locale + upstream remoto)

```
PLC locale --ADS--+
                  +--> DataService [buffer + Parquet] --> Client
Provider remoto --WS--+     :5081
```

- `DataSources[]` per input locale diretto
- `Sources[]` per upstream WS remoto
- Entrambi coesistono nello stesso DataService
- Un solo buffer, un solo server WS/REST

### 8. DataService standalone per test (la tua configurazione attuale)

```
Simulatore UDP --> DataService [buffer 60min, Parquet write-only] --> WebClient tool
   porta 9100        :5080                                             :5180
```

- `AutoDiscovery: true` — il simulatore invia metadata, nessun tag da configurare
- Il WebClient si collega via WS e REST direttamente al DataService
- I dati vengono bufferizzati in memoria e scritti su Parquet (ma non riletti)

---

## Canali di comunicazione tra nodi

Ci sono **3 tipi di comunicazione** nel sistema, di cui 2 usano UDP ma con scopi completamente diversi:

### 1. UDP Input — dati dal campo (PLC/simulatore --> Bridge)

| | |
|---|---|
| **Cosa e** | Un **driver di input**, come ADS o Mock. Riceve dati dal mondo esterno. |
| **Chi lo invia** | Un PLC fisico o il simulatore UDP |
| **Chi lo riceve** | **DataProvider** o **DataService** (tramite `DataSources[].Inputs[].Type: "Udp"`) |
| **Porta tipica** | 9100 |
| **Protocollo** | `custom-v1` — binario, definito dal PLC/simulatore |
| **Configurazione** | `"Type": "Udp", "ListenPort": 9100, "AutoDiscovery": true` |

Questo e lo stesso tipo di input dell'ADS: il bridge lo usa per **acquisire** dati dalla sorgente.
Supporta AutoDiscovery (metadata periodico dal simulatore) e puo coesistere con ADS nello stesso DataSource.

### 2. UDP Inter-Bridge — stream live tra nodi bridge (DataService --> DataServer)

| | |
|---|---|
| **Cosa e** | Un canale **best-effort a bassa latenza** tra due nodi bridge. Non e un input. |
| **Chi lo invia** | Solo **DataService** (configurato in `UdpDestinations[]`) |
| **Chi lo riceve** | Solo **DataServer** (configurato in `UdpReceiver{}`) |
| **Porta tipica** | 9200 |
| **Protocollo** | Binario compatto proprietario (header 24 byte, MTU-safe 1400 byte, CRC32 per source/tag, msgId per deduplicazione) |
| **Configurazione sender** | `"UdpDestinations": [{ "Host": "...", "Port": 9200, "Sources": [...] }]` |
| **Configurazione receiver** | `"UdpReceiver": { "ListenPort": 9200, "Sources": [...] }` |

Questo e un **canale opzionale** alternativo/complementare al WebSocket tra DataService e DataServer.
Il vantaggio e la latenza inferiore. Lo svantaggio e che puo perdere pacchetti — ma il chunk transfer WS recupera tutto.
Il DataServer deduplica automaticamente: se un dato arriva prima via UDP e poi via WS (o viceversa), il duplicato viene scartato tramite `msgId`.

**Quando usarlo**: quando il DataServer e su rete diversa (internet, VPN) e serve latenza minima per la dashboard real-time.
**Quando non serve**: se DataService e DataServer sono sulla stessa LAN, il WS basta.

### 3. WebSocket — canale affidabile bidirezionale

| | |
|---|---|
| **Cosa e** | Il canale principale per tutto: dati, comandi, query, chunk transfer |
| **Chi lo usa** | Tutti: client finali, DataService<-->DataProvider, DataServer<-->DataService |
| **Direzione** | Bidirezionale |

| Direzione | Cosa trasporta |
|-----------|----------------|
| Client --> Server | subscribe, unsubscribe, read, write, comandi, query, ackAlarm |
| Server --> Client | push telemetria/eventi/allarmi, risposte, chunkReady, chunk transfer |

- Supporta: recovery (`since: msgId`), compressione Brotli, batch mode, compact protocol
- Heartbeat: ping/pong periodico con RTT misurato

### Riepilogo: chi usa cosa

```
                        UDP Input (9100)    UDP Inter-Bridge (9200)    WebSocket
                        ~~~~~~~~~~~~~~~~    ~~~~~~~~~~~~~~~~~~~~~~~    ~~~~~~~~~
PLC/Simulatore              INVIA                   -                     -
DataProvider                RICEVE                  -                   SERVER
DataService                 RICEVE                INVIA                SERVER + CLIENT
DataServer                    -                   RICEVE               SERVER + CLIENT
Client finale                 -                     -                   CLIENT
```

---

## Note sul codice vs design

Dall'analisi del codice (`Program.cs`), alcune restrizioni della tabella sono **by design** ma **non enforce nel codice**:

| Feature | Design | Codice |
|---------|--------|--------|
| DataProvider con `Sources[]` (client WS upstream) | Non previsto | Non bloccato — funzionerebbe |
| DataServer con `DataSources[].Inputs[]` (input diretto) | Non previsto | Non bloccato — funzionerebbe |
| DataProvider con `UdpDestinations[]` (invio UDP) | Non previsto | Non bloccato — funzionerebbe |

Queste combinazioni non sono testate ne documentate. Potrebbero funzionare ma non sono garantite. Se servissero, il DataService copre gia tutti questi scenari.
