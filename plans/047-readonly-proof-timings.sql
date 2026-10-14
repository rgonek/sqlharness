SET lc_messages = 'C';
SET client_encoding = 'UTF8';
\pset pager off
\timing off
SET max_parallel_workers_per_gather = 0;
SET synchronous_commit = off;

\echo == J. EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) of temp DML: read-write vs read-only, 5 repeats each ==
CREATE TEMP TABLE p47_bench (id int primary key, v text);
INSERT INTO p47_bench SELECT g, 'v' || g FROM generate_series(1, 200000) g;
ANALYZE p47_bench;
UPDATE p47_bench SET v = v || 'rw' WHERE id > 0;
ANALYZE p47_bench;

\echo == J1. read-write repeats ==
SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE;
\timing on
UPDATE p47_bench SET v = v || 'a' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'a' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'a' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'a' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'a' WHERE id <= 50000;
\timing off

\echo == J2. read-only repeats (same statements) ==
SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY;
SHOW transaction_read_only;
\timing on
UPDATE p47_bench SET v = v || 'b' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'b' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'b' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'b' WHERE id <= 50000;
UPDATE p47_bench SET v = v || 'b' WHERE id <= 50000;
\timing off
SELECT 'J rows' AS step, count(*) FROM p47_bench WHERE v LIKE '%b';

\echo == K. EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) of temp DML inside BEGIN READ ONLY ==
SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE;
CREATE TEMP TABLE p47_bench2 (id int primary key, v text);
INSERT INTO p47_bench2 SELECT g, 'v' || g FROM generate_series(1, 200000) g;
ANALYZE p47_bench2;
BEGIN READ ONLY;
SHOW transaction_read_only;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) UPDATE p47_bench2 SET v = v || 'z' WHERE id <= 50000;
ROLLBACK;

\echo == L. read-only session: plan-only EXPLAIN of a persistent-table UPDATE (what sqlharness measures for candidates) ==
BEGIN READ ONLY;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) UPDATE p47_persist SET v = v || 'q' WHERE id <= 2;
ROLLBACK;

\echo == M. session-level read-only: read of a temp table created earlier ==
SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY;
SELECT 'M temp read rows' AS step, count(*) FROM p47_bench2;
SELECT 'M persistent read rows' AS step, count(*) FROM p47_persist;
SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE;
SELECT 'M persistent table unchanged' AS step, id, v FROM p47_persist ORDER BY id;
