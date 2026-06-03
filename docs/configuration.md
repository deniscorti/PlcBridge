# Configurazione

Ogni modalità operativa ha il proprio progetto eseguibile con la propria `appsettings.json`. Ogni file di configurazione contiene **tutte le sezioni possibili** con un flag `Enabled` che indica se la sezione è attiva o meno. Questo permette di vedere a colpo d'occhio tutte le opzioni disponibili.

## Config layering

1. `appsettings.json` del progetto — configurazione completa per la modalità
2. `--config <file>` — override esplicito (priorità massima), argomento da riga di comando
3. Variabili d'ambiente con prefisso `BRIDGE__` (doppio underscore, es. `BRIDGE__Http__Port=5080`)

## Come lanciare i nodi

Ogni nodo è un progetto separato:

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

Ogni nodo Bridge è un **server HTTP/WS** (accetta connessioni) e opzionalmente un **client WS** (si collega a un nodo upstream). Ogni nodo deve avere una **porta diversa**:

```
DataProvider  :5080  ← solo server (legge da PLC, espone WS/REST)
DataService   :5081  ← server + client WS verso DataProvider :5080
DataServer    :5082  ← server + client WS verso DataService :5081
Client web             client WS verso DataServer :5082
```

La porta è configurata in `Http.Port` e viene applicata automaticamente a Kestrel.

```
┌──────────────────┐       ┌──────────────────┐       ┌──────────────────┐
│  DataProvider     │       │  DataService      │       │  DataServer      │
│  Http.Port: 5080  │◄──WS──│  Http.Port: 5081  │◄──WS──│  Http.Port: 5082 │◄──WS── Client
│                   │       │  Sources: [       │       │  Sources: [      │
│                   │       │   Url: ws://:5080 │       │   Url: ws://:5081│
│                   │       │  ]                │       │  ]               │
└──────────────────┘       └──────────────────┘       └──────────────────┘
```

---

## Pattern Enabled

Ogni sezione di configurazione (DataSources, Sources, UdpDestinations, UdpReceiver, Buffer) ha un campo `Enabled` (bool). Con `Enabled: false` la sezione è visibile ma ignorata dal programma. Questo permette di:
- Vedere tutte le opzioni possibili senza cercare nella documentazione
- Attivare/disattivare funzionalità cambiando un solo campo
- Avere template di configurazione pronti da copiare

---

## DataProvider — `src/Bridge.DataProvider/appsettings.json`

Sorgente dati pura. Legge da PLC (ADS, UDP) o simulatore (Mock), espone via WS/REST.

Sezioni rilevanti:
- **DataSources** (`Enabled: true`): input diretti da PLC
- Buffer, Sources, UdpDestinations: non presenti (non servono)

---

## DataService — `src/Bridge.DataService/appsettings.json`

Buffer in memoria + persistenza Parquet. Si connette al DataProvider e/o legge direttamente da PLC.

Sezioni rilevanti:
- **Buffer** (`Enabled: true`): ring buffer con flush Parquet
- **Sources** (`Enabled: true/false`): connessione WS upstream al DataProvider. `AutoDiscovery: true` per scoprire tutti i tag automaticamente, oppure `Tags: ["tag1", "tag2"]` per lista esplicita
- **DataSources** (`Enabled: true/false`): input diretti (opzionale, se DataService legge anche direttamente)
- **UdpDestinations** (`Enabled: true/false`): invio stream UDP verso DataServer (invia automaticamente mapping periodico con nomi source/tag)

---

## DataServer — `src/Bridge.DataServer/appsettings.json`

Aggregatore. Si connette al DataService, buffer grande, replay, caricamento archivi Parquet per analisi offline.

Sezioni rilevanti:
- **Buffer** (`Enabled: true`): buffer grande in memoria (300 min default)
- **Sources** (`Enabled: true`): connessione WS upstream al DataService. `AutoDiscovery: true` per scoprire tutti i tag automaticamente, oppure `Tags: ["tag1", "tag2"]` per lista esplicita
- **UdpReceiver** (`Enabled: true/false`): ricezione stream UDP dal DataService. `AutoDiscovery: true` per accettare qualsiasi source dallo stream (risoluzione nomi tramite pacchetti mapping UDP)
- **SelectiveSubscription**: sottoscrive upstream solo i canali richiesti dai client
- Il DataServer **salva automaticamente** i chunk ricevuti via chunk transfer come file Parquet in `ParquetArchivePath`
- `ParquetArchivePath`: cartella dove trovare i Parquet da caricare per analisi offline

---

## Variabili d'ambiente (esempi)

```
BRIDGE__Http__Port=5080
BRIDGE__Auth__ApiKey=supersecret
BRIDGE__Buffer__InMemoryMinutes=300
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
