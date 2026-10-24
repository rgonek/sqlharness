SET NOCOUNT ON;
SELECT N'VERSION' AS marker, CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version, CONVERT(nvarchar(128), SERVERPROPERTY('Edition')) AS edition, CONVERT(nvarchar(128), SERVERPROPERTY('EngineEdition')) AS engine_edition;
SET STATISTICS IO ON;
SET STATISTICS XML ON;
PRINT N'CASE=table_variable_alone_repeat3';
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
GO
PRINT N'CASE=two_table_variables_repeat3';
EXEC sys.sp_executesql N'DECLARE @v1 table (id int NOT NULL); DECLARE @v2 table (id int NOT NULL); INSERT @v1 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); INSERT @v2 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT (SELECT COUNT_BIG(*) FROM @v1) + (SELECT COUNT_BIG(*) FROM @v2);';
EXEC sys.sp_executesql N'DECLARE @v1 table (id int NOT NULL); DECLARE @v2 table (id int NOT NULL); INSERT @v1 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); INSERT @v2 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT (SELECT COUNT_BIG(*) FROM @v1) + (SELECT COUNT_BIG(*) FROM @v2);';
EXEC sys.sp_executesql N'DECLARE @v1 table (id int NOT NULL); DECLARE @v2 table (id int NOT NULL); INSERT @v1 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); INSERT @v2 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT (SELECT COUNT_BIG(*) FROM @v1) + (SELECT COUNT_BIG(*) FROM @v2);';
GO
PRINT N'CASE=explicit_hex_temp_repeat3';
CREATE TABLE #ABCDEF12 (id int NOT NULL);
INSERT #ABCDEF12 SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n);
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
SELECT N'CATALOG_HEX', o.object_id, o.name FROM tempdb.sys.objects AS o WHERE o.object_id = OBJECT_ID(N'tempdb..#ABCDEF12');
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
DROP TABLE #ABCDEF12;
GO
PRINT N'CASE=conventional_temp_repeat3';
CREATE TABLE #p104_temp (id int NOT NULL);
INSERT #p104_temp SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n);
SELECT COUNT_BIG(*) FROM #p104_temp WHERE id > 0;
SELECT N'CATALOG_CONVENTIONAL', o.object_id, o.name FROM tempdb.sys.objects AS o WHERE o.object_id = OBJECT_ID(N'tempdb..#p104_temp');
SELECT COUNT_BIG(*) FROM #p104_temp WHERE id > 0;
SELECT COUNT_BIG(*) FROM #p104_temp WHERE id > 0;
DROP TABLE #p104_temp;
GO
PRINT N'CASE=mixed_variable_and_hex_temp_repeat3';
CREATE TABLE #ABCDEF12 (id int NOT NULL);
INSERT #ABCDEF12 SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n);
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
SELECT N'CATALOG_MIXED_HEX', o.object_id, o.name FROM tempdb.sys.objects AS o WHERE o.object_id = OBJECT_ID(N'tempdb..#ABCDEF12');
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
EXEC sys.sp_executesql N'DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;';
SELECT COUNT_BIG(*) FROM #ABCDEF12 WHERE id > 0;
DROP TABLE #ABCDEF12;
GO
PRINT N'CASE=nested_dynamic_batch_loop3';
DECLARE @i int = 1;
WHILE @i <= 3
BEGIN
    EXEC sys.sp_executesql N'EXEC sys.sp_executesql N''DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM @v WHERE id > 0;'';';
    SET @i += 1;
END;
GO
PRINT N'CASE=session_temp_procedure_repeat3';
EXEC(N'CREATE PROCEDURE #p104_probe AS BEGIN SET NOCOUNT ON; CREATE TABLE #ABCDEF12 (id int NOT NULL); CREATE TABLE #p104_temp (id int NOT NULL); INSERT #ABCDEF12 SELECT TOP (333) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); INSERT #p104_temp SELECT TOP (333) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); DECLARE @v table (id int NOT NULL); INSERT @v SELECT TOP (333) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n) CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n); SELECT COUNT_BIG(*) FROM #ABCDEF12; SELECT COUNT_BIG(*) FROM #p104_temp; SELECT COUNT_BIG(*) FROM @v; SELECT N''PROC_CATALOG_HEX'', o.object_id, o.name FROM tempdb.sys.objects AS o WHERE o.object_id = OBJECT_ID(N''tempdb..#ABCDEF12''); SELECT N''PROC_CATALOG_CONVENTIONAL'', o.object_id, o.name FROM tempdb.sys.objects AS o WHERE o.object_id = OBJECT_ID(N''tempdb..#p104_temp''); DROP TABLE #ABCDEF12; DROP TABLE #p104_temp; END;');
EXEC #p104_probe;
EXEC #p104_probe;
EXEC #p104_probe;
DROP PROCEDURE #p104_probe;
SET STATISTICS XML OFF;
SET STATISTICS IO OFF;
