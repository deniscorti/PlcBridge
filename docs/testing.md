# Testing e strumenti di sviluppo

Guida completa ai test automatici e agli strumenti di sviluppo/debug disponibili nel progetto Bridge.

---

## Unit test

**Progetto**: `tests/Bridge.Core.Tests/`
**Framework**: xUnit 2.9 + Microsoft.NET.Test.Sdk 17.11
**Dipendenze**: Bridge.Core, Bridge.Inputs

### Esecuzione

```bash
# Tutti i test
dotnet test

# Con output verbose
dotnet test --verbosity normal

# Solo un file di test specifico
dotnet test --filter "FullyQualifiedName~DataSourceTests"

# Con copertura (richiede coverlet)
dotnet test --collect:"XPlat Code Coverage"
```

### Test disponibili (13 test)

#### DataSourceTests (4 test)

| Test | Descrizione |
|------|-------------|
| `RegisterTag_AssignsTagId` | Verifica che `RegisterTag` calcoli il CRC32 del nome tag come TagId |
| `RegisterTag_DuplicateName_Throws` | Un tag con nome duplicato nello stesso DataSource lancia `InvalidOperationException` |
| `MsgId_Increments` | `NextMsgId()` ritorna sequenza incrementale (1, 2, 3...) |
| `SetLastValue_GetLastValue_Roundtrips` | Roundtrip: scrive un TagValue e lo rilegge correttamente |

#### SubscriptionBrokerTests (5 test)

| Test | Descrizione |
|------|-------------|
| `Subscribe_ExactTag_MatchesCorrectValue` | Subscribe con tag specifico matcha solo quel tag |
| `Subscribe_ALL_MatchesAnyTag` | Subscribe con `"ALL"` matcha qualsiasi tag della stessa source |
| `Subscribe_NoSource_MatchesAllSources` | Subscribe senza source (null) matcha qualsiasi source |
| `Subscribe_ByKind_MatchesCorrectKind` | Subscribe per DataKind (es. Alarm) matcha solo quel tipo |
| `Unsubscribe_RemovesSubscription` | Unsubscribe di un tag non tocca gli altri |
| `UnsubscribeAll_ClearsEverything` | `UnsubscribeAll(connId)` rimuove tutte le sottoscrizioni della connessione |

#### MockInputTests (3 test)

| Test | Descrizione |
|------|-------------|
| `MockInput_ProducesValues` | MockInput genera valori periodici dopo Start (verifica con delay 350ms) |
| `MockInput_ReadTag_ReturnsValue` | `ReadTagAsync` ritorna un double per un tag registrato |
| `MockInput_ReadTag_UnknownTag_Throws` | `ReadTagAsync` con tag inesistente lancia `KeyNotFoundException` |

### Aggiungere nuovi test

Ogni test class va in un file separato in `tests/Bridge.Core.Tests/`. La convenzione è:

```csharp
using Bridge.Core.Model;
// using altre dipendenze...

namespace Bridge.Core.Tests;

public class NuovoComponenteTests
{
    [Fact]
    public void NomeMetodo_Scenario_Risultato()
    {
        // Arrange
        // Act
        // Assert
    }

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(0, 0, 0)]
    public void NomeMetodo_ConDati_Parametrizzato(int a, int b, int expected)
    {
        // ...
    }
}
```

### Test suggeriti per produzione

I seguenti test non sono ancora implementati ma sono consigliati prima del rilascio:

| Area | Test | Priorità |
|------|------|----------|
| **ChunkedRingBuffer** | Seal automatico al superamento durata, eviction chunk vecchi, query per range | Alta |
| **ChunkedRingBuffer** | Gestione chunk di confine (sostituzione Live→Full) | Alta |
| **CompactBatchAccumulator** | Accumulo e flush corretto, null per tag senza aggiornamento | Media |
| **CompactLayoutManager** | Layout versioning, AddTags/RemoveTags, rimozione connessione | Media |
| **UpstreamSubscriptionAggregator** | Ref-counting, debounce, ALL short-circuit, reconnect re-push | Alta |
| **ChunkTransferService** | Ordinamento priorità (recenti prima), completamento | Media |
| **InputRegistry** | Registrazione factory, Create per protocollo, protocollo sconosciuto | Bassa |
| **UdpInput** | Parsing pacchetti binari, multi-tag, tipi diversi | Alta |
| **AlarmService** | Transizioni 4 stati ISA-18.2, ack, auto-resolve | Media |
| **ParquetStorage** | Write/Read roundtrip, schema corretto | Media |
| **WsHandler** | Subscribe/unsubscribe, compact mode, rate limiting | Alta |
| **Integration** | Catena completa DataProvider→DataService→DataServer→Client | Alta |

Per i test di integrazione è consigliato usare `WebApplicationFactory<Program>` di ASP.NET Core per testare l'intera pipeline HTTP/WS senza rete.

---

## Strumenti di sviluppo

### Bridge.Tools.UdpSimulator

**Progetto**: `tools/Bridge.Tools.UdpSimulator/`
**Tipo**: Console app (.NET 8)

Genera pacchetti UDP binari che simulano un PLC reale. Utile per:
- Testare `UdpInput` senza hardware PLC
- Verificare la catena DataProvider(UDP) → DataService → DataServer
- Stress test con frequenza e numero tag configurabili
- Debug del protocollo binario UDP

#### Uso

```bash
dotnet run --project tools/Bridge.Tools.UdpSimulator -- [targetHost] [targetPort] [intervalMs] [tagCount]
```

| Parametro | Default | Descrizione |
|-----------|---------|-------------|
| `targetHost` | `127.0.0.1` | IP destinazione (Bridge con UdpInput) |
| `targetPort` | `9100` | Porta UDP destinazione |
| `intervalMs` | `200` | Intervallo tra pacchetti (ms) |
| `tagCount` | `10` | Numero di tag per pacchetto |

#### Formato pacchetto

Il simulatore produce pacchetti con lo stesso formato binario del protocollo inter-bridge:

```
Header (22 byte):
  Magic: 0xBD010100
  SourceId: CRC32("udp-sim")
  Timestamp: Unix microseconds (8 byte)
  Sequence: uint32 incrementale
  TagCount: uint16

Per ogni tag (9 byte):
  TagId: CRC32(nome tag) - 4 byte
  ValueType: 0x01 (float32) - 1 byte
  Value: sine wave + noise - 4 byte
```

Tag generati: `sensor_000`, `sensor_001`, ..., `sensor_N-1` con valori sinusoidali sfasati + rumore casuale. Log su console ogni 50 pacchetti.

#### Scenario tipico di test

```bash
# Terminale 1: avvia Bridge come DataProvider con UdpInput su porta 9100
dotnet run --project src/Bridge.Host

# Terminale 2: avvia simulatore (20 tag, 100ms)
dotnet run --project tools/Bridge.Tools.UdpSimulator -- 127.0.0.1 9100 100 20

# Terminale 3: verifica dati arrivati
curl http://localhost:5080/api/sources
curl http://localhost:5080/api/sources/udp-sim/tags/sensor_000
```

---

### Bridge.Tools.WebClient

**Progetto**: `tools/Bridge.Tools.WebClient/`
**Tipo**: ASP.NET Core app (Sdk.Web)

Dashboard HTML statica minimale per test manuali delle API REST e WebSocket di Bridge. Serve come strumento di debug rapido — non è il client di produzione.

#### Uso

```bash
dotnet run --project tools/Bridge.Tools.WebClient -- [--bridge http://localhost:5080]
```

| Parametro | Default | Descrizione |
|-----------|---------|-------------|
| `--bridge` | `http://localhost:5080` | URL del Bridge a cui connettersi |

Si apre su `http://localhost:5180`. L'endpoint `/config` ritorna URL del Bridge e URL WebSocket per il frontend.

#### Funzionalità

- Connessione WebSocket verso Bridge per ricevere push live
- Visualizzazione stato connessione e fonti dati
- Invio comandi manuale (subscribe, read, write, query)
- Test rapido senza necessità del client Vue 3

#### Quando usarlo

| Scenario | Strumento consigliato |
|----------|----------------------|
| Debug rapido API/WS durante sviluppo backend | **WebClient (tools)** |
| Dashboard operativa con grafici | **Web Client Vue 3** (clients/web) |
| Test automatizzati | **Unit test** (xUnit) |
| Simulazione dati PLC | **UdpSimulator** |

---

## Client web Vue 3 (sviluppo)

**Directory**: `clients/web/`
**Stack**: Vue 3 + Vite + TypeScript + uPlot + Pinia + Vue Router

Il client Vue 3 è il frontend di produzione. Per sviluppo:

```bash
cd clients/web
npm install
npm run dev      # Dev server su http://localhost:3000 con HMR
npm run build    # Build produzione in dist/
```

Il dev server proxya `/api/*` e `/ws/*` verso `http://localhost:5080` (Bridge).

### Variabili d'ambiente

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `VITE_BRIDGE_WS_URL` | `ws://localhost:5080/ws` | URL WebSocket del Bridge |
| `VITE_BRIDGE_API_KEY` | (vuoto) | API key per autenticazione |

Creare un file `.env.local` nella cartella `clients/web/`:
```
VITE_BRIDGE_WS_URL=ws://bridge.example.com/ws
VITE_BRIDGE_API_KEY=my-api-key
```

### Type-check e build

```bash
# Solo type-check (senza build)
npx vue-tsc --noEmit

# Build completa (type-check + vite build)
npm run build
```

---

## Scenario di test end-to-end

Catena completa con tutti e tre i livelli operativi:

```bash
# 1. DataProvider (porta 5080, MockInput)
cd src/Bridge.Host
BRIDGE__Mode=DataProvider BRIDGE__Http__Port=5080 dotnet run

# 2. DataService (porta 5081, si connette al DataProvider)
BRIDGE__Mode=DataService BRIDGE__Http__Port=5081 dotnet run

# 3. DataServer (porta 5082, si connette al DataService)
BRIDGE__Mode=DataServer BRIDGE__Http__Port=5082 dotnet run

# 4. Client Vue 3 (porta 3000, proxy verso DataServer)
cd clients/web
VITE_BRIDGE_WS_URL=ws://localhost:5082/ws npm run dev

# 5. (opzionale) UDP Simulator verso DataProvider
dotnet run --project tools/Bridge.Tools.UdpSimulator -- 127.0.0.1 9100 100 20
```

### Verifiche

| Verifica | Come |
|----------|------|
| Dati live fluiscono | Dashboard → Source page → Start Live → grafico si aggiorna |
| Compact push attivo | Ispeziona WS in DevTools: messaggi `{"type":"d","l":...}` |
| Chunk transfer | Archives page → seleziona source → chunks con stato Sealed/Full |
| Selective subscription | Sottoscrivi solo alcuni tag → verifica che upstream riceva solo quelli |
| Query storica | Query page → seleziona source/tags/range → grafico risultati |
| Reconnect automatico | Ferma il DataService → StatusBar mostra disconnected → riavvia → riconnessione |
