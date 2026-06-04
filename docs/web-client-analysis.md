# Web Client — Analisi architettura

## Stack tecnologico

| Layer | Scelta | Motivazione |
|-------|--------|-------------|
| Framework | **Vue 3** (Composition API + `<script setup>`) | Reattività fine-grained, composables per logica riusabile, ottimo per dashboard |
| Build | **Vite** | HMR istantaneo, tree-shaking, ESM nativo |
| Charts | **uPlot** | Fastest time-series chart (~45KB), gestisce 100k+ punti, canvas puro |
| State | **Pinia** | Store per sources, subscriptions, alarms, connection state |
| UI | **PrimeVue** o **Naive UI** | Componenti pronti (tabelle, dialog, toast, sidebar). Alternativa: headless con Tailwind |
| WS client | Custom composable | Gestione connect/reconnect, compact push, layout tracking |
| Router | **Vue Router** | Pagine: Dashboard, Source detail, Alarms, Archives, Settings |

## Struttura progetto

```
clients/web/
├── index.html
├── vite.config.ts
├── package.json
├── tsconfig.json
├── src/
│   ├── main.ts                     // App bootstrap
│   ├── App.vue                     // Layout: sidebar + router-view
│   ├── router/
│   │   └── index.ts                // Route definitions
│   │
│   ├── composables/                // Logica riusabile (Composition API)
│   │   ├── useBridgeWs.ts          // WebSocket connection + reconnect + compact protocol
│   │   ├── useSubscription.ts      // Subscribe/unsubscribe tag, gestione layout compatto
│   │   ├── useTimeSeriesBuffer.ts  // Ring buffer lato client per dati chart
│   │   ├── useAlarms.ts            // Stato allarmi ISA-18.2, ack
│   │   ├── useQuery.ts             // Query temporali (telemetry, events, alarms)
│   │   └── useExport.ts            // Export CSV/Parquet download
│   │
│   ├── stores/                     // Pinia stores
│   │   ├── connectionStore.ts      // Stato connessione WS, source list, health
│   │   ├── subscriptionStore.ts    // Tag sottoscritti, layout compatti attivi
│   │   └── alarmStore.ts           // Allarmi attivi, history, ack state
│   │
│   ├── components/
│   │   ├── charts/
│   │   │   ├── UPlotChart.vue      // Wrapper Vue per uPlot (generico)
│   │   │   ├── TelemetryChart.vue  // Chart telemetria con streaming live
│   │   │   ├── EventTimeline.vue   // Timeline eventi discreti
│   │   │   └── AlarmGauge.vue      // Indicatore stato allarmi
│   │   │
│   │   ├── layout/
│   │   │   ├── AppSidebar.vue      // Navigation: sources, pages
│   │   │   ├── AppHeader.vue       // Connection status, mode badge
│   │   │   └── ConnectionBadge.vue // Pallino verde/rosso + RTT
│   │   │
│   │   ├── source/
│   │   │   ├── SourceCard.vue      // Card riepilogo source (tag count, buffer, stato)
│   │   │   ├── TagList.vue         // Elenco tag con ultimo valore + sparkline
│   │   │   ├── TagSelector.vue     // Multi-select tag per chart/subscribe
│   │   │   └── BufferInfo.vue      // Info chunk: quality, range, record count
│   │   │
│   │   ├── alarms/
│   │   │   ├── AlarmTable.vue      // Tabella allarmi attivi (ISA-18.2 states)
│   │   │   ├── AlarmBanner.vue     // Banner top con count allarmi non-ack
│   │   │   └── AlarmHistory.vue    // Storico allarmi con filtri
│   │   │
│   │   └── common/
│   │       ├── TimeRangePicker.vue // Selettore intervallo temporale
│   │       ├── QualityBadge.vue    // Badge live/full/loaded/mixed
│   │       └── JsonViewer.vue      // Debug: visualizza messaggi WS raw
│   │
│   ├── pages/
│   │   ├── DashboardPage.vue       // Overview: tutte le source + allarmi attivi
│   │   ├── SourcePage.vue          // Dettaglio source: chart + tag list + buffer
│   │   ├── AlarmsPage.vue          // Vista allarmi dedicata
│   │   ├── ArchivesPage.vue        // Gestione file Parquet (list, load)
│   │   ├── QueryPage.vue           // Query temporali manuali (debug/analisi)
│   │   └── SettingsPage.vue        // Impostazioni bridge (chunkDuration, etc.)
│   │
│   ├── lib/
│   │   ├── ws-protocol.ts          // Parsing messaggi WS (verbose + compact)
│   │   ├── compact-layout.ts       // Gestione layout compatti lato client
│   │   └── time-utils.ts           // Formattazione timestamp, range helpers
│   │
│   └── types/
│       ├── bridge.ts               // Types: Source, Tag, TagValue, AlarmState, etc.
│       ├── ws-messages.ts          // Types per messaggi WS (client→server, server→client)
│       └── chart.ts                // Types per configurazione chart
```

## Composables chiave

### `useBridgeWs` — Connessione WebSocket

```
Responsabilità:
- Connect con apiKey (query string)
- Auto-reconnect con backoff esponenziale
- Ping/pong heartbeat
- Dispatch messaggi ricevuti (verbose e compact)
- Track connection state (connected, connecting, disconnected)
- Metriche: RTT, messaggi/sec, bytes/sec

Stato esposto:
- isConnected: Ref<boolean>
- connectionState: Ref<'connecting' | 'connected' | 'disconnected'>
- rttMs: Ref<number>
- send(msg): void
- onMessage(handler): void  // raw dispatch
```

### `useSubscription` — Gestione sottoscrizioni + compact layout

```
Responsabilità:
- Subscribe/unsubscribe tag per source (con compact:true)
- Mantiene la mappa layout: layoutId → tags[]
- Decodifica messaggi compact batch: {"type":"d","l":1,"v":[...]} → TagValue[]
- Emette valori decodificati come stream reattivo
- Gestisce layoutUpdate (aggiorna la mappa quando cambiano le sub)

Stato esposto:
- subscribedTags: Map<source, Set<tag>>
- subscribe(source, tags[]): void
- unsubscribe(source, tags[]): void
- onValue(handler: (tagValue) => void): void
```

### `useTimeSeriesBuffer` — Buffer circolare lato client per chart

```
Responsabilità:
- Ring buffer per-tag con capacità configurabile (es. 10 minuti di punti)
- Mantiene array [timestamps[], values[]] nel formato richiesto da uPlot
- Append efficiente senza riallocazione (circular index)
- Quando il buffer è pieno, scarta i più vecchi
- Opzionale: downsample locale per zoom-out (LTTB algorithm)

Perché serve:
- uPlot vuole array numerici contigui, non oggetti
- Il buffer WS del Bridge tiene i dati grezzi; il client li organizza per la chart
- Permette zoom/pan senza ri-query al server (per i dati già ricevuti)

Stato esposto:
- data: Ref<uPlot.AlignedData>  // [timestamps[], series1[], series2[], ...]
- append(tag, value, timestamp): void
- clear(): void
- timeRange: { from: number, to: number }
```

### `useAlarms` — Allarmi ISA-18.2

```
Responsabilità:
- Mantiene lista allarmi attivi per source
- Aggiorna stato su messaggi push alarm
- Invoca ackAlarm via WS
- Conteggio per stato (active+unack, active+ack, returned+unack)
- Notifica sonora/visiva per nuovi allarmi non-ack

Stato esposto:
- activeAlarms: Ref<AlarmInfo[]>
- unacknowledgedCount: Ref<number>
- acknowledge(source, tag): void
- acknowledgeAll(source): void
```

## Wrapper uPlot per Vue

uPlot non ha un wrapper Vue ufficiale — ne serve uno piccolo. Design:

```
UPlotChart.vue
Props:
  - options: uPlot.Options    // dimensioni, serie, assi, plugins
  - data: uPlot.AlignedData   // [timestamps[], ...values[]]

Behavior:
  - onMounted: crea istanza uPlot nel div container
  - watch(data): chiama uplot.setData(data) (update in-place, no recreate)
  - watch(options.series): se cambiano le serie (add/remove tag), ricrea l'istanza
  - onUnmounted: uplot.destroy()
  - ResizeObserver sul container → uplot.setSize()

Nota: uPlot.setData() è O(1) — non rialloca, aggiorna il canvas.
Questo è il motivo per cui è così veloce per streaming.
```

## Flusso dati: dal WS alla chart

```
Bridge WS                    useBridgeWs              useSubscription
    │                            │                         │
    │── {"type":"d","l":1,       │                         │
    │    "v":[23.5,null,1.02]}──►│ raw message             │
    │                            │──── dispatch ──────────►│
    │                            │                         │ decode layout:
    │                            │                         │   v[0]=23.5 → "temp1"
    │                            │                         │   v[1]=null → skip
    │                            │                         │   v[2]=1.02 → "press1"
    │                            │                         │
    │                            │              useTimeSeriesBuffer
    │                            │                         │
    │                            │                         │── append("temp1", 23.5, ts)
    │                            │                         │── append("press1", 1.02, ts)
    │                            │                         │
    │                            │                    UPlotChart.vue
    │                            │                         │
    │                            │                         │ watch(data) triggered
    │                            │                         │── uplot.setData(data)
    │                            │                         │   (canvas update, <1ms)
```

Flusso per un tick a 50ms con 200 tag:
1. **1 messaggio WS** arriva (compact batch, ~1.2KB)
2. **1 decode** del layout → 200 TagValue
3. **200 append** al buffer circolare (~0.1ms totale)
4. **1 setData** su uPlot (~0.5ms per ridisegnare)
5. **Totale: ~1-2ms** di lavoro per tick → il browser resta fluido a 60fps

## Pagine e funzionalità

### Dashboard (home)

```
┌──────────────────────────────────────────────────────────┐
│  🟢 Connected to DataServer (RTT: 12ms)    [⚠ 2 Alarms] │
├──────────┬───────────────────────────────────────────────┤
│          │                                               │
│ Sources  │  ┌─────────────┐  ┌─────────────┐            │
│          │  │  linea1     │  │  pressa-nord│            │
│ ○ linea1 │  │  12 tags    │  │  6 tags     │            │
│ ○ pressa │  │  🟢 live    │  │  🟢 live    │            │
│          │  │  45k records│  │  12k records│            │
│ ─────── │  └─────────────┘  └─────────────┘            │
│ Pages    │                                               │
│          │  Active Alarms                                │
│ Dashboard│  ┌────────┬──────┬────────┬──────────┐       │
│ Alarms   │  │ Source │ Tag  │ State  │ Action   │       │
│ Archives │  │ linea1 │ temp │ 🔴 NEW │ [ACK]    │       │
│ Query    │  │ linea1 │ press│ 🟡 ACK │          │       │
│ Settings │  └────────┴──────┴────────┴──────────┘       │
│          │                                               │
└──────────┴───────────────────────────────────────────────┘
```

### Source detail

```
┌──────────────────────────────────────────────────────────┐
│  linea1                          [Quality: full] [5h buf]│
├──────────────────────────────────────────────────────────┤
│                                                          │
│  Tag Selector: [✓ temp1] [✓ temp2] [✓ press1] [ flow1]  │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │              Telemetry Chart (uPlot)               │  │
│  │  temp1 ─── 23.5°C                                 │  │
│  │  temp2 ─── 18.2°C                                 │  │
│  │  press1─── 1.02 bar                               │  │
│  │                                                    │  │
│  │  ~~~/\~~~/\~~~~/\~~~~~/\~~~~                       │  │
│  │  ───────────────────────────                       │  │
│  │  ─·─·─·─·─·─·─·─·─·─·─·─·                       │  │
│  │                                                    │  │
│  │  [──────────── time range ────────────]  [10m ▾]  │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│  Events                         Alarms                   │
│  ┌──────────────────────┐       ┌──────────────────────┐ │
│  │ 10:30:01 startButton │       │ 🔴 overtemp (NEW)    │ │
│  │ 10:28:45 cycleEnd    │       │    since 10:29:12     │ │
│  │ 10:25:03 startButton │       │    [ACKNOWLEDGE]      │ │
│  └──────────────────────┘       └──────────────────────┘ │
│                                                          │
│  Buffer Info                                             │
│  ┌──────────────────────────────────────────────────┐    │
│  │ Chunks: 58 (52 full, 3 live, 3 loaded)          │    │
│  │ Range: 09:30 → 14:30 (5h)  Records: 450,000    │    │
│  │ MsgId: 98000 → 103000                           │    │
│  └──────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
```

### Interazioni chart importanti

| Feature | Come funziona |
|---------|--------------|
| **Live streaming** | I dati arrivano via compact push ogni 50ms → append al buffer → setData → aggiornamento fluido |
| **Zoom/Pan** | uPlot supporta zoom nativo con mouse wheel e drag. Per i dati nel buffer locale: istantaneo. Per dati fuori buffer: queryTelemetry al server |
| **Time range presets** | Bottoni "1m, 5m, 10m, 1h, ALL" — cambiano la finestra visibile. Se i dati sono nel buffer locale, istantaneo. Se serve storia oltre il buffer client → query |
| **Add/Remove serie** | TagSelector aggiunge/toglie tag → subscribe/unsubscribe WS → layout update → chart ricreata con nuove serie |
| **Cursor sync** | Più chart nella stessa pagina condividono il cursore (uPlot ha plugin cursor sync nativo) |
| **Quality indicator** | Badge "live/full/mixed" accanto al time range, basato sulla risposta quality delle query |
| **Pause streaming** | Bottone pause: smette di appendere al buffer (i dati continuano ad arrivare via WS ma non aggiornano la chart). Resume: riparte dal live |

## Gestione protocollo compact — lato client

```typescript
// compact-layout.ts

interface CompactLayout {
  source: string
  layoutId: number
  tags: string[]
}

class CompactLayoutTracker {
  // source → layout corrente
  private layouts = new Map<string, CompactLayout>()

  setLayout(layout: CompactLayout) {
    this.layouts.set(layout.source, layout)
  }

  // Decodifica un batch compatto in TagValue[]
  decode(msg: { l: number, ts: string, m: number, v: (number|null)[] }): TagValue[] {
    // Trova il layout per questo layoutId
    for (const layout of this.layouts.values()) {
      if (layout.layoutId !== msg.l) continue

      const values: TagValue[] = []
      for (let i = 0; i < msg.v.length; i++) {
        if (msg.v[i] === null) continue  // nessun aggiornamento
        values.push({
          source: layout.source,
          tag: layout.tags[i],
          value: msg.v[i],
          timestamp: msg.ts,
          msgId: msg.m
        })
      }
      return values
    }
    return []  // layout sconosciuto — skip (potrebbe essere in transito)
  }
}
```

## Performance budget

| Metrica | Target | Note |
|---------|--------|------|
| Bundle size | < 200KB gzipped | Vue3 ~30KB + uPlot ~15KB + PrimeVue tree-shaken ~100KB |
| First paint | < 1s | Vite + code splitting per route |
| Chart update latency | < 2ms per tick | uPlot setData è O(1), il collo di bottiglia è il decode |
| Memoria per chart | ~5MB per 100k punti | Float64Array per timestamps e valori |
| Max tag visualizzati | 20-30 serie per chart | Oltre: sovrapporre troppe linee diventa illeggibile |
| WS message decode | < 0.5ms per batch | JSON.parse + layout lookup |

## Dipendenze npm previste

```json
{
  "dependencies": {
    "vue": "^3.5",
    "vue-router": "^4.4",
    "pinia": "^2.2",
    "uplot": "^1.6",
    "primevue": "^4.0"
  },
  "devDependencies": {
    "vite": "^6.0",
    "@vitejs/plugin-vue": "^5.0",
    "typescript": "^5.5",
    "vue-tsc": "^2.0"
  }
}
```

## Fasi di implementazione suggerite

```
Fase 1 — Fondamenta
├── Scaffold Vue 3 + Vite + TypeScript + PrimeVue
├── useBridgeWs: connessione WS con reconnect
├── connectionStore: stato connessione, source list (getSources)
├── DashboardPage: elenco source cards con stato
├── AppHeader + ConnectionBadge
└── Verifica: connettersi al Bridge, vedere le source

Fase 2 — Chart live
├── UPlotChart.vue: wrapper generico
├── useSubscription: subscribe con compact:true, decode layout
├── useTimeSeriesBuffer: ring buffer per chart data
├── TelemetryChart.vue: chart con streaming live
├── SourcePage: tag selector + chart
└── Verifica: vedere dati live che scorrono in real-time

Fase 3 — Allarmi e eventi
├── useAlarms: stato ISA-18.2, ack via WS
├── AlarmTable + AlarmBanner
├── EventTimeline: lista eventi con timestamp
├── AlarmsPage: vista dedicata
└── Verifica: vedere allarmi scattare, acknowledge, risoluzione

Fase 4 — Query e storico
├── useQuery: queryTelemetry/Events/Alarms via WS
├── TimeRangePicker: selettore intervallo
├── QualityBadge: live/full/loaded/mixed
├── QueryPage: query manuali
├── Zoom/pan su chart con fetch automatico storia
└── Verifica: navigare nello storico, vedere quality cambiare

Fase 5 — Archivi e settings
├── ArchivesPage: list + load file Parquet
├── SettingsPage: setChunkDuration, bridge info
├── useExport: download CSV/Parquet
└── Verifica: caricare archivio, esportare dati
```
