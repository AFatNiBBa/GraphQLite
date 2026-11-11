# GraphQLite

Graph database **embedded** (in-process, come SQLite) per .NET 10, interrogabile in **openCypher**.
Obiettivo dichiarato dall'utente: **il più veloce possibile**, sfruttando le funzionalità moderne di .NET (`Span`, `unsafe`, SIMD, memory-mapped file), con **letture concorrenti illimitate** e grafi più grandi della RAM.
Il `README.md` (in italiano) descrive funzionalità, prestazioni e limiti per l'utente finale: tenerlo allineato quando cambiano.
Il progetto si chiamava **GraphDb** (rinominato in GraphQLite su richiesta dell'utente; la directory del repository è ancora `gdb`). Restano col vecchio nome, apposta, il magic su disco `"\0GRPGDB3"` in `Header` (cambiarlo renderebbe illeggibili i database esistenti) e il prefisso `GB:` dei bookmark Bolt.

## Stack
- .NET 10, C# `latest`, `AllowUnsafeBlocks`, `IsAotCompatible`, `InvariantGlobalization`, nessuna dipendenza esterna nella libreria
- Soluzione in formato **`.slnx`** (`GraphQLite.slnx`): niente `.sln` (richiesta esplicita dell'utente)
- Test con xUnit (`InternalsVisibleTo GraphQLite.Tests`)

## Comandi
- `dotnet build GraphQLite.slnx -c Release` (deve restare a 0 warning)
- `dotnet test GraphQLite.slnx -c Release`
- `dotnet run -c Release --project tools/GraphQLite.Bench -- 200000 5 [directory]` → benchmark (senza directory: in memoria)
- `dotnet run --project tools/GraphQLite.Shell -- mydb` → REPL stile `sqlite3` (`-c "query"` per una sola query)
- `dotnet run -c Release --project src/GraphQLite.Server -- mydb [--port 7687] [--user u --password p]` → server Bolt
- Profiling: `dotnet-trace` (tool globale, in `%USERPROFILE%\.dotnet\tools`, non sempre nel PATH: `dotnet tool install -g dotnet-trace` se manca); formato speedscope analizzabile con uno script PowerShell che somma tempo self/inclusivo per frame. Nel formato speedscope il tempo self finisce nello pseudo-frame `CPU_TIME`: va attribuito al frame sottostante. I thread worker dei `Parallel.For` non hanno i frame del chiamante nello stack
- Il benchmark esegue le query analitiche 5 volte e tiene la migliore, ma non arrivano al tier-1 del JIT: per misure stabili su una singola query serve un harness che la ripeta per secondi

## Struttura
- `src/GraphQLite/Storage`:
  - `Pager.cs`: `SegmentedSpace` (mmap a segmenti), `PageVersion` / `PageTable<T>` (overlay MVCC), `Pager`, `WriteTx` (copy-on-write, savepoint), `Wal`
  - `Layout.cs`: `Header` (256 byte), `NodeRec` (48 byte), `RelRec` (32 byte), `AdjBlock`, helper dei blob
  - `GraphStore.cs`: `IndexDef`, `Catalog`, `GraphStore`, `StoreTx` (tutto l'accesso ai record e tutte le mutazioni)
  - `BTree.cs`: chiavi order-preserving (`IndexKeys`) e B+tree
  - `HashIndex.cs`: indice hash (linear hashing) affiancato al B+tree degli indici unique
  - `RwLock.cs`: lock lettori/scrittore usato **solo** per l'esclusività del checkpoint. I lettori stanno su contatori per core distanti 128 byte (un contatore unico costava circa un terzo del throughput a 24 thread); un lettore può decrementare un contatore diverso da quello incrementato (cambio di core), quindi conta solo la somma. Ordine Dekker: lettore incrementa poi legge il flag, checkpoint alza il flag poi somma; lo stress test `RwLock_Never_Lets_A_Writer_In_With_Readers_Inside` deve restare verde
- `src/GraphQLite/Values`: `Value` (struct da 24 byte), `ValueOps` (uguaglianza, hash, ordinamento), `Temporal`, `Json`, `Entities` (`Node`, `Relationship`, `GraphPath`)
- `src/GraphQLite/Cypher`: `Lexer`, `Parser`, `Ast`, `Planner`, `ExprCompiler` (espressioni → closure)
- `src/GraphQLite/Runtime`: operatori pull (`ReadOperators`, `WriteOperators`, `LoadCsv`), `Functions`, `Aggregation`, `Procedures`, `Core` (`ExecContext`)
- `GraphDatabase.cs` (apertura, opzioni, cache dei piani, checkpoint), `Transaction.cs`, `QueryResult.cs`
- `GraphDatabase.cs` / `Transaction.cs` / `QueryCursor.cs`: `Execute` materializza passando da `QueryCursor` (streaming); `StatementLog` registra le query delle transazioni concorrenti
- `src/GraphQLite.Server/Bolt`: server Bolt 4.4 / 5.0–5.4 (`PackStream`, `BoltValues` per la conversione dei valori, `BoltConnection` con la macchina a stati, `BoltServer`, `Transport` che riconosce TLS / Bolt / WebSocket / HTTP sulla stessa porta, `SystemQueries` per le query del database `system` usate da Neo4j Browser, `TlsCertificates`); `Program.cs` è la CLI. Accede agli internal della libreria (`InternalsVisibleTo`)

## Architettura (da sapere prima di toccare lo storage)
- Un database è una **directory** con `data`, `nodes`, `rels` (pagine da 8 KB) e `wal`. `GraphDatabase.OpenInMemory()` usa segmenti di memoria nativa al posto dei file
- I segmenti crescono in modo geometrico (1, 1, 2 … 32 MB, poi 64 MB) e **non vengono mai rimappati**: i puntatori alle pagine del file restano validi finché il database è aperto. All'apertura si mappano i file per intero
- **MVCC a pagine**: lo scrittore lavora su copie private (`WriteTx`); al commit le pagine vanno nel WAL (solo i delta) e vengono pubblicate come catene `PageVersion` nell'overlay. Un lettore legge la versione con `Seq <= snapshot`, altrimenti la pagina del file. Le versioni vecchie si liberano **solo al checkpoint**
- Checkpoint: `RwLock` esclusivo → `MergeOverlay` (copia le versioni più recenti nel file mappato) → flush dei file → troncamento del WAL. Automatico oltre `CheckpointThresholdBytes` (256 MB), bloccante oltre 8×
- Scrittori esclusivi serializzati da un `SemaphoreSlim` (writer gate), tenuto per tutta la transazione; i lettori non vengono mai bloccati dallo scrittore, solo (brevemente) dal checkpoint
- Transazioni **concorrenti** (`BeginConcurrentTransaction`, usate dal server per le transazioni esplicite di scrittura): `WriteTx` con snapshot (legge `Seq <= snapshot`), tengono il read lock del `RwLock` come i lettori. Al commit rilasciano il read lock **prima** di prendere il gate (altrimenti deadlock con un checkpoint urgente), poi: nessuna pagina sporca → niente; `CommittedSeq == Snapshot` → pubblicano; altrimenti scartano le pagine e **rieseguono** le query registrate (`StatementLog`: testo, parametri, orologio, seed, righe lette, hash `ResultHash` di righe e statistiche) in una transazione esclusiva, e committano solo se ogni esito coincide, altrimenti `TransactionConflictException` (Bolt: `Neo.TransientError.Transaction.Outdated`)
- Gli id di nodi e relazioni vengono da contatori atomici in `GraphStore` (`NextNodeId` / `NextRelId`, inizializzati da `NodeHigh` / `RelHigh` all'apertura), non dall'header: le transazioni concorrenti non si scontrano e la riesecuzione riusa gli id riservati (`ReservedNodes` → `ReplayNodes`). L'header tiene `NodeHigh = max`. Gli scrittori esclusivi restituiscono gli id di uno statement o di una transazione annullati se nessuno ne ha riservati dopo (`IdRun`), così restano densi
- Orologio e seed casuale di uno statement stanno in `ExecContext` (`Now`, `Seed`, letti in modo pigro): ogni funzione temporale o casuale deve usarli, mai `DateTime.UtcNow` / `Random.Shared`, altrimenti la riesecuzione darebbe risultati diversi e falsi conflitti
- WAL: frame `[magic 4][n. pagine 4][seq 8][crc 4][lunghezza corpo 4]` + corpo (per pagina: chiave `spazio<<56 | pagina`, poi immagine intera o intervalli `[offset][len][byte]`). Il formato attuale è `WAL4` (CRC su tutta l'intestazione); i frame `WAL3` (CRC senza il numero di pagine) vengono ancora riapplicati, e ogni corpo viene validato prima dell'applicazione
- **Group commit** (solo `SyncMode.Full`): `Pager.CommittedSeq` è l'ultimo commit scritto (ci lavorano gli scrittori esclusivi e il controllo dei conflitti), `Pager.VisibleSeq` l'ultimo durevole (snapshot dei lettori e delle transazioni concorrenti). Sotto il gate il frame va solo in un buffer in memoria del `Wal`; dopo aver rilasciato il gate il commit chiama `Pager.WaitDurable(seq)`: il primo che aspetta fa da leader, scrive tutto il buffer con una sola scrittura e un solo fsync. I frame non si scrivono sul file durante un fsync perché su Windows `FlushFileBuffers` blocca le scritture concorrenti (con le scritture dirette i commit tornavano in fila, ~1,5k/s a qualunque numero di thread). Ogni `Pager.Commit` deve essere seguito da `WaitDurable`, altrimenti il commit resta invisibile (vale anche per i lotti di `IN TRANSACTIONS`, che aspettano subito). Una transazione esclusiva legge anche commit non ancora durevoli: prima di consegnare una riga al chiamante aspetta che lo diventino (`Transaction.BeforeExposingRows`, chiamata da `QueryCursor`); non serve per l'auto-commit di `Execute`, che restituisce le righe dopo il proprio commit (`QueryCursor.ExposesRows = false`), né per la riesecuzione delle transazioni concorrenti
- Il `Wal` usa `SafeFileHandle` + `RandomAccess` (scritture posizionali), non `FileStream`: accodamento e fsync avvengono su thread diversi. `SyncMode.Off` ormai equivale a `Normal`
- Recovery: replay dei frame del WAL validati da CRC32C; il replay è idempotente byte per byte, quindi corretto anche dopo un checkpoint interrotto
- Indici: B+tree con chiavi `[tag][valore][id nodo 8 byte big-endian]`; il planner tiene **sempre** il predicato originale come filtro sopra al seek (le stringhe nelle chiavi sono troncate a 900 byte)
- Gli indici unique hanno anche un `HashIndex` (`IndexDef.HashSlot`, flag nel catalogo; i cataloghi senza flag restano validi e usano solo il B+tree). Salva solo (hash a 64 bit, id), non i valori: i risultati sono **candidati** da verificare (filtro del planner, `FindConflict`). `HashIndex.HashOf` è persistito su disco: non va mai cambiato. Usato per `Equal`/`In` nel seek e per i controlli di unicità; range, prefissi e backfill restano sul B+tree
- Il blob del catalogo non viene mai liberato: il suo indirizzo è univoco e fa da chiave per la cache dei piani
- Aggregazioni parallele (`AggregateOp`): se l'input è una catena di operatori con `RowLocal == true` che parte da uno scan `IPartitionedScan` sopra `StartOp`, nelle transazioni di sola lettura lo spazio degli id viene diviso in blocchi; ogni worker ha un proprio `ExecContext` (`ForPartition`) e lo scan legge l'intervallo con `ctx.PartitionOf(this)`. I parziali si uniscono in ordine di blocco con `Aggregator.Merge`, così gruppi, snapshot e ordine di `collect()` coincidono con l'esecuzione sequenziale
- Le pagine private (copy-on-write, overlay, recovery) si allocano con `Pages.Alloc` / `Pages.Free`, che usano un pool: mai `NativeMemory` direttamente

## Trappole (invarianti da rispettare)
- **I cursori tengono indirizzi, non puntatori, tra una `Next()` e l'altra**: con `CALL { } IN TRANSACTIONS` può avvenire un checkpoint a metà query e i puntatori all'overlay diventerebbero invalidi
- Il planner inserisce barriere `Eager` per: scrittura dopo lettura, `DELETE` dopo letture, espansione a lunghezza variabile prima di `CREATE`/`MERGE`, index scan attivo prima di una scrittura (una `SET` su una proprietà indicizzata durante un seek saltava righe). Non rimuoverle senza un test che lo giustifichi
- Le label e i tipi creati prima nella stessa query devono essere visibili alle clausole successive: i token si risolvono per riga finché non sono tutti risolti
- La cache dei piani non deve mai memorizzare un piano se la transazione ha cambiato lo schema
- I parametri del costruttore primario non sono accessibili dalle classi annidate: servono campi `private readonly` espliciti
- `byte*` non può essere argomento di un generico (niente `Array.Resize` su `byte*[]`)
- Un nuovo operatore è `RowLocal` solo se ogni riga d'uscita dipende da una sola riga d'ingresso e non ha effetti collaterali (niente `Limit`, `Distinct`, `Sort`, aggregazioni, scritture, procedure): altrimenti l'aggregazione parallela darebbe risultati sbagliati. Gli operatori e le closure dei piani devono restare thread-safe (sono già condivisi tra query dalla cache dei piani)
- Gli aggregatori nuovi devono implementare `Merge`; `DistinctAgg.Merge` si appoggia all'ordine di inserimento di `HashSet` (vale finché non si rimuovono elementi)
- Server Bolt: `localhost` risolve prima a `::1`, per questo `BoltServer` ascolta anche sul loopback IPv6 (senza, ogni connessione dei driver aspetta ~2 s). L'agent di default è `Neo4j/5.26.0` perché alcuni driver decidono le funzionalità dalla versione. Gli element id sono `n:<id>` / `r:<id>`, come `elementId()`: vanno cambiati insieme
- `QueryCursor`: una sola query aperta per transazione; iniziarne un'altra (o il commit) chiama `Detach` / `Dispose` su quella aperta. `Dispose` prima della fine ferma una lettura ma porta sempre a termine una scrittura. Un cursore di `db.Stream` possiede la sua transazione e la chiude a fine risultato
- Neo4j Browser (fonte: sorgenti del Browser 2025.x): legge la versione da `dbms.components()` `versions[0]` (non dall'agent), interroga `SHOW DATABASES` sul db `system` (anche scritto `SYSTEM`), `dbms.showCurrentUser()` deve restituire una riga; su una pagina https accetta solo `neo4j+s` / `bolt+s` con certificato fidato; via WebSocket la risposta di 4 byte dell'handshake deve arrivare in **un solo** messaggio
- L'app Query di browser.neo4j.io (2025+, diversa dal vecchio Browser) all'avvio manda anche `CALL dbms.info()`, `SHOW INDEXES`, `SHOW VECTOR INDEXES YIELD …`, `CALL db.schema.visualization() YIELD nodes, relationships` (uno standalone `CALL … YIELD`): l'elenco del pannello *Update styling priority* (↑↓) viene dallo schema, se la query fallisce resta vuoto. `dbms.licenseAgreementDetails` fallisce anche su Neo4j community: lasciarlo così. Le query che manda si vedono catturando i frame WebSocket via CDP (`Network.webSocketFrameSent`) da Playwright
- I nodi e le relazioni già materializzati (`Value.Obj` è `Node` / `Relationship`, per esempio quelli virtuali con id negativi di `db.schema.visualization`) non vengono ricaricati dallo store dal `Materializer`
- I test con `Neo4j.Driver` importano `using Neo4j.Driver;` per `As<T>()`: i tipi omonimi (`GraphDatabase`, `Node`…) si risolvono comunque su `GraphQLite` perché il namespace contenitore ha la precedenza; per i tipi del driver usare l'alias `N.`
- `Path` collide con `System.IO.Path`: il tipo dei percorsi si chiama `GraphPath`, ma il membro dell'enum resta `ValueKind.Path`

## Scelte volute (non segnalarle come problemi)
- Mappe e liste annidate sono ammesse come proprietà (al contrario di Neo4j): la restrizione di Neo4j è ideologica, non di prestazioni, e l'utente le ha chieste esplicitamente. Di conseguenza `SET n.a.b.c = v` / `REMOVE n.a.b.c` modificano un percorso dentro la mappa della proprietà `a` (`CompiledSetItem.SubPath`, `Mutations.SetNested`): estensione nostra, Neo4j la rifiuta
- Un solo processo per database (la directory è aperta in esclusiva): il supporto multi-processo richiederebbe un indice delle versioni in shared memory, come il `-shm` di SQLite
- Date limitate agli anni 1–9999 e precisione di 100 ns (tick .NET)
- Supporto a 32 bit gradito ma non necessario: funziona finché i file stanno nello spazio di indirizzamento

## Stile
- Imitare lo stile del codice circostante: indentazione a 4 spazi, commenti e `///` attualmente **in inglese**; nel dubbio, chiedere
- In `Math.Max` / `Math.Min` l'eventuale valore costante va per primo (`Math.Max(0, x)`)
- Le concatenazioni di metodi con più di 2 elementi vanno a capo su ogni punto, indentate
- Un oggetto letterale che deve aderire ad un tipo esistente va scritto dove quel tipo è noto (tipo dichiarato, `new()` con tipo di destinazione), così l'editor suggerisce i membri
- I TODO si scrivono come `// TODO: ...`, anche a destra della riga a cui si riferiscono
- Le prestazioni contano: ogni modifica sul percorso caldo (lettura pagine, operatori, `Value`) va misurata con il benchmark prima e dopo
- Confronto con Neo4j: `tools/GraphQLite.Compare` (C#: GraphQLite in-process o qualsiasi server Bolt; la modalità `bolt` **cancella** il database di destinazione) e `tools/GraphQLite.Compare/neo4j-embedded/Bench.java` (Neo4j embedded, si compila con i jar e il JDK di un'installazione di Neo4j, per esempio quella di Neo4j Desktop in `%USERPROFILE%\.Neo4jDesktop2\Data\dbmss\<id>\lib` e `...\Cache\runtime\zulu21...`). Enterprise embedded avvia i servizi di cluster: senza `embedded.conf` le porte collidono con un'istanza di Desktop accesa, e il database `neo4j` viene creato in modo asincrono (il benchmark lo aspetta; senza `System.exit` un'eccezione lascia la JVM appesa). L'istanza di Desktop dell'utente ascolta su 7687 (Bolt), 7688 (routing) e 7474: per un nostro server di prova usare un'altra porta (es. 7699). La password dell'istanza la chiede all'utente
- Gli stress test di concorrenza usano thread dedicati (`new Thread`), non `Task.Run`: dei cicli continui sul thread pool affamano gli altri test che girano in parallelo (i test Bolt asincroni andavano in timeout e la suite passava da 8 s a oltre un minuto)
- Le righe "24 threads" del benchmark completo sono troppo rumorose per giudicare una modifica alla concorrenza: serve un harness dedicato (`Parallel.For` per qualche centinaio di ms, migliore di 8 round) compilato contro le due versioni
- Sulla macchina dell'utente le misure oscillano anche di 2× tra un'esecuzione e l'altra: per un confronto si costruisce la versione committata in una `git worktree` nello scratchpad, si alternano le esecuzioni A/B più volte e si confronta il migliore di più round, non una singola esecuzione del benchmark

## Come lavorare
- Git lo gestisce l'utente: niente commit, cambi di branch o merge se non richiesti
- I fine riga (CRLF su disco) li gestisce git: non serve preoccuparsene né verificarli
- Un server avviato da `src/GraphQLite.Server/bin` blocca le DLL e fa fallire la build: per tenerlo acceso mentre si lavora, avviarlo da una copia della cartella di output nello scratchpad
- Neo4j Browser si prova con il server MCP Playwright (`.mcp.json`, Edge): per il certificato self-signed usare una sessione CDP con `Security.setIgnoreCertificateErrors` (da `browser_run_code_unsafe`), poi `https://browser.neo4j.io/?connectURL=neo4j%2Bs://localhost:7687`; senza autenticazione sul server va bene qualsiasi password
- Gli script Python passati a Git Bash con un heredoc si rompono se contengono apostrofi (`l'indirizzo`): scriverli in un file nello scratchpad con il tool Write
- Con PowerShell: le here-string producono CRLF e i `-replace` multilinea non trovano il testo (meglio il tool Edit); `R` è un alias di `Invoke-History`, non usarlo come nome di funzione
- Ogni modifica allo storage va verificata con i test di `StorageTests.cs` (crash simulati, WAL troncato, confronto casuale con un riferimento in memoria), ripetuti più volte perché quelli di concorrenza sono probabilistici
- Le risposte all'utente sono in italiano
- **Aggiorna questo file** ogni volta che scopri qualcosa di non ovvio sul progetto che non è già scritto qui: convenzioni, scelte volute, spiegazioni dell'utente, trappole. Tienilo conciso e nello stesso formato
