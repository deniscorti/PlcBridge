# Bridge — Requisiti

## 1. Scopo
Bridge è un servizio che fa da ponte tra uno o più PLC industriali e i sistemi client
(SCADA, MES, dashboard web, applicazioni mobile), esponendo:
- una **REST API HTTP** per operazioni request/response (lettura, scrittura, configurazione);
- un canale **WebSocket** per comandi bidirezionali e streaming push di dati/eventi.

## 2. Requisiti funzionali
| ID    | Requisito |
|-------|-----------|
| RF-01 | Connettersi a uno o più PLC configurabili tramite file di configurazione. |
| RF-02 | Supportare driver multipli ( ADS Backoff, UDP, il bridge deve poter ricevere stream UDP secodno un protocollo da definire, nel caso si voglia implementare nel plc la send di pacchetti udp per notificare lo stato/valore dei vari segnali|— estensibile). |
| RF-03 | Esporre endpoint REST per leggere il valore corrente di un tag. |
| RF-04 | Esporre endpoint REST per scrivere un valore su un tag (con autorizzazione). |
| RF-05 | Esporre endpoint REST per elencare i PLC e i tag disponibili. |
| RF-06 | Esporre un endpoint WebSocket per sottoscrivere variazioni di uno o più tag. |
| RF-07 | Inviare via WebSocket eventi push (cambio valore, allarmi, stato connessione). |
| RF-08 | Ricevere via WebSocket comandi di scrittura/lettura on-demand. |
| RF-09 | Persistere uno storico minimo dei valori (opzionale, configurabile). |
| RF-10 | Riconnessione automatica al PLC in caso di caduta connessione. |
| RF-11 | Dal plc si possono ricevere vari tipi di dati, da trattare in modo diverso
		1- Dati sensori = sono numeri o liste di numeri che indicano i valori campionati e letti dal plc
		2- Dati di tipo evento = ad esempio quando succeder qualcosa, tipo un bottone premuto, e' inutile mandare continuamente il valore 0 o 1
		3- Allarmi = simili agli eventi ma originati da campi particolari nel plc 
		|
| RF-12 | il bridge quando archivia, crea file parquet con nome data e ora e chiave plc, gli archivi automatici sono possono andare in cartella separata. vista nauta mista dei dati, si puo' valutare anche tipi di salvataggio diversi, non solo parquet
		 MEttendo nel nome dei file data e ora, possono essere utilizzati per ricaricare tutti dati in ordine semplicemente caricando i paquet necessari in ordine crescente
| RF-13 | Possibilita' di tenere in memoria i dati degli ultimi x minuti configurabile 
| RF-14 | salvataggio dei dati ricevuti su hard disk in modo da poter ricostruire la storia in caso di necessita'
| RF-15 | Mantenere i dati separati in base alla loro natura, valori  di telemetria, eventi o allarmi e salvare su disco in modo incrementale per avere backup che potranno poi essere caricati in memoria da un altro bridge ma con cnfigurazione differente per servire i client con le stesse API
| RF-16 | Il bridge deve poter agire in differenti configurazioni
		1- Bridge-DataProvider puro legge dati in ads oppure li riceve in udp, li espone o tramite websocket o web api senza fare caching e senza salvare nulla, e invia i comandi ricevuti al plc e ritorna la risposta
		2- Bridge-DataService  oltre a fare il bridge puro, puo' mantenerli in memoria per un certo intervallo in memoria circolare, ad esempio 1 ora (configurabile) e opzioalmente salva su file a intervalli per avere tutti i dati che possono essere ricaricati in seuito dal Bridge-Server-Client
		3- Bridge-DataServer e' una ulteriore configurazione che permette di essere configurato per ricevere i dati dalle configurazioni precedenti, oltre a poter caricare i file salvati dal server-Live
		Il DataService riceve i dati dal DAtaPRovier, o li legge direttamente tramite ADS o li riceve direttamente UDP li salva a frequenza normale, ma li puo inviare a DataServer con downsampling
		DataService e DataSErver devono poter avere un concetto di dataSource o Gruppo che raggruppa i canali, non si possono avere nomi duplicati all'interno del datasource/gruppo, ma si puo' avere stesso nome in gruppi diversi
| RF-17 | I bridge 2 e 3 possono essere configurati per ricevere i comandi dal client e inoltrarli alla rispettiva sorgente, il 3 passa il comando al 2 (o direttamente all 1 ), il 2 passa il comando all 1. mentre 1 esegue il comando, e la risposta deve seguire li stessi steps in direzione opposta. quindi sia 2 che 3 devono agire come bridge per comandi
		PRevedre timeout per ogni elemento della catena in caso di comandi e notifica in modo che il client sappia del timeout o dell'errore
		
| RF-18 | Il Bridge 2 deve poter fornire i dati della telemtria live a una frequenza minore di quella reale al Bridge 3, nessun downsampling per eventi e allarmi
| RF-19 | In futuro il Bridge 3 deve poter recuperare dati dal 2 in caso vengano usati tramite internet, preparare la struttura per aggiungere id ai vari messaggi inviati da 1 a 2 oppure letti direttamente da 2, potrebbe aiutare in futuro per implementare questo punto.
		I dati possono essere recuperati solamente se presenti nel buffer circolare, non considerare i parquet come fonte per il recupero

|RF-20| Il DataProvider salva row data di come li riceve, nel caso udp puo' salvare un file aggiornato ogni x secondi e di durata massima Y dei messaggi esattamente come li riceve
		NEl caso di ADS vedere come e' meglio salvarli (immagino debba generare un id visto che non ne abbiamo mai parlato da usare anche per il flusso normale)
		Questi file sono di backup per recupero di tutto, quindi da prevedere un tool per creare i parquet per dataSErver da questi singoli file, con nome simile al template usato per i parquet 
		

## 3. Requisiti non funzionali
| ID     | Requisito |
|--------|-----------|
| RNF-01 | Eseguibile come Windows Service e come Linux systemd daemon. |
| RNF-02 | Latenza media lettura tag (cache) < 50 ms. |
| RNF-03 | Supporto a >= 1000 sottoscrizioni WebSocket concorrenti. |
| RNF-04 | Logging strutturato (JSON) con livelli configurabili. |
| RNF-05 | Esposizione metriche Prometheus / OpenTelemetry. |
| RNF-06 | Nessuna Configurazione hot-reload. se cambia funzionalita' DataPRovider-SErver o Service si deve riavviare |
| RNF-07 | Autenticazione su API e WS (API key / JWT). |
| RNF-08 | Containerizzabile (Docker). |

## 4. Vincoli tecnici
- .NET 8 (LTS), C# 12.
- ASP.NET Core Minimal API + `System.Net.WebSockets`.
- Hosting tramite `Microsoft.Extensions.Hosting` Worker Service.
- Serilog per logging, OpenTelemetry per metriche/tracing.

| RF-20 | Gestione allarmi con modello a 4 stati (ISA-18.2): attivo/non-ack, attivo/ack, rientrato/non-ack, risolto. Acknowledge dal client via WS o REST, inoltrato nella catena come comando. |
| RF-21 | Export dati on-demand: endpoint REST per scaricare dati filtrati come CSV o Parquet. Download di chunk sealed come file Parquet. |
| RF-22 | Modalità replay (DataServer): il client richiede un intervallo temporale e una velocità, il server re-invia i dati storici come stream push WS rispettando la sequenza temporale originale. |
| RF-23 | Heartbeat inter-bridge: ping/pong periodico con misurazione RTT, riconnessione automatica dopo N pong mancati. |
| RF-24 | Metriche operative esposte via endpoint REST `/metrics` in formato JSON (throughput, RTT, connessioni, stato buffer, disco). |
| RF-25 | Rate limiting per connessione WS: limiti configurabili su max sottoscrizioni, max query/min, max coda messaggi. |
| RF-26 | Info buffer esposte ai client via `getSources` WS e `/status` REST (range temporale disponibile, conteggio chunk e record, msgId range). |
| RF-27 | Trasferimento chunk in background dal DataService al DataServer: i chunk sealed (dati full-fidelity) vengono trasferiti compressi a bassa priorità di banda. Il DataServer sostituisce progressivamente i dati downsampliati con quelli completi. Limite di banda configurabile. |
| RF-28 | Il DataServer risponde sempre immediatamente alle query con i dati che ha (anche downsampliati). Il campo `quality` nella risposta indica la fidelity dei dati. Il chunk transfer in background migliora la qualità progressivamente, senza bloccare le query. |
| RF-29 | Stream live DataService→DataServer via UDP: protocollo binario compatto, porta unica per tutti i DataSource, frammentazione per rispettare MTU, attivabile/disattivabile a runtime. UDP e WS possono coesistere — deduplicazione via msgId. |
| RF-30 | Destinazioni UDP multiple configurabili nel DataService, ciascuna attivabile/disattivabile indipendentemente a runtime via comando REST/WS. |
| RF-31 | Selective live subscription: il DataServer sottoscrive all'upstream solo i canali che i client stanno visualizzando. Gli altri canali arrivano comunque via chunk transfer. La sottoscrizione è dinamica: quando i client aggiungono/tolgono canali, il DataServer propaga il delta all'upstream. Al subscribe di nuovi canali, viene recuperata automaticamente una storia recente (backfill configurabile, default 10 minuti). |
| RF-32 | Compact push protocol: il client può richiedere modalità compatta (subscribe con `compact: true`). Il server risponde con un layout posizionale (array ordinato di tag), poi invia batch periodici (ogni ~50ms) come array di valori nella stessa posizione. null = nessun aggiornamento. Risparmio fino a 13× sulla banda. Layout update automatico al cambio sottoscrizioni. Retrocompatibile: chi non chiede compact riceve i push normali. |
| RF-33 | Chunk transfer con priorità: i chunk più recenti vengono trasferiti prima degli storici, per rendere disponibili i dati recenti il più rapidamente possibile. |
| RF-34 | Input extensibility: architettura a factory pattern per i driver di input. Ogni protocollo (UDP, ADS, Modbus, OPC-UA, ecc.) implementa IDataInput + IDataInputFactory e si registra nell'InputRegistry. Aggiungere un nuovo driver non richiede modifiche al codice di bootstrap. |

## 5. Fuori scope (per ora)
- UI web integrata (verrà fornita separatamente come progetto a sé).
- Time-series DB esterno (InfluxDB, TimescaleDB, ecc.) — lo storage locale su Parquet è in scope.
- Configurazione runtime via UI.
- Recovery automatico dai file Parquet (solo caricamento manuale/esplicito).
- Tag metadata (unità di misura, min/max, descrizione) — valutazione futura.
- Raggruppamento logico dei tag (tag groups) — configurazione manuale lato client.

