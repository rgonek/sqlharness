SET lc_messages = 'C';
SET client_encoding = 'UTF8';
\pset pager off
\timing off

\echo == A. read-write setup: persistent + temp objects ==
DROP TABLE IF EXISTS p47_persist;
DROP TABLE IF EXISTS p47_shadow;
DROP TABLE IF EXISTS p47_seq;
CREATE TABLE p47_persist (id int primary key, v text);
INSERT INTO p47_persist VALUES (1, 'a'), (2, 'b');
CREATE SEQUENCE p47_seq;
CREATE TEMP TABLE p47_temp (id int primary key, v text);
INSERT INTO p47_temp VALUES (1, 'x'), (2, 'y');
CREATE TEMP TABLE p47_shadow (id int primary key, v text);
INSERT INTO p47_shadow VALUES (2, 'temp-wins');
CREATE TABLE p47_shadow (id int primary key, v text);
INSERT INTO p47_shadow VALUES (1, 'persistent-shadow');
CREATE TEMP TABLE p47_temp5 (id int, v text);
SELECT 'read-write setup done' AS step;

\echo == B. BEGIN READ ONLY: DML on TEMP table ==
BEGIN READ ONLY;
INSERT INTO p47_temp VALUES (3, 'ro');
UPDATE p47_temp SET v = 'ro' WHERE id = 1;
DELETE FROM p47_temp WHERE id = 2;
MERGE INTO p47_temp t USING (VALUES (4, 'm')) AS s(id, v) ON t.id = s.id
  WHEN MATCHED THEN UPDATE SET v = s.v
  WHEN NOT MATCHED THEN INSERT (id, v) VALUES (s.id, s.v);
SELECT 'B temp rows' AS step, count(*) FROM p47_temp;
SELECT 'B temp contents' AS step, id, v FROM p47_temp ORDER BY id;
ROLLBACK;

\echo == C. BEGIN READ ONLY: DML on PERSISTENT table ==
BEGIN READ ONLY;
INSERT INTO p47_persist VALUES (3, 'ro');
ROLLBACK;
BEGIN READ ONLY;
UPDATE p47_persist SET v = 'ro' WHERE id = 1;
ROLLBACK;
BEGIN READ ONLY;
DELETE FROM p47_persist WHERE id = 2;
ROLLBACK;
BEGIN READ ONLY;
MERGE INTO p47_persist t USING (VALUES (1, 'ro')) AS s(id, v) ON t.id = s.id
  WHEN MATCHED THEN UPDATE SET v = s.v
  WHEN NOT MATCHED THEN INSERT (id, v) VALUES (s.id, s.v);
ROLLBACK;

\echo == D. SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY: DML on TEMP table ==
SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY;
SHOW transaction_read_only;
INSERT INTO p47_temp VALUES (3, 'ro');
UPDATE p47_temp SET v = 'ro' WHERE id = 1;
DELETE FROM p47_temp WHERE id = 2;
MERGE INTO p47_temp t USING (VALUES (4, 'm')) AS s(id, v) ON t.id = s.id
  WHEN MATCHED THEN UPDATE SET v = s.v
  WHEN NOT MATCHED THEN INSERT (id, v) VALUES (s.id, s.v);
SELECT 'D temp contents' AS step, id, v FROM p47_temp ORDER BY id;

\echo == E. read-only session: DML on PERSISTENT table ==
INSERT INTO p47_persist VALUES (4, 'ro2');
UPDATE p47_persist SET v = 'ro2' WHERE id = 1;
DELETE FROM p47_persist WHERE id = 1;
MERGE INTO p47_persist t USING (VALUES (2, 'ro2')) AS s(id, v) ON t.id = s.id
  WHEN MATCHED THEN UPDATE SET v = s.v
  WHEN NOT MATCHED THEN INSERT (id, v) VALUES (s.id, s.v);
SELECT * INTO TEMP p47_temp6 FROM p47_persist;

\echo == F. read-only session: DDL on temp (CREATE TEMP / SELECT INTO TEMP / CREATE INDEX / TRUNCATE / DROP) ==
CREATE TEMP TABLE p47_temp7 (id int primary key, v text);
CREATE TEMP TABLE p47_temp8 AS SELECT * FROM p47_persist;
SELECT * INTO TEMP p47_temp9 FROM p47_persist;
CREATE INDEX p47_temp7_idx ON p47_temp7 (v);
CREATE INDEX p47_temp8_idx ON p47_temp8 (v);
CREATE INDEX p47_temp9_idx ON p47_temp9 (v);
TRUNCATE p47_temp7;
TRUNCATE p47_temp8;
TRUNCATE p47_temp9;
DROP TABLE p47_temp7;
DROP TABLE p47_temp8;
DROP TABLE p47_temp9;
SELECT 'F temp tables left' AS step, count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname LIKE 'pg_temp%';

\echo == G. search_path shadowing under a read-only session: unqualified name, public before pg_temp ==
SET search_path TO public, pg_temp;
SHOW search_path;
SELECT 'G which relation wins (public first)' AS step, n.nspname AS schema, c.relname
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'p47_shadow';
INSERT INTO p47_shadow VALUES (3, 'unqualified');
SELECT 'G unqualified rows' AS step, id, v FROM p47_shadow ORDER BY id;
SELECT 'G public.p47_shadow rows' AS step, id, v FROM public.p47_shadow ORDER BY id;
SET search_path TO pg_temp, public;
SELECT 'G unqualified rows (pg_temp first)' AS step, id, v FROM p47_shadow ORDER BY id;
INSERT INTO p47_shadow VALUES (4, 'unqualified-pgtemp-first');
SELECT 'G unqualified rows after 2nd insert' AS step, id, v FROM p47_shadow ORDER BY id;
SELECT 'G public.p47_shadow rows after 2nd insert' AS step, id, v FROM public.p47_shadow ORDER BY id;
RESET search_path;

\echo == H. volatile function writing persistent table / nextval on persistent sequence / advisory lock / pg_sleep under read-only ==
SELECT p47_volatile_write();
SELECT nextval('p47_seq');
SELECT pg_advisory_lock(4711) IS NOT NULL AS lock_acquired;
SELECT pg_advisory_unlock(4711) IS NOT NULL AS lock_released;
SELECT pg_sleep(0) AS sleep_ok;

\echo == I. reachability of turning read-only off inside the session ==
SET transaction_read_only = off;
SHOW transaction_read_only;
INSERT INTO p47_persist VALUES (7, 'flipped');
SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE;
SHOW transaction_read_only;
INSERT INTO p47_persist VALUES (8, 'after-session-restore');
SELECT 'I persistent rows' AS step, id, v FROM p47_persist ORDER BY id;
