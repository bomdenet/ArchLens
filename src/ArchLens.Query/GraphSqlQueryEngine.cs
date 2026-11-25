using System.Text;
using ArchLens.Analysis;
using ArchLens.Model;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ArchLens.Query;

public sealed record SqlQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    bool Truncated);

/// <summary>
/// Projects a CodeGraph and its computed metrics into an in-memory SQLite database
/// and executes strictly read-only ad-hoc SQL queries (mini-CQLinq / SQL over Graph).
/// Enforces multi-layer read-only protection: SQL tokenizer, SQLite engine authorizer,
/// PRAGMA query_only = ON, and progress-handler / interrupt timeout enforcement.
/// </summary>
public sealed class GraphSqlQueryEngine
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE",
        "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX"
    };

    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension",
        "sqlite_load_extension"
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Column, string Type)>> SchemaTables =
        new Dictionary<string, IReadOnlyList<(string Column, string Type)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["nodes"] =
            [
                ("id", "TEXT"), ("name", "TEXT"), ("full_name", "TEXT"), ("kind", "TEXT"),
                ("project_name", "TEXT"), ("namespace", "TEXT"), ("type_kind", "TEXT"), ("accessibility", "TEXT"),
                ("is_abstract", "INTEGER"), ("is_static", "INTEGER"), ("file_path", "TEXT"), ("line", "INTEGER"),
                ("line_count", "INTEGER"), ("cyclomatic_complexity", "INTEGER"), ("cognitive_complexity", "INTEGER"),
                ("lcom4", "INTEGER"), ("constructor_params", "INTEGER"), ("member_count", "INTEGER"), ("containing_type_id", "TEXT")
            ],
            ["edges"] =
            [
                ("from_id", "TEXT"), ("to_id", "TEXT"), ("kind", "TEXT"), ("weight", "INTEGER"),
                ("file_path", "TEXT"), ("line", "INTEGER")
            ],
            ["metrics"] =
            [
                ("node_id", "TEXT"), ("name", "TEXT"), ("kind", "TEXT"),
                ("afferent_coupling", "INTEGER"), ("efferent_coupling", "INTEGER"),
                ("instability", "REAL"), ("abstractness", "REAL"), ("distance_from_main_sequence", "REAL"),
                ("cyclomatic_complexity", "INTEGER"), ("max_method_complexity", "INTEGER"),
                ("cognitive_complexity", "INTEGER"), ("max_method_cognitive_complexity", "INTEGER"),
                ("lcom4", "INTEGER"), ("line_count", "INTEGER"), ("constructor_params", "INTEGER")
            ],
            ["projects"] =
            [
                ("name", "TEXT"), ("file_path", "TEXT"), ("target_framework", "TEXT"),
                ("type_count", "INTEGER"), ("document_count", "INTEGER")
            ],
            ["di_registrations"] =
            [
                ("service_type_id", "TEXT"), ("implementation_type_id", "TEXT"), ("lifetime", "TEXT"),
                ("registered_by_type_id", "TEXT"), ("project_name", "TEXT"), ("file_path", "TEXT"), ("line", "INTEGER")
            ],
            ["analyzer_diagnostics"] =
            [
                ("id", "TEXT"), ("title", "TEXT"), ("category", "TEXT"), ("severity", "TEXT"),
                ("message", "TEXT"), ("project_name", "TEXT"), ("file_path", "TEXT"), ("line", "INTEGER")
            ],
            ["custom_metrics"] =
            [
                ("node_id", "TEXT"), ("metric_name", "TEXT"), ("value", "REAL")
            ],
            ["schema_info"] =
            [
                ("table_name", "TEXT"), ("column_name", "TEXT"), ("column_type", "TEXT")
            ]
        };

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetCompactSchema() =>
        SchemaTables.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.Select(c => c.Column).ToList(),
            StringComparer.Ordinal);

    public const string SchemaDocumentation = """
        # ArchLens SQL-over-Graph Schema (Read-Only SQLite)

        > Note: `PRAGMA` statements and `pragma_*` table-valued functions are disabled for read-only security.
        > Use `SELECT * FROM schema_info` (or `WHERE table_name = 'nodes'`) to inspect tables and columns at any time.

        Tables available in `query`:

        1. `nodes`
           - `id` TEXT PRIMARY KEY
           - `name` TEXT
           - `full_name` TEXT
           - `kind` TEXT ('Project', 'Namespace', 'Type', 'Method')
           - `project_name` TEXT
           - `namespace` TEXT
           - `type_kind` TEXT ('Class', 'Interface', 'Struct', 'Record', 'Enum', 'Delegate', NULL)
           - `accessibility` TEXT ('Public', 'Internal', 'Protected', 'Private', ...)
           - `is_abstract` INTEGER (0/1)
           - `is_static` INTEGER (0/1)
           - `file_path` TEXT
           - `line` INTEGER
           - `line_count` INTEGER
           - `cyclomatic_complexity` INTEGER
           - `cognitive_complexity` INTEGER
           - `lcom4` INTEGER
           - `constructor_params` INTEGER
           - `member_count` INTEGER
           - `containing_type_id` TEXT

        2. `edges`
           - `from_id` TEXT
           - `to_id` TEXT
           - `kind` TEXT ('ProjectReference', 'Inherits', 'Implements', 'FieldOrProperty', 'Parameter', 'ReturnType', 'MethodCall', 'ObjectCreation', 'GenericArgument', 'Attribute', 'TypeReference')
           - `weight` INTEGER
           - `file_path` TEXT
           - `line` INTEGER

        3. `metrics`
           - `node_id` TEXT
           - `name` TEXT
           - `kind` TEXT ('Type', 'Namespace', 'Method')
           - `afferent_coupling` INTEGER
           - `efferent_coupling` INTEGER
           - `instability` REAL
           - `abstractness` REAL (NULL for Type/Method)
           - `distance_from_main_sequence` REAL (NULL for Type/Method)
           - `cyclomatic_complexity` INTEGER
           - `max_method_complexity` INTEGER
           - `cognitive_complexity` INTEGER
           - `max_method_cognitive_complexity` INTEGER
           - `lcom4` INTEGER
           - `line_count` INTEGER
           - `constructor_params` INTEGER

        4. `projects`
           - `name` TEXT, `file_path` TEXT, `target_framework` TEXT, `type_count` INTEGER, `document_count` INTEGER

        5. `di_registrations`
           - `service_type_id` TEXT, `implementation_type_id` TEXT, `lifetime` TEXT, `registered_by_type_id` TEXT, `project_name` TEXT, `file_path` TEXT, `line` INTEGER

        6. `analyzer_diagnostics`
           - `id` TEXT, `title` TEXT, `category` TEXT, `severity` TEXT, `message` TEXT, `project_name` TEXT, `file_path` TEXT, `line` INTEGER

        7. `custom_metrics`
           - `node_id` TEXT, `metric_name` TEXT, `value` REAL

        8. `schema_info` (read-only schema metadata replacement for PRAGMA)
           - `table_name` TEXT, `column_name` TEXT, `column_type` TEXT

        Example queries:
        - Inspect columns: `SELECT column_name, column_type FROM schema_info WHERE table_name = 'nodes'`
        - Find God classes: `SELECT name, lcom4, efferent_coupling, max_method_cognitive_complexity FROM metrics WHERE kind = 'Type' AND lcom4 >= 2 ORDER BY lcom4 DESC, efferent_coupling DESC LIMIT 20`
        - Find cross-project edges: `SELECT a.project_name AS from_proj, b.project_name AS to_proj, COUNT(*) AS edge_count FROM edges e JOIN nodes a ON e.from_id = a.id JOIN nodes b ON e.to_id = b.id WHERE a.kind = 'Type' AND b.kind = 'Type' AND a.project_name != b.project_name GROUP BY from_proj, to_proj ORDER BY edge_count DESC`
        """;

    private readonly MetricsEngine _metricsEngine;

    public GraphSqlQueryEngine(MetricsEngine? metricsEngine = null)
    {
        _metricsEngine = metricsEngine ?? new MetricsEngine();
    }

    public SqlQueryResult Execute(
        CodeGraph graph,
        string sql,
        int maxRows = 100,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReadOnlySql(sql);
        cancellationToken.ThrowIfCancellationRequested();

        var clampedMaxRows = Math.Clamp(maxRows, 1, 500);
        var effectiveTimeout = timeout is { TotalMilliseconds: > 0 }
            ? timeout.Value
            : TimeSpan.FromSeconds(5);

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        CreateSchema(conn);
        PopulateDatabase(conn, graph, extraMetricCalculators, cancellationToken);

        // Layer 1: SQLite query_only lock
        using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = "PRAGMA query_only = ON;";
            lockCmd.ExecuteNonQuery();
        }

        // Layer 2: Engine-level SQLite authorizer allowing ONLY read-only SELECT / READ / safe functions
        var handle = conn.Handle!;
        var deadlineMs = Environment.TickCount64 + (long)Math.Ceiling(effectiveTimeout.TotalMilliseconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        raw.sqlite3_set_authorizer(handle, (_, actionCode, param0, param1, _, _) =>
        {
            if (actionCode == raw.SQLITE_SELECT || actionCode == raw.SQLITE_RECURSIVE)
            {
                return raw.SQLITE_OK;
            }

            if (actionCode == raw.SQLITE_READ)
            {
                // Block pragma_* table-valued functions (e.g. pragma_database_list, pragma_compile_options)
                if (!string.IsNullOrEmpty(param0) &&
                    param0.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
                {
                    return raw.SQLITE_DENY;
                }
                return raw.SQLITE_OK;
            }

            if (actionCode == raw.SQLITE_FUNCTION)
            {
                var fnName = param1 ?? param0 ?? string.Empty;
                return ForbiddenFunctions.Contains(fnName) ? raw.SQLITE_DENY : raw.SQLITE_OK;
            }

            return raw.SQLITE_DENY;
        }, null);

        // Layer 3: Deterministic CPU/infinite-recursion DoS cutoff via progress handler + sqlite3_interrupt
        raw.sqlite3_progress_handler(handle, 1000, _ =>
            timeoutCts.IsCancellationRequested || Environment.TickCount64 >= deadlineMs ? 1 : 0, null);

        using var cancelReg = timeoutCts.Token.Register(() =>
        {
            try { raw.sqlite3_interrupt(handle); } catch { }
        });

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Math.Max(1, (int)Math.Ceiling(effectiveTimeout.TotalSeconds));

            using var reader = cmd.ExecuteReader();

            // Deduplicate column names upfront so result.Columns and each row's keys match 1:1 without overwrites.
            var columns = new List<string>(reader.FieldCount);
            var usedColumnNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var baseName = reader.GetName(i);
                var candidate = baseName;
                var suffix = 1;
                while (!usedColumnNames.Add(candidate))
                {
                    candidate = $"{baseName}_{suffix++}";
                }
                columns.Add(candidate);
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var truncated = false;

            while (reader.Read())
            {
                if (rows.Count >= clampedMaxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return new SqlQueryResult(columns, rows, rows.Count, truncated);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("SQL query was cancelled.", ex, cancellationToken);
            }

            throw new TimeoutException($"SQL query exceeded the {effectiveTimeout.TotalSeconds:0.#}s execution time limit.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_AUTH)
        {
            throw new InvalidOperationException(
                $"SQL query rejected by read-only authorizer: {ex.Message}. Note: PRAGMA is disabled; query 'SELECT * FROM schema_info' to inspect tables and columns.", ex);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(BuildSchemaHintErrorMessage(ex.Message, sql), ex);
        }
        finally
        {
            try { raw.sqlite3_progress_handler(handle, 0, null, null); } catch { }
            try { raw.sqlite3_set_authorizer(handle, (strdelegate_authorizer?)null, null); } catch { }
        }
    }

    private static string BuildSchemaHintErrorMessage(string sqliteMessage, string sql)
    {
        var mentionedTables = SchemaTables.Keys
            .Where(t => System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{System.Text.RegularExpressions.Regex.Escape(t)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        if (sqliteMessage.Contains("no such table", StringComparison.OrdinalIgnoreCase) || mentionedTables.Count == 0)
        {
            var tableList = string.Join(", ", SchemaTables.Keys);
            return $"{sqliteMessage}. Available tables: {tableList}. Tip: run 'SELECT table_name, column_name, column_type FROM schema_info' (or pass includeSchema: true / --schema).";
        }

        var parts = mentionedTables.Select(t =>
            $"'{t}': [{string.Join(", ", SchemaTables[t].Select(c => c.Column))}]");
        return $"{sqliteMessage}. Available columns for {string.Join("; ", parts)}. Tip: query 'SELECT * FROM schema_info' for full schema.";
    }

    public static void ValidateReadOnlySql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));
        }

        var tokens = TokenizeSqlOutsideLiterals(sql, out var hasMultipleStatements);
        if (tokens.Count == 0)
        {
            throw new ArgumentException("SQL query contains no executable tokens.", nameof(sql));
        }

        if (hasMultipleStatements)
        {
            throw new InvalidOperationException("Only a single read-only SQL statement is allowed.");
        }

        var first = tokens[0];
        if (!first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !first.Equals("WITH", StringComparison.OrdinalIgnoreCase) &&
            !first.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only read-only SELECT / WITH / EXPLAIN queries are permitted (got '{first}').");
        }

        foreach (var token in tokens)
        {
            if (ForbiddenKeywords.Contains(token))
            {
                if (token.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "PRAGMA is forbidden in read-only queries. Query 'SELECT * FROM schema_info' or pass includeSchema: true (CLI: --schema) to inspect tables and columns.");
                }
                throw new InvalidOperationException($"Forbidden SQL keyword '{token}' in read-only query.");
            }

            if (token.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PRAGMA table-valued function '{token}' is forbidden in read-only queries. Query 'SELECT * FROM schema_info' or pass includeSchema: true (CLI: --schema) to inspect tables and columns.");
            }

            if (ForbiddenFunctions.Contains(token))
            {
                throw new InvalidOperationException($"Forbidden SQL function '{token}' in read-only query.");
            }
        }
    }

    private static List<string> TokenizeSqlOutsideLiterals(string sql, out bool hasMultipleStatements)
    {
        var tokens = new List<string>();
        hasMultipleStatements = false;
        var sawSemicolon = false;
        var i = 0;

        while (i < sql.Length)
        {
            var ch = sql[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            // Line comment --
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            // Block comment /* ... */
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                var closed = false;
                while (i + 1 < sql.Length)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        i += 2;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    throw new InvalidOperationException("Unterminated block comment in SQL query.");
                }
                continue;
            }

            // Single-quoted string literal ('...')
            if (ch == '\'')
            {
                if (sawSemicolon) hasMultipleStatements = true;
                i = ConsumeQuoted(sql, i, '\'');
                continue;
            }

            // Double-quoted identifier ("...")
            if (ch == '"')
            {
                if (sawSemicolon) hasMultipleStatements = true;
                i = ConsumeQuoted(sql, i, '"');
                continue;
            }

            // Backtick-quoted identifier (`...`)
            if (ch == '`')
            {
                if (sawSemicolon) hasMultipleStatements = true;
                i = ConsumeQuoted(sql, i, '`');
                continue;
            }

            // Bracket-quoted identifier ([...])
            if (ch == '[')
            {
                if (sawSemicolon) hasMultipleStatements = true;
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == ']')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    throw new InvalidOperationException("Unterminated bracket-quoted identifier in SQL query.");
                }
                continue;
            }

            if (ch == ';')
            {
                sawSemicolon = true;
                i++;
                continue;
            }

            if (sawSemicolon)
            {
                hasMultipleStatements = true;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(sql[start..i]);
                continue;
            }

            i++;
        }

        return tokens;
    }

    private static int ConsumeQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }

        throw new InvalidOperationException($"Unterminated quoted literal/identifier ({quote}) in SQL query.");
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE nodes (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                full_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_name TEXT NOT NULL,
                namespace TEXT,
                type_kind TEXT,
                accessibility TEXT NOT NULL,
                is_abstract INTEGER NOT NULL,
                is_static INTEGER NOT NULL,
                file_path TEXT,
                line INTEGER,
                line_count INTEGER NOT NULL,
                cyclomatic_complexity INTEGER NOT NULL,
                cognitive_complexity INTEGER NOT NULL,
                lcom4 INTEGER NOT NULL,
                constructor_params INTEGER NOT NULL,
                member_count INTEGER NOT NULL,
                containing_type_id TEXT
            );

            CREATE TABLE edges (
                from_id TEXT NOT NULL,
                to_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                weight INTEGER NOT NULL,
                file_path TEXT,
                line INTEGER
            );

            CREATE TABLE metrics (
                node_id TEXT NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                afferent_coupling INTEGER NOT NULL,
                efferent_coupling INTEGER NOT NULL,
                instability REAL NOT NULL,
                abstractness REAL,
                distance_from_main_sequence REAL,
                cyclomatic_complexity INTEGER NOT NULL,
                max_method_complexity INTEGER NOT NULL,
                cognitive_complexity INTEGER NOT NULL,
                max_method_cognitive_complexity INTEGER NOT NULL,
                lcom4 INTEGER NOT NULL,
                line_count INTEGER NOT NULL,
                constructor_params INTEGER NOT NULL
            );

            CREATE TABLE projects (
                name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                target_framework TEXT,
                type_count INTEGER NOT NULL,
                document_count INTEGER NOT NULL
            );

            CREATE TABLE di_registrations (
                service_type_id TEXT NOT NULL,
                implementation_type_id TEXT NOT NULL,
                lifetime TEXT NOT NULL,
                registered_by_type_id TEXT,
                project_name TEXT,
                file_path TEXT,
                line INTEGER
            );

            CREATE TABLE analyzer_diagnostics (
                id TEXT NOT NULL,
                title TEXT NOT NULL,
                category TEXT NOT NULL,
                severity TEXT NOT NULL,
                message TEXT NOT NULL,
                project_name TEXT,
                file_path TEXT,
                line INTEGER
            );

            CREATE TABLE custom_metrics (
                node_id TEXT NOT NULL,
                metric_name TEXT NOT NULL,
                value REAL NOT NULL
            );

            CREATE TABLE schema_info (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                column_type TEXT NOT NULL
            );

            CREATE INDEX idx_nodes_kind_proj ON nodes(kind, project_name);
            CREATE INDEX idx_nodes_ns ON nodes(namespace);
            CREATE INDEX idx_edges_from ON edges(from_id);
            CREATE INDEX idx_edges_to ON edges(to_id);
            CREATE INDEX idx_edges_kind ON edges(kind);
            CREATE INDEX idx_metrics_kind_node ON metrics(kind, node_id);
            CREATE INDEX idx_custom_metrics_name_node ON custom_metrics(metric_name, node_id);
            """;
        cmd.ExecuteNonQuery();

        using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = "INSERT INTO schema_info VALUES ($tbl, $col, $type);";
        var pTbl = schemaCmd.Parameters.Add("$tbl", SqliteType.Text);
        var pCol = schemaCmd.Parameters.Add("$col", SqliteType.Text);
        var pType = schemaCmd.Parameters.Add("$type", SqliteType.Text);
        foreach (var (tableName, cols) in SchemaTables)
        {
            foreach (var (colName, colType) in cols)
            {
                pTbl.Value = tableName;
                pCol.Value = colName;
                pType.Value = colType;
                schemaCmd.ExecuteNonQuery();
            }
        }
    }

    private void PopulateDatabase(
        SqliteConnection conn,
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators,
        CancellationToken cancellationToken)
    {
        using var tx = conn.BeginTransaction();

        cancellationToken.ThrowIfCancellationRequested();
        InsertNodes(conn, tx, graph);
        cancellationToken.ThrowIfCancellationRequested();
        InsertEdges(conn, tx, graph);
        cancellationToken.ThrowIfCancellationRequested();
        InsertProjects(conn, tx, graph);
        cancellationToken.ThrowIfCancellationRequested();
        InsertMetricsAndCustomMetrics(conn, tx, graph, extraMetricCalculators);
        cancellationToken.ThrowIfCancellationRequested();
        InsertDiRegistrations(conn, tx, graph);
        cancellationToken.ThrowIfCancellationRequested();
        InsertAnalyzerDiagnostics(conn, tx, graph);

        tx.Commit();
    }

    private static void InsertNodes(SqliteConnection conn, SqliteTransaction tx, CodeGraph graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO nodes VALUES (
                $id, $name, $full, $kind, $proj, $ns, $tk, $acc,
                $abs, $stat, $fp, $line, $lc, $cc, $cog, $lcom, $ctor, $mem, $ct
            );
            """;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pFull = cmd.Parameters.Add("$full", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pProj = cmd.Parameters.Add("$proj", SqliteType.Text);
        var pNs = cmd.Parameters.Add("$ns", SqliteType.Text);
        var pTk = cmd.Parameters.Add("$tk", SqliteType.Text);
        var pAcc = cmd.Parameters.Add("$acc", SqliteType.Text);
        var pAbs = cmd.Parameters.Add("$abs", SqliteType.Integer);
        var pStat = cmd.Parameters.Add("$stat", SqliteType.Integer);
        var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
        var pLine = cmd.Parameters.Add("$line", SqliteType.Integer);
        var pLc = cmd.Parameters.Add("$lc", SqliteType.Integer);
        var pCc = cmd.Parameters.Add("$cc", SqliteType.Integer);
        var pCog = cmd.Parameters.Add("$cog", SqliteType.Integer);
        var pLcom = cmd.Parameters.Add("$lcom", SqliteType.Integer);
        var pCtor = cmd.Parameters.Add("$ctor", SqliteType.Integer);
        var pMem = cmd.Parameters.Add("$mem", SqliteType.Integer);
        var pCt = cmd.Parameters.Add("$ct", SqliteType.Text);

        foreach (var n in graph.Nodes)
        {
            pId.Value = n.Id;
            pName.Value = n.Name;
            pFull.Value = n.FullName;
            pKind.Value = n.Kind.ToString();
            pProj.Value = n.ProjectName;
            pNs.Value = (object?)n.Namespace ?? DBNull.Value;
            pTk.Value = (object?)n.TypeKind?.ToString() ?? DBNull.Value;
            pAcc.Value = n.Accessibility.ToString();
            pAbs.Value = n.IsAbstract ? 1 : 0;
            pStat.Value = n.IsStatic ? 1 : 0;
            pFp.Value = (object?)n.Location?.FilePath ?? DBNull.Value;
            pLine.Value = (object?)n.Location?.Line ?? DBNull.Value;
            pLc.Value = n.LineCount;
            pCc.Value = n.CyclomaticComplexity;
            pCog.Value = n.CognitiveComplexity;
            pLcom.Value = n.Lcom4;
            pCtor.Value = n.ConstructorParameterCount;
            pMem.Value = n.MemberCount;
            pCt.Value = (object?)n.ContainingTypeId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertEdges(SqliteConnection conn, SqliteTransaction tx, CodeGraph graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO edges VALUES ($from, $to, $kind, $w, $fp, $line);";

        var pFrom = cmd.Parameters.Add("$from", SqliteType.Text);
        var pTo = cmd.Parameters.Add("$to", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pW = cmd.Parameters.Add("$w", SqliteType.Integer);
        var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
        var pLine = cmd.Parameters.Add("$line", SqliteType.Integer);

        foreach (var e in graph.Edges)
        {
            pFrom.Value = e.FromId;
            pTo.Value = e.ToId;
            pKind.Value = e.Kind.ToString();
            pW.Value = e.Weight;
            pFp.Value = (object?)e.Location?.FilePath ?? DBNull.Value;
            pLine.Value = (object?)e.Location?.Line ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertProjects(SqliteConnection conn, SqliteTransaction tx, CodeGraph graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO projects VALUES ($name, $fp, $tfm, $tc, $dc);";

        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
        var pTfm = cmd.Parameters.Add("$tfm", SqliteType.Text);
        var pTc = cmd.Parameters.Add("$tc", SqliteType.Integer);
        var pDc = cmd.Parameters.Add("$dc", SqliteType.Integer);

        foreach (var p in graph.Projects)
        {
            pName.Value = p.Name;
            pFp.Value = p.FilePath;
            pTfm.Value = (object?)p.TargetFramework ?? DBNull.Value;
            pTc.Value = p.TypeCount;
            pDc.Value = p.DocumentCount;
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertMetricsAndCustomMetrics(
        SqliteConnection conn,
        SqliteTransaction tx,
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators)
    {
        var extraList = extraMetricCalculators?.ToList();
        var typeMetrics = _metricsEngine.CalculateTypeMetrics(graph, extraList);
        var nsMetrics = _metricsEngine.CalculateNamespaceMetrics(graph, extraList);
        var methodMetrics = _metricsEngine.CalculateMethodMetrics(graph, extraList);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO metrics VALUES (
                $id, $name, $kind, $ca, $ce, $inst, $abs, $dist,
                $cc, $maxCc, $cog, $maxCog, $lcom, $loc, $ctor
            );
            """;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pCa = cmd.Parameters.Add("$ca", SqliteType.Integer);
        var pCe = cmd.Parameters.Add("$ce", SqliteType.Integer);
        var pInst = cmd.Parameters.Add("$inst", SqliteType.Real);
        var pAbs = cmd.Parameters.Add("$abs", SqliteType.Real);
        var pDist = cmd.Parameters.Add("$dist", SqliteType.Real);
        var pCc = cmd.Parameters.Add("$cc", SqliteType.Integer);
        var pMaxCc = cmd.Parameters.Add("$maxCc", SqliteType.Integer);
        var pCog = cmd.Parameters.Add("$cog", SqliteType.Integer);
        var pMaxCog = cmd.Parameters.Add("$maxCog", SqliteType.Integer);
        var pLcom = cmd.Parameters.Add("$lcom", SqliteType.Integer);
        var pLoc = cmd.Parameters.Add("$loc", SqliteType.Integer);
        var pCtor = cmd.Parameters.Add("$ctor", SqliteType.Integer);

        foreach (var m in typeMetrics.Values.Concat(nsMetrics.Values).Concat(methodMetrics.Values))
        {
            pId.Value = m.NodeId;
            pName.Value = m.Name;
            pKind.Value = m.Kind.ToString();
            pCa.Value = m.AfferentCoupling;
            pCe.Value = m.EfferentCoupling;
            pInst.Value = m.Instability;
            pAbs.Value = (object?)m.Abstractness ?? DBNull.Value;
            pDist.Value = (object?)m.DistanceFromMainSequence ?? DBNull.Value;
            pCc.Value = m.CyclomaticComplexity;
            pMaxCc.Value = m.MaxMethodComplexity;
            pCog.Value = m.CognitiveComplexity;
            pMaxCog.Value = m.MaxMethodCognitiveComplexity;
            pLcom.Value = m.Lcom4;
            pLoc.Value = m.LineCount;
            pCtor.Value = m.ConstructorParameterCount;
            cmd.ExecuteNonQuery();
        }

        // Insert custom metrics across ALL node kinds (Type, Method, Namespace, Project), filtering out NaN
        var allCustom = _metricsEngine.CalculateCustomMetrics(graph, extraList);
        if (allCustom.Count == 0)
        {
            return;
        }

        using var cmCmd = conn.CreateCommand();
        cmCmd.Transaction = tx;
        cmCmd.CommandText = "INSERT INTO custom_metrics VALUES ($nid, $mname, $val);";
        var cmNode = cmCmd.Parameters.Add("$nid", SqliteType.Text);
        var cmName = cmCmd.Parameters.Add("$mname", SqliteType.Text);
        var cmVal = cmCmd.Parameters.Add("$val", SqliteType.Real);

        foreach (var (metricName, values) in allCustom)
        {
            foreach (var (nodeId, val) in values)
            {
                if (double.IsNaN(val))
                {
                    continue;
                }

                cmNode.Value = nodeId;
                cmName.Value = metricName;
                cmVal.Value = val;
                cmCmd.ExecuteNonQuery();
            }
        }
    }

    private static void InsertDiRegistrations(SqliteConnection conn, SqliteTransaction tx, CodeGraph graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO di_registrations VALUES ($svc, $impl, $life, $regBy, $proj, $fp, $line);";
        var pSvc = cmd.Parameters.Add("$svc", SqliteType.Text);
        var pImpl = cmd.Parameters.Add("$impl", SqliteType.Text);
        var pLife = cmd.Parameters.Add("$life", SqliteType.Text);
        var pRegBy = cmd.Parameters.Add("$regBy", SqliteType.Text);
        var pProj = cmd.Parameters.Add("$proj", SqliteType.Text);
        var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
        var pLine = cmd.Parameters.Add("$line", SqliteType.Integer);

        foreach (var r in graph.DiRegistrations)
        {
            pSvc.Value = r.ServiceTypeId;
            pImpl.Value = r.ImplementationTypeId;
            pLife.Value = r.Lifetime.ToString();
            pRegBy.Value = (object?)r.RegisteredByTypeId ?? DBNull.Value;
            pProj.Value = (object?)r.ProjectName ?? DBNull.Value;
            pFp.Value = (object?)r.Location?.FilePath ?? DBNull.Value;
            pLine.Value = (object?)r.Location?.Line ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertAnalyzerDiagnostics(SqliteConnection conn, SqliteTransaction tx, CodeGraph graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO analyzer_diagnostics VALUES ($id, $title, $cat, $sev, $msg, $proj, $fp, $line);";
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pCat = cmd.Parameters.Add("$cat", SqliteType.Text);
        var pSev = cmd.Parameters.Add("$sev", SqliteType.Text);
        var pMsg = cmd.Parameters.Add("$msg", SqliteType.Text);
        var pProj = cmd.Parameters.Add("$proj", SqliteType.Text);
        var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
        var pLine = cmd.Parameters.Add("$line", SqliteType.Integer);

        foreach (var d in graph.AnalyzerDiagnostics)
        {
            pId.Value = d.Id;
            pTitle.Value = d.Title;
            pCat.Value = d.Category;
            pSev.Value = d.Severity.ToString();
            pMsg.Value = d.Message;
            pProj.Value = (object?)d.ProjectName ?? DBNull.Value;
            pFp.Value = (object?)d.Location?.FilePath ?? DBNull.Value;
            pLine.Value = (object?)d.Location?.Line ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }
}
