namespace SqlHarness.Core;

internal static class JournalSchema
{
    internal const int CurrentVersion = 4;

    internal const string Version1 = """
        CREATE TABLE sessions (
            id INTEGER PRIMARY KEY,
            session_key TEXT NOT NULL UNIQUE,
            agent_kind TEXT NOT NULL,
            transport TEXT NOT NULL,
            source TEXT NOT NULL,
            client_name TEXT,
            client_version TEXT,
            mcp_mode TEXT,
            agent_pid INTEGER,
            agent_started_at TEXT,
            host_pid INTEGER NOT NULL,
            host_started_at TEXT,
            cwd TEXT,
            first_seen TEXT NOT NULL,
            last_seen TEXT NOT NULL
        );
        CREATE TABLE operations (
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            operation TEXT NOT NULL,
            host_pid INTEGER NOT NULL,
            host_started_at TEXT,
            started_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL,
            exit_code INTEGER,
            error_kind TEXT,
            duration_ms INTEGER,
            profile TEXT,
            vars_json TEXT,
            engine TEXT,
            server TEXT,
            database TEXT,
            mutation_requested INTEGER NOT NULL DEFAULT 0,
            result_sets INTEGER,
            rows_returned INTEGER,
            artifact_dir TEXT,
            summary_json TEXT,
            progress_json TEXT,
            raw_tokens INTEGER,
            emitted_tokens INTEGER,
            sql_hash TEXT,
            candidate_sql_hash TEXT,
            sql_text TEXT,
            candidate_sql_text TEXT
        );
        CREATE INDEX ix_operations_session ON operations(session_id, id);
        CREATE INDEX ix_operations_updated ON operations(updated_at);
        CREATE INDEX ix_operations_status ON operations(status);
        CREATE INDEX ix_operations_sql_hash ON operations(sql_hash);
        """;

    internal const string Version2 = """
        CREATE TABLE operation_metrics (
            id INTEGER PRIMARY KEY,
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            variant TEXT NOT NULL,
            parameter_set TEXT,
            matrix_cell INTEGER,
            runs INTEGER NOT NULL,
            elapsed_ms_min INTEGER, elapsed_ms_median INTEGER, elapsed_ms_max INTEGER,
            cpu_ms_min INTEGER, cpu_ms_median INTEGER, cpu_ms_max INTEGER,
            logical_reads_min INTEGER, logical_reads_median INTEGER, logical_reads_max INTEGER,
            grant_requested_kb INTEGER, grant_granted_kb INTEGER, grant_max_used_kb INTEGER,
            dop INTEGER, compile_time_ms INTEGER, compile_cpu_ms INTEGER,
            spill_count INTEGER NOT NULL,
            has_warnings INTEGER NOT NULL,
            has_implicit_conversion INTEGER NOT NULL,
            missing_index_count INTEGER NOT NULL,
            waits_json TEXT,
            pg_shared_hit INTEGER, pg_shared_read INTEGER, pg_shared_dirtied INTEGER, pg_shared_written INTEGER,
            pg_temp_read INTEGER, pg_temp_written INTEGER
        );
        CREATE INDEX ix_operation_metrics_operation ON operation_metrics(operation_id, ordinal);
        CREATE TABLE operation_table_io (
            metric_id INTEGER NOT NULL REFERENCES operation_metrics(id) ON DELETE CASCADE,
            table_name TEXT NOT NULL,
            logical_reads INTEGER NOT NULL,
            scan_count INTEGER, physical_reads INTEGER, page_server_reads INTEGER, read_ahead_reads INTEGER,
            lob_logical_reads INTEGER, lob_physical_reads INTEGER, lob_read_ahead_reads INTEGER,
            cold_runs INTEGER NOT NULL,
            PRIMARY KEY (metric_id, table_name)
        );
        CREATE TABLE operation_plans (
            metric_id INTEGER NOT NULL REFERENCES operation_metrics(id) ON DELETE CASCADE,
            repetition INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            plan_hash TEXT NOT NULL,
            PRIMARY KEY (metric_id, repetition, ordinal)
        );
        CREATE INDEX ix_operation_plans_hash ON operation_plans(plan_hash);
        CREATE TABLE plans (
            hash TEXT PRIMARY KEY,
            format TEXT NOT NULL,
            raw_size INTEGER NOT NULL,
            gz BLOB NOT NULL,
            first_seen TEXT NOT NULL
        );
        """;

    internal const string Version3 = """
        ALTER TABLE operations ADD COLUMN raw_bytes INTEGER;
        ALTER TABLE operations ADD COLUMN raw_lines INTEGER;
        ALTER TABLE operations ADD COLUMN emitted_bytes INTEGER;
        ALTER TABLE operations ADD COLUMN emitted_lines INTEGER;
        """;

    internal const string Version4 = """
        ALTER TABLE operations ADD COLUMN error_message TEXT;
        """;
}