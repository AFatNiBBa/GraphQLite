# TODO

Proposte di miglioramento emerse durante il lavoro e non ancora fatte. Prima di iniziarne una, chiedere conferma all'utente (alcune toccano l'API pubblica). Ogni modifica sul percorso caldo va misurata A/B (vedi `CLAUDE.md`).

## Prestazioni

- **Mappa compatta delle proprietà di `Node` / `Relationship`.** `MATCH (n) RETURN n` con molti nodi è dominato da allocazioni e GC: circa 800 byte per nodo, soprattutto per il `Dictionary` delle proprietà. Con il gather parallelo il tempo migliore è sceso (84–200 → 33–70 ms per 200k nodi), ma la mediana no. Idea: due array paralleli (chiavi / valori), o un array di coppie, con ricerca lineare (le proprietà per nodo sono poche), esposti dietro un `IReadOnlyDictionary`. Tocca un tipo dell'API pubblica.
- **Array delle label condivisi.** Ogni `Node` materializzato alloca il proprio array di label: si potrebbero internare per combinazione di label (stesso set → stessa istanza immutabile).
- **Parallelismo oltre il piano principale.** Oggi `GatherOp` / `SortOp` / `AggregateOp` parallelizzano solo sopra uno scan di nodi (`IPartitionedScan`) e `Planner.Parallelize` agisce solo sul piano principale delle query di sola lettura. Restano sequenziali:
  - scan di relazioni senza nodo di partenza (es. `MATCH ()-[r:T]->() RETURN ...`): servirebbe un `IPartitionedScan` sullo spazio degli id delle relazioni;
  - sottoquery `CALL { }` e rami interni non raggiungibili con `WithInput`;
  - `LIMIT` senza `ORDER BY` (lo scan sequenziale si ferma dopo poche righe: si potrebbe partire sequenziali e passare al gather solo oltre una soglia di righe consumate).
- **Scritture.** Restano su un solo thread (scrittore esclusivo). Un'eventuale parallelizzazione riguarderebbe solo la parte di lettura che precede una barriera `Eager`, calcolata in parallelo e poi applicata in sequenza.

## API

- **Metodi async mirati per le scritture** (`BeginTransactionAsync`, `CommitAsync`, `ExecuteAsync` per le scritture in auto-commit). Gli unici punti in cui un thread resta fermo senza lavorare sono l'attesa del writer gate (`SemaphoreSlim`, ha già `WaitAsync`) e l'attesa dell'fsync del group commit (`Pager.WaitDurable`, oggi `Monitor.Wait`: servirebbe un `TaskCompletionSource` completato dal leader dell'fsync). Con molti scrittori concorrenti in `SyncMode.Full` (es. un'app ASP.NET) i thread bloccati possono affamare il thread pool. L'esecuzione della query in mezzo resta sincrona.
- **Non** rendere async il resto (letture, `Cursor.Next`, operatori): il lavoro è CPU-bound su file mappati, i page fault non sono "awaitabili" e una macchina a stati per riga rallenterebbe il percorso caldo (anche Microsoft.Data.Sqlite ha `*Async` solo di facciata).

## Non da fare (già valutate)

- Neo4j embedded che non scala con i thread nel confronto: è un limite di Neo4j, non nostro.
- Multi-processo sullo stesso database: scelta voluta (vedi `CLAUDE.md`).
