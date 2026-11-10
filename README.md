# GraphDb

Graph database **embedded** (in-process, come SQLite) per .NET 10, interrogabile in **openCypher**.

```csharp
using GraphDb;

using var db = GraphDatabase.Open("social.gdb");          // oppure GraphDatabase.OpenInMemory()

db.Execute("CREATE CONSTRAINT FOR (p:Person) REQUIRE p.name IS UNIQUE");
db.Execute("""
    CREATE (a:Person {name: 'Alice', age: 30})-[:KNOWS {since: 2010}]->(b:Person {name: 'Bob', age: 25})
    """);

var result = db.Execute(
    "MATCH (p:Person)-[:KNOWS]->(f) WHERE p.age > $min RETURN p.name AS name, collect(f.name) AS friends",
    new Dictionary<string, object?> { ["min"] = 18 });

foreach (var row in result)
    Console.WriteLine($"{row.Get<string>("name")}: {row["friends"]}");

// transazioni esplicite
using (var tx = db.BeginTransaction())
{
    tx.Execute("MATCH (p:Person {name: 'Bob'}) SET p.age = p.age + 1");
    tx.Commit();                                          // Dispose senza Commit = rollback
}
```

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/GraphDb` | la libreria |
| `tests/GraphDb.Tests` | test xUnit (semantica Cypher, persistenza, crash recovery, concorrenza) |
| `tools/GraphDb.Shell` | shell interattiva stile `sqlite3` (`dotnet run --project tools/GraphDb.Shell -- file.gdb`) |
| `tools/GraphDb.Bench` | benchmark (`dotnet run -c Release --project tools/GraphDb.Bench -- 200000 5 [file]`) |

```
dotnet build GraphDb.slnx
dotnet test  GraphDb.slnx
```

## Concorrenza

- **Letture concorrenti illimitate**: `db.Execute` di una query di sola lettura (o `BeginReadTransaction`) entra con
  un solo CAS su un lock lettori/scrittore, senza passare dal kernel. Le letture scalano linearmente con i core.
- **Uno scrittore alla volta** (come SQLite): una transazione di scrittura esclude i lettori solo mentre è aperta.
  Il lock è *writer-preferring*, quindi gli scrittori non soffrono di starvation.
- Il checkpoint (snapshot completo) gira **in parallelo ai lettori**: blocca solo gli altri scrittori.
- Il lock non è legato al thread: una transazione può essere chiusa da un thread diverso da quello che l'ha aperta.
- Le letture vedono sempre e solo transazioni committate per intero (isolamento serializzabile).

## Persistenza (modello SQLite WAL)

- `file.gdb` contiene uno snapshot binario; `file.gdb-wal` riceve un frame per ogni commit.
- Ogni frame ha CRC32C calcolato in hardware (`BitOperations.Crc32C`) e un numero di sequenza;
  alla riapertura si carica lo snapshot e si riapplicano i frame più recenti. Una coda troncata
  (crash a metà scrittura) viene scartata.
- Il checkpoint scrive lo snapshot su file temporaneo e lo rinomina atomicamente; è idempotente rispetto
  a un crash prima del troncamento del WAL. Avviene in automatico oltre `AutoCheckpointBytes` (64 MB) e alla chiusura.
- `SyncMode`: `Off` / `Normal` (flush su OS a ogni commit, default) / `Full` (fsync a ogni commit).
- Il file WAL è aperto in esclusiva: un secondo processo riceve "database is locked".

Il grafo è interamente in memoria (come un database SQLite con cache illimitata): la dimensione massima è la RAM.

## Cosa c'è sotto (perché è veloce)

- **Storage a struct-of-arrays**: nodi e relazioni sono `struct` in array contigui; le liste di adiacenza sono
  inline nel nodo e ogni voce contiene già *vicino* e *tipo*, quindi un'espansione non tocca mai il record della relazione.
- **Indice delle label a bitset** con iterazione `TrailingZeroCount`; ricerca delle proprietà con `Span.IndexOf` (SIMD).
- **`Value`** è una struct discriminata da 24 byte: niente boxing per interi, float, booleani, nodi, relazioni.
- **Compilazione delle espressioni in closure** (niente interprete dell'AST a runtime) e operatori **pull a cursore**
  che scrivono in-place su un'unica riga "larga" (`Value[]`): zero allocazioni per riga nei percorsi caldi.
- **Planner cost-based**: sceglie il punto di partenza fra index seek (unique / normale / `IN`), seek per `id()`,
  scan per label (la più piccola) o all-nodes; spinge i filtri al primo punto in cui le variabili sono legate;
  usa `ExpandInto` sui cicli; Top-N con heap per `ORDER BY … LIMIT`; `shortestPath` con BFS bidirezionale.
- **Cache dei piani** per testo della query (invalidata automaticamente dai cambi di schema) e parametri risolti
  per indice, non per nome.
- WAL con serializzazione varint su `Span<byte>`, I/O binario senza stream intermedi.

Risultati indicativi (`tools/GraphDb.Bench`, 200k nodi / 1M relazioni, 24 core, in-memory):

| Operazione | Throughput |
|---|---|
| lookup su indice unique, 1 thread | ~0,6–0,9 M query/s |
| lookup su indice unique, 24 thread | ~8 M query/s |
| 2-hop friends-of-friends, 24 thread | ~1,4 M query/s |
| scan per label + group by | ~10 M righe/s |
| espansione di tutte le relazioni + filtro | ~4 M relazioni/s |
| `shortestPath` (≤ 6 hop) | ~15 k query/s |
| bulk insert relazioni (`UNWIND` + 2 index seek) | ~300 k/s |

## openCypher supportato

**Clausole**: `MATCH`, `OPTIONAL MATCH`, `WHERE`, `RETURN` / `WITH` (`DISTINCT`, `*`, alias, `ORDER BY`, `SKIP`, `LIMIT`),
`UNWIND`, `CREATE`, `MERGE` (`ON CREATE SET` / `ON MATCH SET`), `SET` (`n.p =`, `n = {}`, `n += {}`, `n:Label`),
`REMOVE`, `DELETE`, `DETACH DELETE`, `FOREACH`, `UNION` / `UNION ALL`, `CALL proc() YIELD …`, `EXPLAIN`.

**Pattern**: label multiple, proprietà inline, direzioni `->` `<-` `-`, tipi alternativi `[:A|B]`,
lunghezza variabile `*`, `*n`, `*n..m`, `*..m`, path con nome `p = (…)`, `shortestPath`, `allShortestPaths`,
unicità delle relazioni all'interno di un `MATCH`.

**Espressioni**: aritmetica, confronti concatenati, logica a tre valori (`AND OR XOR NOT`), `IS [NOT] NULL`, `IN`,
`STARTS WITH`, `ENDS WITH`, `CONTAINS`, `=~`, `CASE` (semplice e generico), accesso a proprietà / indici / slice,
list comprehension, pattern comprehension, `all/any/none/single`, `reduce`, map projection `n {.a, .*, k: expr}`,
predicati di pattern `(a)-->(b)`, `EXISTS { … }`, `COUNT { … }`, parametri `$p`.

**Funzioni**: `id elementId labels type keys properties startNode endNode nodes relationships length size head last
tail reverse range isEmpty coalesce exists timestamp randomUUID rand pi e toInteger toFloat toString toBoolean
(+ varianti *OrNull) toUpper toLower trim ltrim rtrim substring left right replace split abs sign ceil floor round
sqrt exp log log10 sin cos tan cot asin acos atan atan2 degrees radians`.
**Aggregazioni**: `count(*) count sum avg min max collect stDev stDevP percentileCont percentileDisc`, anche `DISTINCT`.

**Schema**: `CREATE INDEX [nome] [IF NOT EXISTS] FOR (n:L) ON (n.p)`, `CREATE CONSTRAINT [nome] [IF NOT EXISTS]
FOR (n:L) REQUIRE n.p IS UNIQUE`, `DROP INDEX|CONSTRAINT nome [IF EXISTS]` (anche sintassi legacy `ON :L(p)`).

**Procedure**: `db.labels`, `db.relationshipTypes`, `db.propertyKeys`, `db.indexes`, `db.constraints`, `db.stats`, `dbms.components`.

### Limiti noti

- Niente tipi temporali / spaziali (`date()`, `point()`), niente `CALL { subquery }`, niente `LOAD CSV`.
- Gli indici sono hash (uguaglianza e `IN`); i range (`<`, `>`) usano scan + filtro.
- Le label `:A|B` nei nodi sono trattate come congiunzione (come `:A:B`).
- Singolo processo: un altro processo non può aprire lo stesso file contemporaneamente.
