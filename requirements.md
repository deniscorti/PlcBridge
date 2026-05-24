# PlcBridge — Requisiti

## 1. Scopo
PlcBridge è un servizio che fa da ponte tra uno o più PLC industriali e i sistemi client
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



## 3. Requisiti non funzionali
| ID     | Requisito |
|--------|-----------|
| RNF-01 | Eseguibile come Windows Service e come Linux systemd daemon. |
| RNF-02 | Latenza media lettura tag (cache) < 50 ms. |
| RNF-03 | Supporto a >= 1000 sottoscrizioni WebSocket concorrenti. |
| RNF-04 | Logging strutturato (JSON) con livelli configurabili. |
| RNF-05 | Esposizione metriche Prometheus / OpenTelemetry. |
| RNF-06 | Configurazione hot-reload dove possibile. |
| RNF-07 | Autenticazione su API e WS (API key / JWT). |
| RNF-08 | Containerizzabile (Docker). |
| RNF-09 | Possibilita' di tenere in memoria i dati degli ultimi x minuti configurabile 
| RNF-10 | salvataggio dei dati ricevuti su hard disk in modo da poter ricostruire la storia in caso di necessita'
| RNF-11 | Mantenere i dati separati in base alla loro natura, valori  di telemetria, eventi o allarmi

## 4. Vincoli tecnici
- .NET 8 (LTS), C# 12.
- ASP.NET Core Minimal API + `System.Net.WebSockets`.
- Hosting tramite `Microsoft.Extensions.Hosting` Worker Service.
- Serilog per logging, OpenTelemetry per metriche/tracing.

## 5. Fuori scope (per ora)
- UI web integrata (verrà fornita separatamente come progetto a sé).
- Storico a lungo termine / time-series DB (delegato a sistemi esterni).
- Configurazione runtime via UI.
- 
