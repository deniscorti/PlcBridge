# Configurazione

Ogni modalita' operativa ha il proprio progetto eseguibile con la propria `appsettings.json`. Ogni file contiene **tutte le sezioni possibili** con un flag `Enabled` che indica se la sezione e' attiva o meno.

## Config layering

1. `appsettings.json` del progetto — configurazione completa per la modalita'
2. `--config <file>` (o `-c <file>`) — override esplicito (priorita' massima), argomento da riga di comando
3. Variabili d'ambiente con prefisso `BRIDGE__` (doppio underscore, es. `BRIDGE__Http__Port=5080`)

## Come lanciare i nodi

```bash
# Terminale 1: DataProvider su porta 5080
dotnet run --project src/Bridge.DataProvider

# Terminale 2: DataService su porta 5081 (si collega al Provider su :5080)
dotnet run --project src/Bridge.DataService

# Terminale 3: DataServer su porta 5082 (si collega al Service su :5081)
dotnet run --project src/Bridge.DataServer
```

Oppure con `--config` override:
```bash
dotnet run --project src/Bridge.DataProvider -- --config config/my-provider.json
```

## Porte e connessioni

Ogni nodo e' un **server HTTP/WS** e opzionalmente un **client WS** verso un upstream. Ogni nodo deve avere una **porta diversa**:

```
DataProvider  :5080  ← solo server (legge da PLC, espone WS/REST)
DataService   :5081  ← server + client WS verso DataProvider :5080
DataServer    :5082  ← server + client WS verso DataService :5081
Client web             client WS verso DataServer :5082
```

---

## Riferimento completo parametri

Tutti i parametri sono sotto la sezione root `"Bridge"` in `appsettings.json`.

### `Mode` (string)

Modalita' operativa del nodo. Determina il comportamento del servizio.

| Valore | Descrizione |
|--------|-------------|
| `"DataProvider"` | Sorgente dati pura. Legge da PLC, espone via WS/REST. Nessun buffer. |
| `"DataService"` | Buffer in memoria + persistenza Parquet. Si connette all'upstream e/o legge direttamente. |
| `"DataServer"` | Aggregatore/storico. Si connette a uno o piu' DataService/DataProvider. Buffer grande, replay, archivi. |

---

### `Http` — Server HTTP

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Port` | int | `5080` | Porta di ascolto HTTP/Kestrel. Viene applicata direttamente a Kestrel. |

---

### `WebSocket` — Server WebSocket

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Path` | string | `"/ws"` | Path dell'endpoint WebSocket. |
| `MaxConnections` | int | `100` | Numero massimo di connessioni WS contemporanee. DataService usa 500, DataServer usa 2000. |
| `MaxSubscriptionsPerClient` | int | `200` | Numero massimo di sottoscrizioni per singolo client. DataServer usa 500. |
| `MaxQueriesPerMinute` | int | `60` | Rate limit: query massime al minuto per client. DataServer usa 120. |
| `MaxMessageQueueSize` | int | `10000` | Dimensione massima della coda messaggi per client. Se superata, i messaggi piu' vecchi vengono scartati. DataServer usa 50000. |
| `CompactFlushMs` | int | `50` | Intervallo in millisecondi per il flush dei batch nel protocollo compact push. Valori piu' bassi = latenza minore, piu' messaggi WS. |

---

### `Auth` — Autenticazione

Se entrambi i campi sono vuoti o assenti, l'autenticazione e' disabilitata.

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `ApiKey` | string? | `null` | API key statica. Se impostata, i client devono autenticarsi con questa chiave. |
| `JwtSecret` | string? | `null` | Secret per la validazione dei token JWT. Se impostato, abilita autenticazione JWT. |

**Metodi di autenticazione supportati** (tutti equivalenti):
- Header `X-Api-Key: <key>`
- Header `Authorization: Bearer <key>`
- Query string `?apiKey=<key>`
- Query string `?token=<key>` (utile per connessioni WS)

---

### `CommandTimeoutMs` (int, default: `5000`)

Timeout in millisecondi per l'esecuzione di comandi relay (catena bidirezionale client → DataServer → DataService → DataProvider → PLC). Se un hop non risponde entro il timeout, viene restituito un errore con il segmento che ha fallito. Il DataServer usa 10000 di default.

### `HeartbeatIntervalMs` (int, default: `10000`)

Intervallo in millisecondi tra i ping di heartbeat sulle connessioni inter-bridge. Serve a misurare il RTT e rilevare degradazione.

### `HeartbeatMaxMissed` (int, default: `3`)

Numero massimo di pong mancati consecutivi prima di triggare una riconnessione automatica.

---

### `DataSources[]` — Input diretti da PLC

Array di sorgenti dati con input hardware. Usato da **DataProvider** e opzionalmente da **DataService** (quando legge direttamente dal PLC senza passare da un Provider upstream).

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Enabled` | bool | `true` | Se `false`, la sorgente viene ignorata. |
| `Id` | string | **obbligatorio** | Nome univoco della source (es. `"linea1"`, `"pressa-nord"`). |
| `Inputs[]` | array | `[]` | Lista di input per questa source. Piu' input possono coesistere nella stessa source. |

#### `DataSources[].Inputs[]` — Configurazione input

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Type` | string | **obbligatorio** | Tipo di input: `"Mock"`, `"Udp"`, `"Ads"`. Determina quale factory crea il driver. |
| `AutoDiscovery` | bool | `false` | Solo per `Udp`: se `true`, i tag vengono scoperti automaticamente dai pacchetti metadata. La sezione `Tags` puo' essere omessa. |
| `Tags[]` | array | `[]` | Lista dei tag da leggere. Obbligatoria se `AutoDiscovery` e' `false`. |

**Parametri specifici per tipo di input:**

| Parametro | Tipo | Usato da | Descrizione |
|-----------|------|----------|-------------|
| `Host` | string | Ads | Indirizzo IP del PLC Beckhoff (es. `"192.168.0.10"`). |
| `AmsNetId` | string | Ads | AMS Net ID del PLC (es. `"5.23.40.1.1.1"`). |
| `Port` | int | Ads | Porta ADS (tipicamente `851`). |
| `ListenPort` | int | Udp | Porta UDP su cui ascoltare i pacchetti dal PLC (es. `9100`). |
| `Protocol` | string | Udp | Formato del protocollo UDP. Attualmente supportato: `"custom-v1"`. |

#### `DataSources[].Inputs[].Tags[]` — Definizione tag

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Name` | string | **obbligatorio** | Nome del tag. Deve essere univoco all'interno della source. |
| `DataKind` | string | `"Telemetry"` | Tipo di dato: `"Telemetry"` (valori numerici campionati), `"Event"` (cambi di stato discreti), `"Alarm"` (condizioni anomale, ISA-18.2). |
| `Address` | string? | `null` | Solo per Ads: indirizzo della variabile PLC (es. `"MAIN.fTemperature"`). |
| `PollMs` | int? | `null` | Intervallo di polling in millisecondi. Se omesso, usa il default del driver. Per Mock, definisce la frequenza di generazione dati. |
| `Offset` | int? | `null` | Solo per Udp: offset in byte del valore nel pacchetto. |
| `Length` | int? | `null` | Solo per Udp: lunghezza in byte del valore nel pacchetto. |

---

### `Sources[]` — Connessioni upstream (WS client)

Array di connessioni WebSocket verso nodi upstream. Usato da **DataService** (si collega al DataProvider) e **DataServer** (si collega al DataService). Il nodo corrente agisce come client WS.

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Enabled` | bool | `true` | Se `false`, la connessione viene ignorata. |
| `Id` | string | **obbligatorio** | Identificativo della connessione (es. `"upstream-provider"`, `"service-linea1"`). |
| `Url` | string | **obbligatorio** | URL WebSocket del nodo upstream (es. `"ws://localhost:5080/ws"`, `"wss://dataservice:5081/ws"`). |
| `ApiKey` | string? | `null` | API key per autenticazione verso l'upstream. Inviata come query string `?token=`. |
| `AutoDiscovery` | bool | `true` | Se `true`, alla connessione invia `getSources` all'upstream per scoprire automaticamente tutte le source e i tag disponibili. Se `false`, usa solo i tag in `Tags[]`. |
| `Tags` | string[] | `[]` | Lista esplicita di tag da sottoscrivere. Ignorata se `AutoDiscovery` e' `true`. |
| `ForwardIntervalMs` | int | `2000` | Intervallo di downsampling in millisecondi per la telemetria ricevuta dall'upstream. Il server upstream accumula i campioni e invia un batch ogni N ms. Non si applica a eventi e allarmi (sempre immediati). |
| `Compression` | string | `"none"` | Compressione dei messaggi WS. Valori: `"none"`, `"brotli"`. Con `"brotli"` i batch telemetria e i chunk vengono compressi prima dell'invio. |
| `BatchMode` | bool | `false` | Se `true`, la telemetria viene inviata dall'upstream in batch compressi (un messaggio WS binary per batch) anziche' come messaggi JSON singoli. Richiede `Compression` diverso da `"none"` per essere efficace. Riduce il numero di messaggi WS e la banda. |
| `ChunkSync` | bool | `false` | Se `true`, abilita il trasferimento chunk in background. Il nodo upstream invia `chunkReady` quando un chunk viene sealed, e il nodo corrente lo scarica per ottenere dati full-fidelity. Senza questo, il nodo ha solo dati live (potenzialmente downsampliati). |
| `ChunkSyncMaxBandwidthKbps` | int | `100` | Banda massima in Kbps allocata al chunk transfer in background. Limita l'impatto sulla rete. Aumentare per ambienti LAN, ridurre per connessioni lente. |

---

### `Buffer` — Ring buffer in memoria

Configurazione del buffer circolare a chunk. Usato da **DataService** e **DataServer**.

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Enabled` | bool | `true` | Se `false`, il buffer non viene creato (utile solo per testing). |
| `InMemoryMinutes` | int | `60` | Durata in minuti della finestra dati in memoria. I chunk piu' vecchi vengono rimossi. DataService tipico: 30-60 min. DataServer tipico: 300 min (5 ore). |
| `ChunkDurationMin` | int | `5` | Durata di ogni chunk in minuti. Chunk piu' piccoli = recovery piu' rapido, trasferimento piu' frequente. Chunk piu' grandi = meno overhead, compressione migliore. Modificabile a runtime via comando WS/REST `setChunkDuration`. |
| `PersistToDisk` | bool | `true` | Se `true`, i chunk sealed vengono scritti come file Parquet. Su DataService crea i file in `ParquetOutputPath`. |
| `ParquetOutputPath` | string | `"./data/parquet"` | Directory dove scrivere i file Parquet dei chunk sealed. Usato dal DataService. |
| `ParquetArchivePath` | string | `"./data/archives"` | Directory per gli archivi Parquet. Il DataServer salva automaticamente qui i chunk ricevuti via chunk transfer e li rilegge al caricamento archivi. |
| `HistoricalDataPath` | string? | `null` | Solo DataServer: cartella con file Parquet storici (prodotti dal DataService o copiati manualmente). Usata da `scan-historical`, `load-historical` e `load-channels` per accedere a dati storici senza caricarli nel ring buffer. |
| `FileFormat` | string | `"Parquet"` | Formato dei file su disco. Attualmente supportato solo `"Parquet"`. |

---

### `SelectiveSubscription` (bool, default: `true`)

Solo **DataServer**. Se `true`, il DataServer sottoscrive upstream solo i canali che i suoi client stanno attivamente visualizzando (gestito dall'`UpstreamSubscriptionAggregator`). I canali non sottoscritti arrivano comunque via chunk transfer ma con latenza maggiore. Se `false`, sottoscrive tutto al momento della connessione.

### `BackfillMinutes` (int, default: `10`)

Solo **DataServer**. Quando un tag viene sottoscritto per la prima volta upstream (tramite selective subscription), il DataServer richiede la telemetria degli ultimi N minuti e la inietta nel buffer locale. Utile per avere subito dati nel grafico quando un client si connette. Il DataServer usa 60 di default nel suo appsettings.

---

### `UdpDestinations[]` — Invio stream UDP inter-bridge

Array di destinazioni UDP per lo stream live. Usato dal **DataService** per inviare dati a bassa latenza verso uno o piu' DataServer.

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Enabled` | bool | `true` | Se `false`, la destinazione viene ignorata. Puo' essere attivata/disattivata a runtime via comando `enableUdpStream`/`disableUdpStream`. |
| `Id` | string | **obbligatorio** | Identificativo della destinazione (es. `"to-server"`, `"server-main"`). Usato nei comandi di attivazione/disattivazione. |
| `Host` | string | **obbligatorio** | IP o hostname del DataServer destinazione. |
| `Port` | int | `9200` | Porta UDP del DataServer. |
| `Sources` | string[] | `[]` | Lista dei nomi source da inviare a questa destinazione. Se vuoto, invia tutte le source. |
| `DownsampleMs` | int | `2000` | Intervallo di downsampling in millisecondi. La telemetria viene campionata a questa frequenza prima dell'invio UDP. Valori bassi = piu' banda, piu' precisione. |
| `MaxPacketBytes` | int | `1400` | Dimensione massima del pacchetto UDP in byte. 1400 e' MTU-safe per evitare frammentazione IP. |

Il sender invia automaticamente pacchetti **mapping** ogni 10 secondi con le corrispondenze CRC32 → nome per source e tag, rendendo il canale UDP completamente autosufficiente.

---

### `UdpReceiver` — Ricezione stream UDP inter-bridge

Configurazione del receiver UDP. Usato dal **DataServer** per ricevere lo stream live dal DataService.

| Parametro | Tipo | Default | Descrizione |
|-----------|------|---------|-------------|
| `Enabled` | bool | `true` | Se `false`, il receiver UDP non viene avviato. |
| `ListenPort` | int | `9200` | Porta UDP su cui ascoltare. Piu' source possono condividere la stessa porta (il protocollo include l'identificativo della source). |
| `AutoDiscovery` | bool | `true` | Se `true`, accetta qualsiasi source dallo stream UDP. La risoluzione nomi avviene tramite i pacchetti mapping inviati periodicamente dal sender. Se `false`, accetta solo i tag in `Tags[]`. |
| `Tags` | string[] | `[]` | Lista esplicita di tag da accettare. Usato solo se `AutoDiscovery` e' `false`. |

Il receiver puo' essere attivato/disattivato a runtime via comandi `enableUdpReceive`/`disableUdpReceive`.

---

## Valori di default per modalita'

I tre progetti hanno default diversi per alcuni parametri nel loro `appsettings.json`:

| Parametro | DataProvider | DataService | DataServer |
|-----------|:---:|:---:|:---:|
| `Http.Port` | 5080 | 5081 | 5082 |
| `WebSocket.MaxConnections` | 100 | 500 | 2000 |
| `WebSocket.MaxSubscriptionsPerClient` | 200 | 200 | 500 |
| `WebSocket.MaxQueriesPerMinute` | 60 | 60 | 120 |
| `WebSocket.MaxMessageQueueSize` | 10000 | 10000 | 50000 |
| `CommandTimeoutMs` | 5000 | 5000 | 10000 |
| `Buffer.Enabled` | — | true | true |
| `Buffer.InMemoryMinutes` | — | 60 | 300 |
| `Buffer.PersistToDisk` | — | true | false |
| `SelectiveSubscription` | — | — | true |
| `BackfillMinutes` | — | — | 60 |

---

## Sezioni attive per modalita'

| Sezione | DataProvider | DataService | DataServer |
|---------|:---:|:---:|:---:|
| `DataSources` | ✅ | ✅ (opzionale) | ❌ |
| `Sources` | ❌ | ✅ (opzionale) | ✅ |
| `Buffer` | ❌ | ✅ | ✅ |
| `UdpDestinations` | ❌ | ✅ (opzionale) | ❌ |
| `UdpReceiver` | ❌ | ❌ | ✅ (opzionale) |
| `SelectiveSubscription` | ❌ | ❌ | ✅ |
| `BackfillMinutes` | ❌ | ❌ | ✅ |

Il DataService puo' avere contemporaneamente `DataSources` (input diretti) e `Sources` (upstream WS).

---

## Variabili d'ambiente (esempi)

```
BRIDGE__Http__Port=5080
BRIDGE__Auth__ApiKey=supersecret
BRIDGE__Buffer__InMemoryMinutes=300
BRIDGE__SelectiveSubscription=true
BRIDGE__BackfillMinutes=60
```

---

## Client Web Vue 3

Il client web in `clients/web/` si configura tramite variabili d'ambiente Vite (prefisso `VITE_`).

Creare un file `clients/web/.env.local` (gitignorato):

```
VITE_BRIDGE_WS_URL=ws://localhost:5082/ws
VITE_BRIDGE_API_KEY=server-key-789
```

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `VITE_BRIDGE_WS_URL` | `ws://localhost:5080/ws` | URL WebSocket del Bridge |
| `VITE_BRIDGE_API_KEY` | (vuoto) | API key per autenticazione WS |

---

## Strumenti di test

### UdpSimulator

```bash
dotnet run --project tools/Bridge.Tools.UdpSimulator -- [host] [port] [intervalMs] [tagCount]
```

### WebClient (debug)

```bash
dotnet run --project tools/Bridge.Tools.WebClient -- [--bridge http://localhost:5080]
# Dashboard su http://localhost:5180
```
