import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;
import org.neo4j.dbms.api.DatabaseManagementService;
import org.neo4j.dbms.api.Neo4jDatabaseManagementServiceBuilder;
import org.neo4j.graphdb.GraphDatabaseService;

// Neo4j embedded counterpart of GraphQLite.Compare: same data, queries and measuring method.
//
// Compile and run with the jars and the JDK of a Neo4j installation (for example a Neo4j Desktop instance):
//   javac -proc:none -cp "<neo4j>/lib/*" -d out Bench.java
//   java -Xmx2g -cp "<neo4j>/lib/*;out" Bench <database directory> [--community] [--parallel-runtime] [--skip-load]
// The settings come from embedded.conf (next to this file, or -Dconf=<path>): with Enterprise it moves the cluster
// ports, which would otherwise clash with a Neo4j server running on the same machine.
public class Bench {
    static final int NODES = 200_000, RELS_PER_NODE = 5, BATCH = 10_000;
    static GraphDatabaseService db;

    public static void main(String[] args) throws Exception {
        var options = Arrays.asList(args);
        Path dir = Path.of(args[0]);
        Path conf = Path.of(System.getProperty("conf", "embedded.conf"));
        boolean community = options.contains("--community");
        Neo4jDatabaseManagementServiceBuilder builder = community
            ? new org.neo4j.dbms.api.DatabaseManagementServiceBuilder(dir)
            : (Neo4jDatabaseManagementServiceBuilder) Class.forName("com.neo4j.dbms.api.EnterpriseDatabaseManagementServiceBuilder")
                .getConstructor(Path.class).newInstance(dir);
        DatabaseManagementService dbms = builder.loadPropertiesFromFile(conf).build();
        try {
            // Enterprise creates the default database asynchronously after startup: wait for it
            long deadline = System.nanoTime() + 120_000_000_000L;
            while (true) {
                try {
                    db = dbms.database("neo4j");
                    if (db.isAvailable(1000)) break;
                } catch (org.neo4j.dbms.api.DatabaseNotFoundException e) {
                    if (System.nanoTime() > deadline) throw e;
                    Thread.sleep(200);
                }
            }
            System.out.println("target: neo4j embedded " + (community ? "community" : "enterprise"));
            run(!options.contains("--skip-load"), options.contains("--parallel-runtime") ? "CYPHER runtime=parallel " : "");
        } finally {
            dbms.shutdown();
            System.out.flush();
            System.exit(0); // never leave the JVM hanging on non-daemon Neo4j threads
        }
    }

    static void run(boolean load, String pre) throws Exception {
        int clients = Runtime.getRuntime().availableProcessors();
        if (load) {
            execute("CREATE CONSTRAINT person_id IF NOT EXISTS FOR (p:Person) REQUIRE p.id IS UNIQUE", null);
            long s = System.nanoTime();
            for (int start = 0; start < NODES; start += BATCH) {
                var rows = new ArrayList<Map<String, Object>>();
                for (int i = start; i < Math.min(NODES, start + BATCH); i++)
                    rows.add(Map.of("id", (long) i, "name", "person" + i, "age", (long) (18 + i % 60)));
                execute("UNWIND $rows AS r CREATE (:Person {id: r.id, name: r.name, age: r.age})", Map.of("rows", rows));
            }
            report("bulk create nodes (UNWIND, 10k/tx)", NODES / seconds(s), "ops/s");
            var lr = new Random(42);
            s = System.nanoTime();
            long totalRels = (long) NODES * RELS_PER_NODE;
            for (long start = 0; start < totalRels; start += BATCH) {
                var rows = new ArrayList<Map<String, Object>>();
                for (long i = start; i < Math.min(totalRels, start + BATCH); i++)
                    rows.add(Map.of("a", i / RELS_PER_NODE, "b", (long) lr.nextInt(NODES), "w", (long) lr.nextInt(100)));
                execute("UNWIND $rows AS r MATCH (a:Person {id: r.a}), (b:Person {id: r.b}) CREATE (a)-[:KNOWS {w: r.w}]->(b)", Map.of("rows", rows));
            }
            report("bulk create rels (UNWIND + 2 index seeks)", totalRels / seconds(s), "ops/s");
        }

        String lookup = "MATCH (p:Person {id: $id}) RETURN p.name";
        String oneHop = "MATCH (p:Person {id: $id})-[:KNOWS]->(f) RETURN count(f)";
        String twoHop = "MATCH (p:Person {id: $id})-[:KNOWS]->()-[:KNOWS]->(fof) RETURN count(DISTINCT fof)";
        String update = "MATCH (p:Person {id: $id}) SET p.visits = coalesce(p.visits, 0) + 1";
        Function<String, Function<Random, Query>> byId = q -> r -> new Query(q, Map.of("id", (long) r.nextInt(NODES)));

        report("point lookup by unique index (1 client)", timed(1, 2, byId.apply(lookup)), "ops/s");
        report("point lookup by unique index (" + clients + " clients)", timed(clients, 2, byId.apply(lookup)), "ops/s");
        report("1-hop neighbour count (1 client)", timed(1, 2, byId.apply(oneHop)), "ops/s");
        report("2-hop friends-of-friends (1 client)", timed(1, 2, byId.apply(twoHop)), "ops/s");
        report("2-hop friends-of-friends (" + clients + " clients)", timed(clients, 2, byId.apply(twoHop)), "ops/s");
        report("shortestPath (max 6 hops, 1 client)", timed(1, 3, r -> new Query(
            "MATCH (a:Person {id: $a}), (b:Person {id: $b}) MATCH p = shortestPath((a)-[:KNOWS*..6]->(b)) RETURN length(p)",
            Map.of("a", (long) r.nextInt(NODES), "b", (long) r.nextInt(NODES)))), "ops/s");

        analytic("label scan + group by age", NODES, pre + "MATCH (p:Person) RETURN p.age AS age, count(*) AS c ORDER BY c DESC LIMIT 3");
        analytic("label scan + filter", NODES, pre + "MATCH (p:Person) WHERE p.age > 50 AND p.name STARTS WITH 'person1' RETURN count(p)");
        analytic("expand all relationships + filter on rel prop", (long) NODES * RELS_PER_NODE, pre + "MATCH (:Person)-[k:KNOWS]->(:Person) WHERE k.w < 10 RETURN count(k)");
        analytic("in-degree top-5 (aggregate + top-N)", (long) NODES * RELS_PER_NODE, pre + "MATCH (p:Person)<-[:KNOWS]-(f) RETURN p.id, count(f) AS deg ORDER BY deg DESC LIMIT 5");

        report("single-statement update (1 client)", timed(1, 3, byId.apply(update)), "ops/s");
        report("single-statement update (" + clients + " clients)", timed(clients, 3, byId.apply(update)), "ops/s");
    }

    record Query(String text, Map<String, Object> parameters) {}

    static double seconds(long startNanos) { return (System.nanoTime() - startNanos) / 1e9; }

    static void report(String name, double perSecond, String unit) { System.out.printf("%-50s %,14.0f %s%n", name, perSecond, unit); }

    /** One auto-commit transaction; every row is consumed. */
    static void execute(String q, Map<String, Object> p) {
        db.executeTransactionally(q, p == null ? Map.of() : p, r -> { while (r.hasNext()) r.next(); return null; });
    }

    /** Warm-up, then the best of three windows. */
    static double timed(int clients, double seconds, Function<Random, Query> next) throws Exception {
        window(clients, Math.max(1, seconds / 2), 0, next);
        double best = 0;
        for (int w = 1; w <= 3; w++) best = Math.max(best, window(clients, seconds, w, next));
        return best;
    }

    static double window(int clients, double seconds, int seed, Function<Random, Query> next) throws Exception {
        var pool = Executors.newFixedThreadPool(clients);
        var total = new AtomicLong();
        long start = System.nanoTime(), end = start + (long) (seconds * 1e9);
        var futures = new ArrayList<Future<?>>();
        for (int c = 0; c < clients; c++) {
            int client = c;
            futures.add(pool.submit(() -> {
                var r = new Random(seed * 1000L + client);
                long n = 0;
                while (System.nanoTime() < end) {
                    var q = next.apply(r);
                    execute(q.text(), q.parameters());
                    n++;
                }
                total.addAndGet(n);
            }));
        }
        for (var f : futures) f.get();
        pool.shutdown();
        return total.get() / seconds(start);
    }

    /** Best of 5 after 3 warm-up runs. */
    static void analytic(String name, long rows, String q) {
        for (int i = 0; i < 3; i++) execute(q, null);
        long best = Long.MAX_VALUE;
        for (int i = 0; i < 5; i++) {
            long s = System.nanoTime();
            execute(q, null);
            best = Math.min(best, System.nanoTime() - s);
        }
        report(String.format("%s [%.1f ms]", name, best / 1e6), rows / (best / 1e9), "rows/s");
    }
}
