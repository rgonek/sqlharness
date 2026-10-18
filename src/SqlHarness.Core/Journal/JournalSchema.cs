using SqlHarness.Core.Journal;

namespace SqlHarness.Core;

internal static class JournalSchema
{
    internal const int FirstRetainedVersion = 17;
    internal const int CurrentVersion = 17;

    internal static readonly IReadOnlyDictionary<int, string> Migrations = new Dictionary<int, string>
    {
        [15] = "ALTER TABLE operations ADD COLUMN measurement_fingerprint TEXT;",
        [16] = "ALTER TABLE operations ADD COLUMN warnings_json TEXT NOT NULL DEFAULT '[]';",
    };

    internal const string Current = """
        CREATE TABLE journal_meta (key TEXT PRIMARY KEY, value BLOB NOT NULL);
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
            last_seen TEXT NOT NULL,
            client_title TEXT,
            roots_json TEXT,
            protocol_version TEXT
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
            candidate_sql_text TEXT,
            raw_bytes INTEGER,
            raw_lines INTEGER,
            emitted_bytes INTEGER,
            emitted_lines INTEGER,
            error_message TEXT,
            cancel_reason TEXT,
            client_call_id TEXT,
            agent_model TEXT,
            agent_reasoning_effort TEXT,
            agent_session_id TEXT,
            agent_turn_id TEXT,
            agent_turn_trigger TEXT,
            agent_thread_source TEXT,
            operation_ref TEXT,
            parent_operation_ref TEXT,
            dry_run INTEGER NOT NULL DEFAULT 0,
            compare_subject TEXT,
            compare_scope TEXT,
            compare_conditions_json TEXT,
            setup_sql_text TEXT,
            conditions_json TEXT,
            conditions_fingerprint TEXT,
            measurement_fingerprint TEXT,
            warnings_json TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX ix_operations_session ON operations(session_id, id);
        CREATE INDEX ix_operations_updated ON operations(updated_at);
        CREATE INDEX ix_operations_status ON operations(status);
        CREATE INDEX ix_operations_sql_hash ON operations(sql_hash);
        CREATE TABLE operation_metrics (
            id INTEGER PRIMARY KEY,
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            variant TEXT NOT NULL,
            parameter_set TEXT,
            matrix_cell INTEGER,
            case_id TEXT NOT NULL DEFAULT '0',
            case_label TEXT NOT NULL DEFAULT 'Case 1 of 1',
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
        CREATE TABLE plan_occurrences (
            metric_id INTEGER NOT NULL REFERENCES operation_metrics(id) ON DELETE CASCADE,
            repetition INTEGER NOT NULL,
            document_ordinal INTEGER NOT NULL,
            statement_index INTEGER NOT NULL,
            document_sha TEXT,
            omitted INTEGER NOT NULL DEFAULT 0,
            span_start INTEGER,
            span_length INTEGER,
            match_kind TEXT NOT NULL DEFAULT 'unmatched',
            statement_id INTEGER,
            statement_comp_id INTEGER,
            PRIMARY KEY (metric_id, repetition, document_ordinal, statement_index)
        );
        CREATE INDEX ix_plan_occurrences_document_sha ON plan_occurrences(document_sha);
        CREATE TABLE plan_documents (
            sha256 TEXT PRIMARY KEY,
            shape_hash TEXT NOT NULL,
            format TEXT NOT NULL,
            raw_size INTEGER NOT NULL,
            gz BLOB NOT NULL
        );
        CREATE TABLE operation_statements (
            id INTEGER PRIMARY KEY,
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            variant TEXT NOT NULL,
            parameter_set TEXT,
            matrix_cell INTEGER,
            case_id TEXT NOT NULL DEFAULT '0',
            case_label TEXT NOT NULL DEFAULT 'Case 1 of 1',
            statement_ordinal INTEGER NOT NULL,
            statement_hash TEXT NOT NULL,
            cpu_ms INTEGER,
            elapsed_ms INTEGER,
            dop INTEGER,
            operators_json TEXT NOT NULL
        );
        CREATE INDEX ix_operation_statements_operation ON operation_statements(operation_id, ordinal);
        CREATE INDEX ix_operation_statements_hash ON operation_statements(statement_hash);
        CREATE UNIQUE INDEX ix_operations_operation_ref ON operations(operation_ref) WHERE operation_ref IS NOT NULL;
        CREATE TABLE session_annotations (
            session_id INTEGER PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
            title TEXT,
            note TEXT,
            starred INTEGER NOT NULL DEFAULT 0,
            archived_at TEXT,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX ix_session_annotations_starred ON session_annotations(starred) WHERE starred = 1;
        CREATE TABLE operation_phases (
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            phase TEXT NOT NULL,
            count INTEGER NOT NULL,
            total_ms INTEGER NOT NULL,
            max_ms INTEGER NOT NULL,
            PRIMARY KEY (operation_id, phase)
        );
        CREATE TABLE operation_events (
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            offset_ms INTEGER NOT NULL,
            phase TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            outcome TEXT NOT NULL,
            error_number INTEGER,
            detail_json TEXT,
            PRIMARY KEY (operation_id, seq)
        );
        CREATE TABLE agent_conversations (
            id INTEGER PRIMARY KEY,
            agent_kind TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            title TEXT,
            updated_at TEXT NOT NULL,
            UNIQUE (agent_kind, conversation_id)
        );
        CREATE TABLE agent_tool_calls (
            agent_kind TEXT NOT NULL,
            call_id TEXT NOT NULL,
            conversation INTEGER NOT NULL REFERENCES agent_conversations(id) ON DELETE CASCADE,
            model TEXT,
            effort TEXT,
            seen_at TEXT NOT NULL,
            PRIMARY KEY (agent_kind, call_id)
        );
        CREATE TABLE operation_agent_links (
            operation_id INTEGER PRIMARY KEY REFERENCES operations(id) ON DELETE CASCADE,
            conversation INTEGER NOT NULL REFERENCES agent_conversations(id) ON DELETE CASCADE,
            model TEXT,
            effort TEXT
        );
        CREATE INDEX ix_operation_agent_links_conversation ON operation_agent_links(conversation);
        CREATE TABLE transcript_cursors (
            path TEXT PRIMARY KEY,
            size INTEGER NOT NULL,
            mtime TEXT NOT NULL,
            offset INTEGER NOT NULL
        );
        CREATE TABLE mutation_requests (
            id INTEGER PRIMARY KEY,
            request_id TEXT NOT NULL UNIQUE,
            session_key TEXT NOT NULL,
            host_pid INTEGER NOT NULL,
            host_started_at TEXT NULL,
            profile TEXT NOT NULL,
            engine TEXT NOT NULL,
            server TEXT NOT NULL,
            database_name TEXT NOT NULL,
            tier TEXT NOT NULL,
            mode TEXT NOT NULL,
            digest TEXT NOT NULL,
            environment TEXT NULL,
            sql_text TEXT NULL,
            parameters_json TEXT NULL,
            state TEXT NOT NULL,
            status TEXT NULL,
            verification TEXT NULL,
            created_at TEXT NOT NULL,
            decided_at TEXT NULL,
            finished_at TEXT NULL
        );
        CREATE INDEX ix_mutation_requests_created ON mutation_requests (created_at);
        """;
}
