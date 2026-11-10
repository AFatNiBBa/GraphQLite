# GraphDb

Graph database **embedded** (in-process, come SQLite) per .NET 10, interrogabile in **openCypher**.
Il grafo vive in una directory di file memory-mapped e può essere molto più grande della RAM.

```csharp
using GraphDb;

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
```

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/GraphDb` | la libreria |
| `tests/GraphDb.Tests` | test xUnit: semantica Cypher, persistenza, crash recovery, concorrenza, nuove funzionalità |
| `tools/GraphDb.Shell` | shell interattiva stile `sqlite3` (`dotnet run --project tools/GraphDb.Shell -- mydb`) |
| `tools/GraphDb.Bench` | benchmark (`dotnet run -c Release --project tools/GraphDb.Bench -- 200000 5 [directory]`) |

```
dotnet build GraphDb.slnx
dotnet test  GraphDb.slnx
```

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
  bloccata dagli scrittori** (né viceversa). Si possono eseguire letture concorrenti illimitate, mentre gli scrittori
  vengono serializzati.
- Il **checkpoint** (automatico oltre `CheckpointThresholdBytes`, 256 MB, oppure con `db.Checkpoint()`) copia le versioni più
  recenti nei file mappati, li sincronizza su disco e tronca il WAL. Esclude i lettori solo per il tempo della copia in
  memoria.
- **WAL a delta**: per ogni pagina si registrano solo gli intervalli di byte cambiati (o l'immagine intera se è cambiata
  più di metà pagina). I frame hanno CRC32C hardware. In recovery una coda troncata viene scartata. Il replay è
  idempotente byte per byte, quindi è corretto anche dopo un checkpoint interrotto a metà.
- `SyncMode`: `Off`, `Normal` (default) oppure `Full`:
  - con `Normal` il commit fa flush verso il sistema operativo. Dopo un power loss si possono perdere gli ultimi commit,
    ma il database non si corrompe, perché i file dati vengono scritti solo dopo il fsync del WAL;
  - con `Full` ogni commit fa fsync.
- **Atomicità per statement**: una query che fallisce dentro una transazione esplicita viene annullata da sola, e la
  transazione resta utilizzabile.
- `CALL { … } IN TRANSACTIONS OF n ROWS` committa a lotti, così si importano dataset enormi senza tenere tutto in memoria.

## Prestazioni

Le tecniche principali:
- valori come struct da 24 byte, senza boxing;
- espressioni compilate in closure;
- operatori pull che scrivono in-place su un'unica riga;
- planner cost-based (index seek per uguaglianza/IN/range/prefisso, seek per `id()`, scan della label più piccola,
  `ExpandInto` sui cicli, Top-N con heap, `shortestPath` con BFS bidirezionale);
- cache dei piani lock-free;
- lookup delle pagine con tabelle sparse lock-free (due accessi ad array).

Risultati indicativi (`tools/GraphDb.Bench`, 200k nodi / 1M relazioni, 24 core):

| Operazione | In memoria |
|---|---|
| lookup su indice unique, 1 thread | ~0,8 M query/s |
| lookup su indice unique, 24 thread | ~6 M query/s |
| 2-hop friends-of-friends, 24 thread | ~1,3 M query/s |
| scan per label + filtro | ~11 M righe/s |
| espansione di tutte le relazioni + filtro su proprietà | ~10 M relazioni/s |
| `shortestPath` (≤ 6 hop) | ~12 k query/s |
| bulk insert relazioni (`UNWIND` + 2 index seek) | ~120 k/s (su disco ~70 k/s) |

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
  e non un vincolo di prestazioni, quindi qui sono permessi;
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
`dbms.components`.

**Parametri .NET**: primitivi, `string`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
dizionari, liste/array, `JsonElement` / `JsonDocument` / `JsonNode`.

**LOAD CSV**:
- legge file locali (percorso o `file:///`);
- con `DatabaseOptions.CsvImportDirectory` i percorsi sono confinati in una directory;
- `http(s)` va abilitato esplicitamente con `AllowRemoteCsv`.

## Limiti noti

- **Un solo processo** per database: la directory è aperta in esclusiva. Più processi richiederebbero un indice delle
  versioni in shared memory e lock tra processi (come il file `-shm` di SQLite). Si può aggiungere, ma è un pezzo
  di architettura a sé.
- Le date sono limitate agli anni 1–9999 e la precisione temporale è di 100 ns (tick .NET).
- Gli indici coprono una sola proprietà (niente indici compositi o full-text).
- Una transazione di scrittura tiene in memoria le pagine che modifica: per import molto grandi usare
  `CALL { … } IN TRANSACTIONS`.
- Un checkpoint deve attendere la fine dei lettori in corso: le letture lunghissime lo rimandano. In casi estremi il
  checkpoint diventa bloccante quando la memoria delle versioni supera 8× la soglia.
