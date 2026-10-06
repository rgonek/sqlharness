namespace SqlHarness.Core;

internal static class JournalSchema
{
    internal const int CurrentVersion = 1;

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
}