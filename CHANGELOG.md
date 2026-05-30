# Changelog

Tutte le modifiche notevoli a questo progetto vengono documentate qui.
Il formato segue [Keep a Changelog](https://keepachangelog.com/it/1.1.0/)
e il versionamento [SemVer](https://semver.org/lang/it/).

## [Unreleased]

### Added — Fase 1: DataProvider con MockInput
- Struttura progetto: Bridge.Core, Bridge.Inputs, Bridge.Contracts, Bridge.InterBridge, Bridge.Storage, Bridge.Host
- Modello di dominio: DataSource, Tag, TagValue, DataKind (Telemetry/Event/Alarm), AlarmState (ISA-18.2), BridgeMode
- CRC32 per TagId (4 byte, lookup rapido per protocollo UDP)
- IDataInput interfaccia per driver PLC (StartAsync, StopAsync, ReadTagAsync, WriteTagAsync, OnValue)
- MockInput: genera dati fake (sine wave telemetria, eventi/allarmi casuali)
- SubscriptionBroker: matching per source:tag, source:*, *:*, kind-based
- REST endpoints: /health, /sources, /tags, read/write, /status, /metrics
- WebSocket handler: subscribe, unsubscribe, read, write, ping/pong, getSources, getStatus, query, commands, ackAlarm
- WS messages con JsonPolymorphic discriminator "op"
- Autenticazione unificata: X-Api-Key header, Bearer token, query string apiKey/token
- Rate limiting per connessione WS (query/min, max subscriptions, max queue)
- Strumenti: UdpSimulator, WebClient dashboard
- 13 unit test (DataSource, SubscriptionBroker, MockInput)

### Added — Fase 2: DataService
- Chunk, ChunkedRingBuffer: buffer circolare a chunk temporali con seal/eviction
- ChunkState (Open/Sealed), ChunkQuality (Live/Full/Loaded)
- BufferManager: gestione per-source, SetChunkDuration a runtime
- AlarmService: ISA-18.2 (ProcessValue, Acknowledge, AcknowledgeAll, auto-resolve)
- ParquetStorage: WriteChunkAsync (column-oriented), ReadFileAsync, ListFiles, ResolvePath
- CsvExporter: export dati a stream
- BridgeWsClient: WS client verso upstream con reconnect esponenziale, heartbeat, subscribe, comandi
- Query endpoints: queryTelemetry, queryEvents, queryAlarms, export, chunks
- Archive endpoints: list files, load archive

### Added — Fase 3: Input UDP
- UdpInput: ricezione pacchetti custom-v1, CRC32 tagId reverse lookup
- Supporto tipi: float32, float64, int32, bool, float32[], float64[]

### Added — Fase 4: DataServer
- ChunkTransferService: trasferimento chunk background, coda, metriche (pending/completed/avgMs)

### Added — Fase 5: Features avanzate
- UdpProtocol: header binario 24 byte, WriteHeader/TryReadHeader
- UdpBridgeSender: invio batch con frammentazione, encode valori, per-destination enable/disable
- UdpBridgeReceiver: porta singola, CRC32 mapping, riassemblaggio frammenti, deduplicazione msgId con sliding window

### Added — Selective Live Subscription
- SubscriptionBroker.OnSubscriptionChanged event con delta (added/removed tags)
- UpstreamSubscriptionAggregator: ref-counting per tag, debounce 80ms, "ALL" short-circuit
- Backfill automatico (queryTelemetry ultimi N minuti) quando un tag passa 0→1
- Re-push sottoscrizioni su reconnect WsClient
- BridgeWsClient: SubscribeTagsAsync, UnsubscribeTagsAsync, QueryTelemetryAsync, OnConnected event
- Config: SelectiveSubscription (bool), BackfillMinutes (int)

### Added — Compact Push Protocol
- CompactLayoutManager: gestione layout posizionali per-connessione, layoutId versioning
- CompactBatchAccumulator: accumula valori per ~50ms poi invia batch posizionale
- Subscribe con `compact: true` → risposta con layout {source, layoutId, tags[]}
- Push batch: `{"type":"d", "l":layoutId, "ts":"...", "m":msgId, "v":[val, null, val, ...]}`
- layoutUpdate automatico su cambio sottoscrizioni
- Retrocompatibile: client senza compact ricevono push verbose come prima
- Config: CompactFlushMs (int, default 50)

### Added — Chunk Transfer Priority
- ChunkTransferService riscritta con lista ordinata per ToTs decrescente (chunk più recenti prima)
- Processor singolo con protezione race condition

### Added — Web Client Vue 3
- Scaffolding Vite + Vue 3 + TypeScript + uPlot + Pinia + Vue Router in `clients/web/`
- useBridgeWs: composable WebSocket con connect, reconnect esponenziale, heartbeat, request/response correlation
- useSubscription: subscribe/unsubscribe con supporto trasparente compact + verbose, decode layout automatico
- useTimeSeriesBuffer: ring buffer uPlot-aligned con merge per timestamp, trim automatico, loadHistory
- useQuery: query storiche (queryTelemetry, getChunks) via WebSocket request/response
- connectionStore (Pinia): stato connessione, lista sources, info bridge
- UPlotChart.vue: wrapper uPlot generico con auto-resize (ResizeObserver), setData O(1)
- TagSelector.vue: selezione tag con All/None, chip toggle
- SourceCard.vue, QualityBadge.vue: componenti UI per dashboard
- AppSidebar.vue: navigazione con lista sources dinamica
- StatusBar.vue: stato connessione, RTT, versione
- DashboardPage: griglia source cards
- SourcePage: live chart con selezione tag + Start/Stop Live (compact push)
- QueryPage: query storica con selezione source/tags/range + chart risultati
- ArchivesPage: tabella chunks per source con stato e qualità
- SettingsPage: info connessione
- CompactLayoutTracker (client-side): decode batch compatti type:"d"
- Dark theme industriale, code-split per route, proxy dev verso Bridge
- Build: 0 errori TypeScript, bundle ~103KB app + ~24KB uPlot (gzipped)

### Added — Documentazione testing
- docs/testing.md: guida completa a test automatici, strumenti di sviluppo, scenari E2E
- Documentazione UdpSimulator: formato pacchetto binario, parametri CLI, scenario di test
- Documentazione WebClient (tools): uso, differenze dal client Vue 3
- Lista test suggeriti per produzione con priorità
- Scenario end-to-end a 3 livelli (DataProvider→DataService→DataServer→Vue client)

### Added — Input Extensibility
- IDataInput arricchito: Protocol (string), Capabilities (flag enum), OnConnectionChanged event
- InputCapabilities: Receive, Read, Write, Subscribe, BatchRead, Browse
- IDataInputFactory + IDataInputConfig: factory pattern per creare input da configurazione
- InputRegistry: registro centrale, Create by protocol name
- MockInputFactory, UdpInputFactory, UdpInputConfig, GenericInputConfig
- Program.cs usa InputRegistry (aggiungere protocolli = registrare factory)
