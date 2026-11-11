# GraphQLite

Graph database **embedded** (in-process, come SQLite) per .NET 10, interrogabile in **openCypher**.
Il grafo vive in una directory di file memory-mapped e può essere molto più grande della RAM.

```csharp
using GraphQLite;

using var db = GraphDatabase.Open("social.db");            // una directory; oppure GraphDatabase.OpenInMemory()

db.Execute("CREATE CONSTRAINT FOR (p:Person) REQUIRE p.name IS UNIQUE");
db.Execute("""
    CREATE (a:Person {name: 'Alice', born: date('1990-05-17'), profile: {city: 'Rome', langs: ['it', 'en']}})
           -[:KNOWS {since: datetime('2010-01-01T10:00Z')}]->
           (b:Person {name: 'Bob', home: point({longitude: 12.49, latitude: 41.90})})
    """);

var result = db.Execute(
    "MATCH (p:Person)-[:KNOWS]->(f) WHERE p.profile.city = $city RETURN p.name AS name, collect(f.name) AS friends",
    new Dictionary<string, object?> { ["city"] = "Rome" });

foreach (var row in result)
    Console.WriteLine($"{row.Get<string>("name")}: {row["friends"]}");

using (var tx = db.BeginTransaction())                     // transazione esplicita
{
    tx.Execute("MATCH (p:Person {name: 'Bob'}) SET p.visits = coalesce(p.visits, 0) + 1");
    tx.Commit();                                           // Dispose senza Commit = rollback
}

using (var cursor = db.Stream("MATCH (p:Person) RETURN p.name AS name"))  // righe prodotte mentre si leggono
    while (cursor.MoveNext())
        Console.WriteLine(cursor.Current.Get<string>("name"));
```

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/GraphQLite` | la libreria |
| `src/GraphQLite.Server` | server **Bolt** (`bolt://` / `neo4j://`) da avviare su una directory del database |
| `tests/GraphQLite.Tests` | test xUnit: semantica Cypher, persistenza, crash recovery, concorrenza, nuove funzionalità, server Bolt con il driver ufficiale Neo4j |
| `tools/GraphQLite.Shell` | shell interattiva stile `sqlite3` (`dotnet run --project tools/GraphQLite.Shell -- mydb`) |
| `tools/GraphQLite.Bench` | benchmark (`dotnet run -c Release --project tools/GraphQLite.Bench -- 200000 5 [directory]`) |
| `tools/GraphQLite.Compare` | confronto con Neo4j sulle stesse query: GraphQLite in-process, qualsiasi server Bolt, e (in `neo4j-embedded/`) Neo4j embedded in Java |

```
dotnet build GraphQLite.slnx
dotnet test  GraphQLite.slnx
```

I test del server e `tools/GraphQLite.Compare` usano il pacchetto `Neo4j.Driver`: la libreria e il server non hanno
dipendenze esterne.

## Storage (grafo più grande della RAM)

Un database è una **directory**:

| File | Contenuto |
|---|---|
| `nodes` | record dei nodi a dimensione fissa (48 byte), indirizzati per id |
| `rels` | record delle relazioni (32 byte), indirizzati per id |
| `data` | header, proprietà, blocchi di adiacenza, bitmap delle label, indici B+tree, cataloghi, token |
| `wal` | write-ahead log |

- I file sono paginati (8 KB) e **memory-mapped a segmenti**. I segmenti crescono in modo geometrico (1 MB … 64 MB) e
  non vengono mai rimappati, quindi la crescita del file non invalida le letture in corso. La memoria residente la
  decide il sistema operativo (page cache): il grafo non deve stare in RAM.
- **32 bit**: funziona finché il database entra nello spazio di indirizzamento del processo (~1–1,5 GB).
- **Proprietà**: blob compatti `[chiavi][offset][valori]`. La lettura di una singola proprietà fa una ricerca SIMD
  sulle chiavi direttamente nella pagina mappata e decodifica solo quel valore. I valori oltre 4 KB vanno in catene di
  pagine di overflow (documenti JSON grandi a piacere).
- **Adiacenza**: liste a blocchi per nodo (out/in). Ogni voce contiene relazione, vicino e tipo, quindi
  un'espansione legge pagine contigue senza toccare i record delle relazioni.
- **Label**: un bitmap per label (una pagina ogni 65 536 nodi). Le scansioni fanno AND/OR di parole a 64 bit.
- Lo spazio liberato (blob, pagine, blocchi) viene riutilizzato tramite free list per classe di dimensione.

### Transazioni, concorrenza, durabilità (MVCC a pagine, modello SQLite WAL)

- Lo scrittore modifica **copie private** delle pagine (copy-on-write). Al commit le pagine vengono scritte nel WAL e
  pubblicate come nuove *versioni* in memoria.
- Ogni lettura lavora su uno **snapshot**: vede sempre e solo transazioni committate per intero, e **non viene mai
  bloccata dagli scrittori** (né viceversa). Si possono eseguire letture concorrenti illimitate.
- Due tipi di transazione di scrittura:
  - **esclusive** (`db.BeginTransaction()`, e le query in auto-commit di `db.Execute`): una alla volta, non falliscono
    mai per conflitto. Sono le più veloci quando il lavoro è tutto nel codice dell'applicazione;
  - **concorrenti** (`db.BeginConcurrentTransaction()`): non aspettano gli altri scrittori e non li bloccano, anche se
    restano aperte a lungo (per esempio una transazione interattiva di un client). Lavorano su uno snapshot; al commit,
    se nel frattempo qualcuno ha committato, le loro query vengono **rieseguite** sullo stato più recente e la
    transazione committa solo se ogni query restituisce lo stesso risultato della prima volta. Altrimenti `Commit`
    lancia `TransactionConflictException` e non scrive nulla: la transazione va ripetuta. Il risultato è
    **serializzabile** e non ci sono aggiornamenti persi: due `SET c.v = c.v + 1` concorrenti committano entrambi e
    il contatore sale di 2. Gli id dei nodi creati, l'orologio della query (`datetime()`, `timestamp()`) e i valori
    di `rand()` / `randomUUID()` restano gli stessi nella riesecuzione, quindi un `CREATE … RETURN n` non va in
    conflitto. Una transazione concorrente che ha solo letto non viene mai rieseguita.
- I risultati si possono leggere in **streaming** (`db.Stream` / `tx.Stream` restituiscono un `QueryCursor`): le righe
  vengono prodotte mentre si leggono, con memoria costante anche per miliardi di righe. Iniziare un'altra query nella
  stessa transazione mette da parte le righe non ancora lette; chiudere il cursore prima della fine ferma una lettura,
  mentre una query di scrittura viene comunque completata. Con `db.Stream` una query di scrittura tiene il turno di
  scrittura finché il cursore non viene chiuso.
- Il **checkpoint** (automatico oltre `CheckpointThresholdBytes`, 256 MB, oppure con `db.Checkpoint()`) copia le versioni più
  recenti nei file mappati, li sincronizza su disco e tronca il WAL. Esclude i lettori solo per il tempo della copia in
  memoria.
- **WAL a delta**: per ogni pagina si registrano solo gli intervalli di byte cambiati (o l'immagine intera se è cambiata
  più di metà pagina). I frame hanno CRC32C hardware su intestazione e corpo. In recovery una coda troncata o
  incoerente viene scartata. Il replay è
  idempotente byte per byte, quindi è corretto anche dopo un checkpoint interrotto a metà.
- `SyncMode`: `Off`, `Normal` (default) oppure `Full`:
  - con `Normal` il commit fa flush verso il sistema operativo. Dopo un power loss si possono perdere gli ultimi commit,
    ma il database non si corrompe, perché i file dati vengono scritti solo dopo il fsync del WAL;
  - con `Full` un commit ritorna solo dopo il fsync del WAL. I commit che arrivano insieme condividono un solo fsync
    (**group commit**: gli scrittori accodano i frame in memoria e il primo che aspetta li scrive e sincronizza tutti),
    e gli altri vedono un commit solo quando è durevole.
- **Atomicità per statement**: una query che fallisce dentro una transazione esplicita viene annullata da sola, e la
  transazione resta utilizzabile.
- `CALL { … } IN TRANSACTIONS OF n ROWS` committa a lotti, così si importano dataset enormi senza tenere tutto in memoria.

## Server (Bolt)

`GraphQLite.Server` espone una directory del database con il protocollo **Bolt**, lo stesso di Neo4j: i driver ufficiali
(.NET, Java, Python, JavaScript, Go…) e gli strumenti che li usano si collegano con `bolt://` o `neo4j://` senza
modifiche, quindi si può passare da Neo4j a GraphQLite (e viceversa) cambiando solo l'indirizzo. Anche **Neo4j Browser**
si collega (vedi sotto).

```
dotnet run -c Release --project src/GraphQLite.Server -- mydb
dotnet run -c Release --project src/GraphQLite.Server -- mydb --host 0.0.0.0 --port 7687 --user neo4j --password secret
dotnet run -c Release --project src/GraphQLite.Server -- mydb --tls-self-signed
```

| Opzione | Significato |
|---|---|
| `--host` | indirizzo di ascolto (default `127.0.0.1`; `0.0.0.0` per tutte le interfacce) |
| `--port` | porta (default 7687) |
| `--user` / `--password` | autenticazione basic obbligatoria (default: nessuna autenticazione) |
| `--database` | nome del database esposto (default `neo4j`) |
| `--advertised` | indirizzo restituito nelle tabelle di routing di `neo4j://` |
| `--sync` | `off` / `normal` / `full` (vedi `SyncMode`) |
| `--tls-cert` / `--tls-key` / `--tls-password` | abilita TLS con un certificato `.pfx`/`.p12` oppure PEM (certificato + chiave) |
| `--tls-self-signed` | abilita TLS con un certificato self-signed, creato al primo avvio e salvato come `tls-selfsigned.pfx` nella directory del database |
| `--tls-required` | rifiuta le connessioni non cifrate (altrimenti la stessa porta accetta entrambe) |

Il server si può anche incorporare in un'applicazione: `new BoltServer(db, new BoltServerOptions { ... }).Start()`.

Cosa supporta:
- Bolt **4.4** e **5.0–5.4** su TCP, **TLS** (`bolt+s://`, `neo4j+s://`, e `+ssc` per i certificati self-signed) e
  **WebSocket** (`ws://` / `wss://`, usati da Neo4j Browser), tutti sulla stessa porta come in Neo4j. Una richiesta
  HTTP normale riceve il documento di discovery di Neo4j (`bolt_direct`, `bolt_routing`, versione);
- autenticazione (`HELLO` / `LOGON` / `LOGOFF`), query in auto-commit, transazioni esplicite (`BEGIN` / `COMMIT` /
  `ROLLBACK`, anche le transazioni gestite `ExecuteRead` / `ExecuteWrite` dei driver), `RESET`, access mode di lettura
  (le scritture vengono rifiutate);
- **streaming**: le righe vengono prodotte mentre il client le chiede (`PULL` con fetch size) e inviate a blocchi di
  64 KB, quindi una query che restituisce milioni di righe usa memoria costante sul server;
- **scritture concorrenti**: le transazioni esplicite di scrittura sono transazioni concorrenti (vedi sopra), quindi i
  client non si aspettano a vicenda neanche se una transazione resta aperta a lungo. Un conflitto viene segnalato al
  `COMMIT` con `Neo.TransientError.Transaction.Outdated`, che i driver ritentano da soli nelle transazioni gestite
  (`ExecuteWrite`). Le query di scrittura in auto-commit sono esclusive (non vengono mai rieseguite);
- `neo4j://`: il server risponde a `ROUTE` indicando sé stesso come writer, reader e router;
- tutti i tipi: nodi, relazioni e path (con element id `n:<id>` / `r:<id>`, gli stessi di `elementId()`), liste e mappe
  annidate, tipi temporali, durate e punti, sia nei risultati sia nei parametri;
- errori con i codici di Neo4j (`Neo.ClientError.Statement.SyntaxError`, `…Schema.ConstraintValidationFailed`, …),
  quindi la logica di retry dei driver funziona come con Neo4j;
- le query di amministrazione che gli strumenti inviano al database `system`: `CALL dbms.components()`,
  `SHOW DATABASES`, `CALL dbms.showCurrentUser()` / `SHOW CURRENT USER`, `CALL dbms.clientConfig()`,
  `SHOW PROCEDURES` / `SHOW FUNCTIONS`, `CALL dbms.info()`, `SHOW [tipo] INDEXES` / `SHOW CONSTRAINTS` (con `YIELD`).
  Il server si presenta come Neo4j 5.26 community.

### Neo4j Browser

Neo4j Browser (per esempio <https://browser.neo4j.io>) si collega con i WebSocket. Una pagina servita in https accetta
solo connessioni cifrate con un certificato di cui il browser si fida:

1. avviare il server con TLS: `--tls-self-signed` (oppure `--tls-cert` con un certificato vero);
2. con un certificato self-signed, aprire una volta `https://localhost:7687` e accettare il certificato (in alternativa
   aggiungerlo ai certificati attendibili del sistema);
3. in Neo4j Browser collegarsi a `neo4j+s://localhost:7687` (il server stampa il link già pronto).

Una copia di Neo4j Browser servita in http (per esempio da Neo4j Desktop) può usare anche `neo4j://` senza TLS.
Provato con l'app Query/Browser di <https://browser.neo4j.io> (2025): connessione, query, grafo, pannello dello schema e
ordinamento delle label (*Update styling priority*).

Limiti del server:
- un solo database per processo (più il database `system`, virtuale): Neo4j Enterprise ne gestisce più di uno nello
  stesso server (`CREATE DATABASE`), qui per servire più database si avviano più server su porte diverse;
- utenti e ruoli non esistono: c'è al massimo un utente (`--user` / `--password`) con tutti i permessi;
- una query di scrittura in auto-commit tiene il turno di scrittura mentre il client legge il suo risultato.

## Prestazioni

Le tecniche principali:
- valori come struct da 24 byte, senza boxing;
- espressioni compilate in closure;
- operatori pull che scrivono in-place su un'unica riga;
- planner cost-based (index seek per uguaglianza/IN/range/prefisso, seek per `id()`, scan della label più piccola,
  `ExpandInto` sui cicli, Top-N con heap, `shortestPath` con BFS bidirezionale);
- **aggregazioni parallele**: un'aggregazione sopra una scansione di nodi grande (≥ 16k righe per thread) divide lo
  spazio degli id in intervalli, li aggrega su più thread e unisce i risultati nell'ordine degli intervalli, quindi il
  risultato è identico a quello sequenziale (a parte l'arrotondamento delle somme di float). Vale per le transazioni di
  sola lettura; `DatabaseOptions.MaxDegreeOfParallelism` (default: numero di core, `1` la disattiva);
- cache dei piani lock-free;
- lookup delle pagine con tabelle sparse lock-free (due accessi ad array);
- pool delle pagine da 8 KB: la copy-on-write non passa dall'heap nativo a ogni pagina toccata;
- lettori contati su contatori separati per core (ognuno sulla sua cache line): le letture parallele non si contendono
  una variabile condivisa (+20–30% di lookup a 24 thread rispetto a un contatore unico).

Con le query parallele conviene il **Server GC** (`<ServerGarbageCollection>true</ServerGarbageCollection>` nel
progetto dell'applicazione): le aggregazioni con molti gruppi allocano da più thread e con il GC workstation vanno
circa a metà velocità. Il benchmark e il server lo usano già.

Risultati indicativi (`tools/GraphQLite.Bench`, 200k nodi / 1M relazioni, 16 core; le misure oscillano tra un'esecuzione
e l'altra):

| Operazione | In memoria | Su disco |
|---|---|---|
| lookup su indice unique, 1 thread | ~1,1 M query/s | ~1,1 M query/s |
| lookup su indice unique, 16 thread | ~11 M query/s | ~11 M query/s |
| 2-hop friends-of-friends, 1 thread | ~120 k query/s | ~120 k query/s |
| 2-hop friends-of-friends, 16 thread | ~1,2–2 M query/s | ~1,2 M query/s |
| scan per label + filtro / group by | ~50–60 M righe/s | ~55–60 M righe/s |
| espansione di tutte le relazioni + filtro su proprietà | ~90 M relazioni/s | ~90 M relazioni/s |
| grado entrante + top-5 (200k gruppi) | ~13 M relazioni/s | ~15 M relazioni/s |
| `shortestPath` (≤ 6 hop) | ~13 k query/s | ~14 k query/s |
| bulk insert relazioni (`UNWIND` + 2 index seek) | ~470 k/s | ~250 k/s |
| update in transazioni da un singolo statement | ~140 k/s | ~60 k/s |

Attraverso il server Bolt, con il driver ufficiale .NET sulla stessa macchina: ~14 k query/s con un client e
~115 k query/s con 32 client (lookup e 2-hop). Il limite è il costo del protocollo e del driver: durante la misura il
server usa circa il 10% della CPU.

### Confronto con Neo4j

Misurato con `tools/GraphQLite.Compare` contro Neo4j 2026.02.2 Enterprise (istanza di Neo4j Desktop con la memoria di
default, e lo stesso Neo4j embedded in Java), stessi dati e stesse query, tutto su disco, sulla stessa macchina
(i7-13700, 24 thread). Per gli update i due database fanno fsync a ogni commit (`SyncMode.Full`).

| Operazione | GraphQLite in-process | Neo4j embedded | GraphQLite.Server | Neo4j server |
|---|---|---|---|---|
| lookup su indice unique, 1 client | ~780 k/s | ~40 k/s | ~3,6–8 k/s | ~1 k/s |
| lookup, molti client (24 / 32) | ~3,1 M/s | ~38 k/s | ~25–53 k/s | ~6,5 k/s |
| 2-hop, 1 client | ~127 k/s | ~32 k/s | ~3,2 k/s | ~1 k/s |
| 2-hop, molti client | ~750 k/s | ~35 k/s | ~22–24 k/s | ~6,4 k/s |
| `shortestPath` | ~19 k/s | ~11 k/s | ~2,7 k/s | ~0,8 k/s |
| scan per label + group by | 5,7 ms | 18 ms | 4,4 ms | 40 ms |
| espansione di 1M relazioni + filtro | 33 ms | 110 ms | 18–20 ms | 257 ms |
| grado entrante + top-5 (200k gruppi) | 20–42 ms | 117 ms | 30 ms | 268 ms |
| update con fsync, 1 client | ~1,8 k/s | ~1,1 k/s | ~1,0 k/s | ~0,5 k/s |
| update con fsync, molti client | ~13,5–15,5 k/s | ~11–15,5 k/s | ~11,7 k/s | ~5,6 k/s |

- Nelle letture di Neo4j embedded il throughput non cresce con i thread (anche con Community): la causa non è stata
  indagata, quindi il divario con molti client va preso con cautela.
- Via Bolt il costo fisso di ogni query pesa di più: un `RETURN 1` costa ~0,28 ms con GraphQLite.Server e ~1,07 ms con
  Neo4j, con lo stesso driver.
- Il runtime parallelo di Neo4j Enterprise (`CYPHER runtime=parallel`) migliora le query analitiche via server del
  10–40%, senza cambiare il quadro.
- Con il group commit gli update con fsync e molti client in-process sono alla pari.

## openCypher supportato

**Clausole**:
- `MATCH`, `OPTIONAL MATCH`, `WHERE`;
- `RETURN` / `WITH` (`DISTINCT`, `*`, alias, `ORDER BY`, `SKIP`, `LIMIT`);
- `UNWIND`, `CREATE`, `MERGE` (`ON CREATE SET` / `ON MATCH SET`);
- `SET` (`n.p =`, `n = {}`, `n += {}`, `n:Label`), `REMOVE`, `DELETE`, `DETACH DELETE`;
- `FOREACH`, `UNION` / `UNION ALL`;
- `CALL proc() YIELD …`;
- **`CALL { … }`** e **`CALL (x, y) { … }`** (anche con `UNION` interno), **`IN TRANSACTIONS [OF n ROWS]`**;
- **`LOAD CSV [WITH HEADERS] FROM … AS row [FIELDTERMINATOR …]`**;
- `EXPLAIN`.

**Pattern**:
- direzioni `->` `<-` `-` e tipi alternativi `[:A|B]`;
- lunghezza variabile `*`, `*n`, `*n..m`;
- path con nome, `shortestPath`, `allShortestPaths`;
- unicità delle relazioni all'interno di un `MATCH`;
- **espressioni sulle label**: `:A|B` (OR), `:A&B` / `:A:B` (AND), `:!A` (NOT), `:%` (qualunque label), parentesi.

**Espressioni**:
- aritmetica, confronti concatenati, logica a tre valori;
- `IS [NOT] NULL`, `IN`, `STARTS WITH`, `ENDS WITH`, `CONTAINS`, `=~`, `CASE`;
- proprietà annidate (`n.doc.address.city`), indici e slice;
- list comprehension e pattern comprehension;
- `all/any/none/single`, `reduce`, map projection;
- `EXISTS { … }`, `COUNT { … }`, parametri.

**Tipi di valore** (anche come proprietà):
- null, booleani, interi a 64 bit, float, stringhe;
- **liste e mappe annidate a piacere** (campi JSON). Neo4j accetta solo array di primitivi, ma è una scelta di design
  e non un vincolo di prestazioni, quindi qui sono permessi. Si possono anche modificare per percorso:
  `SET n.doc.address.city = 'Roma'` aggiorna solo quella chiave (le mappe intermedie mancanti vengono create),
  `SET n.doc.tmp = null` e `REMOVE n.doc.tmp` la tolgono;
- **`date`, `localtime`, `time`, `localdatetime`, `datetime`** (con offset o fuso IANA), **`duration`**;
- **`point`** (cartesiano 2D/3D, WGS-84).

**Funzioni**:
- scalari, stringhe, liste e matematiche (vedi `Functions.cs`);
- temporali: costruttori da stringa ISO-8601, mappa o altro temporale; `.truncate`, `.realtime/.statement/.transaction`;
  `datetime.fromEpoch[Millis]`; `duration.between/inMonths/inDays/inSeconds`; accessori (`d.year`, `dt.epochMillis`,
  `du.hours`, …); aritmetica temporale ± durata e durata × numero;
- spaziali: `point`, `point.distance` (haversine per WGS-84), `point.withinBBox`;
- JSON: `toJson`, `fromJson` (alias `apoc.convert.toJson/fromJsonMap/fromJsonList`), `valueType`.

**Aggregazioni**: `count`, `sum`, `avg`, `min`, `max`, `collect`, `stDev`, `stDevP`, `percentileCont`,
`percentileDisc`, tutte anche con `DISTINCT`.

**Schema**:
- `CREATE INDEX [nome] [IF NOT EXISTS] FOR (n:L) ON (n.p)`;
- `CREATE CONSTRAINT … REQUIRE n.p IS UNIQUE`;
- `DROP INDEX|CONSTRAINT nome [IF EXISTS]`.

Gli indici sono **B+tree** su disco con chiavi order-preserving. Servono uguaglianza, `IN`, range (`<`, `<=`, `>`, `>=`,
anche combinati) e `STARTS WITH`, su numeri, stringhe, booleani e temporali.
Gli indici **unique** (i constraint) hanno in più un **indice hash** (linear hashing su bucket da 1 KB), usato per
uguaglianza e `IN` e per verificare il vincolo: un lookup costa circa un terzo della discesa nel B+tree.

**Procedure**: `db.labels`, `db.relationshipTypes`, `db.propertyKeys`, `db.indexes`, `db.constraints`, `db.stats`,
`db.schema.visualization` (lo schema come grafo: un nodo virtuale per label e una relazione per ogni combinazione
label–tipo–label; legge tutte le relazioni), `dbms.components`. Una `CALL` può anche chiudere la query da sola, con o
senza `YIELD` (`CALL db.labels() YIELD label`).

**Parametri .NET**: primitivi, `string`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
dizionari, liste/array, `JsonElement` / `JsonDocument` / `JsonNode`.

**LOAD CSV**:
- legge file locali (percorso o `file:///`);
- con `DatabaseOptions.CsvImportDirectory` i percorsi sono confinati in una directory;
- `http(s)` va abilitato esplicitamente con `AllowRemoteCsv`.

## Limiti noti

- **Un solo processo** per database: la directory è aperta in esclusiva. Più processi richiederebbero un indice delle
  versioni in shared memory e lock tra processi (come il file `-shm` di SQLite). Si può aggiungere, ma è un pezzo
  di architettura a sé. Per condividere un database tra più processi o macchine si usa `GraphQLite.Server`.
- Le transazioni concorrenti rieseguono le query al commit se nel frattempo altri hanno committato: con molti
  scrittori che toccano gli stessi dati costano più delle esclusive e possono andare in conflitto ripetutamente.
  Gli id dei nodi e delle relazioni delle transazioni annullate o in conflitto non vengono riutilizzati.
- Le query parallele riguardano solo le aggregazioni sopra una scansione di nodi: le query che restituiscono molte righe
  senza aggregare, e le transazioni di scrittura, restano su un solo thread.
- Le date sono limitate agli anni 1–9999 e la precisione temporale è di 100 ns (tick .NET).
- Gli indici coprono una sola proprietà (niente indici compositi o full-text).
- Una transazione di scrittura tiene in memoria le pagine che modifica: per import molto grandi usare
  `CALL { … } IN TRANSACTIONS`.
- Un checkpoint deve attendere la fine dei lettori in corso: le letture lunghissime lo rimandano. In casi estremi il
  checkpoint diventa bloccante quando la memoria delle versioni supera 8× la soglia.
