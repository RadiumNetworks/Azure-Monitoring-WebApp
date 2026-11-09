-- MonitoringApp - delete ALL alerts and RCA cases (SQL Server / Azure SQL)
--
-- Back up the database and stop the app, ingestion and query-refresh agents.
-- Connect directly to the application database, not master.
-- Run this entire file as ONE batch; do not insert GO statements.
--
-- 1. Set @ExpectedDatabase to the actual application database name.
-- 2. Set @Confirmation to N'DELETE ALERTS AND RCAS'.
-- 3. Leave @CommitChanges = 0 for a full test followed by ROLLBACK.
-- 4. Only set @CommitChanges = 1 to permanently delete the data.
--
-- Deletes Alerts, AlertHistory, ParsedAlerts, all RCA cases, their prompts,
-- analysis, comments, changelogs and alert links. No date/status restriction.
-- Preserves inventory (including duplicates and status fields), alert rules,
-- users, Settings, dashboards, inbox layouts, analysis queries/jobs/reviews,
-- the logbook, schema and migration history. Retained jobs and logbook entries
-- can still contain snapshots/references to deleted alerts.
-- RcaAlertLinks may be absent on older RCA schemas; this is reported explicitly.
-- All other target tables are required. No migrations or schema changes.
-- Requires SELECT and DELETE on target tables.
--
-- Restart the app and reload open tabs afterward. New alerts can create new
-- RCAs again. Inventory systems remain visible in the graph.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname = N'<database-name>';
DECLARE @Confirmation nvarchar(32) = N'';
DECLARE @CommitChanges bit = 0;
DECLARE @Step nvarchar(256) = N'Validate target and confirmation';

IF @@TRANCOUNT <> 0
    THROW 50000, 'Run this script without an existing transaction.', 1;

IF @ExpectedDatabase IS NULL OR @ExpectedDatabase = N'<database-name>'
    THROW 50000, 'Set @ExpectedDatabase to the actual application database name.', 1;

IF DB_NAME() <> @ExpectedDatabase OR DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50000, 'Wrong database connection. Connect directly to @ExpectedDatabase.', 1;

IF @Confirmation IS NULL
    OR @Confirmation COLLATE Latin1_General_100_BIN2 <> N'DELETE ALERTS AND RCAS'
    OR DATALENGTH(@Confirmation) <> DATALENGTH(N'DELETE ALERTS AND RCAS')
    THROW 50000, 'Set @Confirmation to exactly DELETE ALERTS AND RCAS.', 1;

IF @CommitChanges IS NULL
    THROW 50000, '@CommitChanges must be 0 (rollback test) or 1 (permanent deletion).', 1;

DECLARE @Tables table
(
    [Sequence] int NOT NULL PRIMARY KEY,
    [TableName] sysname NOT NULL,
    [IsOptional] bit NOT NULL,
    [IsPresent] bit NOT NULL DEFAULT 0,
    [RowsBefore] bigint NULL,
    [RowsAfter] bigint NULL
);

INSERT INTO @Tables ([Sequence], [TableName], [IsOptional])
VALUES
    (1, N'RcaAlertLinks', 1),
    (2, N'RcaChanges', 0),
    (3, N'RcaPrompts', 0),
    (4, N'RootCauseAnalyses', 0),
    (5, N'ParsedAlerts', 0),
    (6, N'AlertHistory', 0),
    (7, N'Alerts', 0);

DECLARE @Sequence int = 1;
DECLARE @TableName sysname;
DECLARE @QualifiedName nvarchar(258);
DECLARE @IsOptional bit;
DECLARE @Sql nvarchar(max);
DECLARE @Rows bigint;
DECLARE @Message nvarchar(2048);

BEGIN TRY
    SET @Step = N'Check required tables';
    WHILE @Sequence <= 7
    BEGIN
        SELECT @TableName = [TableName], @IsOptional = [IsOptional]
        FROM @Tables WHERE [Sequence] = @Sequence;
        SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
        IF OBJECT_ID(@QualifiedName, N'U') IS NULL
        BEGIN
            IF @IsOptional = 0
            BEGIN
                SET @Message = N'Required table ' + @QualifiedName
                    + N' is missing or not visible. Verify database, RCA schema and permissions.';
                THROW 50001, @Message, 1;
            END;
            RAISERROR(N'RcaAlertLinks is absent or not visible; no link-table deletion will run. Verify permissions if using the current schema.', 10, 1) WITH NOWAIT;
        END
        ELSE
            UPDATE @Tables SET [IsPresent] = 1 WHERE [Sequence] = @Sequence;
        SET @Sequence += 1;
    END;

    BEGIN TRANSACTION;

    SET @Sequence = 1;
    WHILE @Sequence <= 7
    BEGIN
        SELECT @TableName = [TableName] FROM @Tables WHERE [Sequence] = @Sequence;
        IF EXISTS (SELECT 1 FROM @Tables WHERE [Sequence] = @Sequence AND [IsPresent] = 1)
        BEGIN
            SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
            SET @Step = N'Count and delete ' + @QualifiedName;
            RAISERROR(N'%s', 10, 1, @Step) WITH NOWAIT;
            SET @Sql = N'SELECT @Count = COUNT_BIG(*) FROM ' + @QualifiedName
                + N' WITH (TABLOCKX, HOLDLOCK); DELETE FROM ' + @QualifiedName + N';';
            EXEC sys.sp_executesql @Sql, N'@Count bigint OUTPUT', @Count = @Rows OUTPUT;
            UPDATE @Tables SET [RowsBefore] = @Rows WHERE [Sequence] = @Sequence;
        END;
        SET @Sequence += 1;
    END;

    SET @Sequence = 1;
    WHILE @Sequence <= 7
    BEGIN
        IF EXISTS (SELECT 1 FROM @Tables WHERE [Sequence] = @Sequence AND [IsPresent] = 1)
        BEGIN
            SELECT @TableName = [TableName] FROM @Tables WHERE [Sequence] = @Sequence;
            SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
            SET @Step = N'Validate empty table ' + @QualifiedName;
            SET @Sql = N'SELECT @Count = COUNT_BIG(*) FROM ' + @QualifiedName + N';';
            EXEC sys.sp_executesql @Sql, N'@Count bigint OUTPUT', @Count = @Rows OUTPUT;
            UPDATE @Tables SET [RowsAfter] = @Rows WHERE [Sequence] = @Sequence;
            IF @Rows <> 0
                THROW 50002, 'Target table is not empty. Rolling back all changes.', 1;
        END;
        SET @Sequence += 1;
    END;

    SET @Step = N'Complete transaction';
    IF @CommitChanges = 1
        COMMIT TRANSACTION;
    ELSE
        ROLLBACK TRANSACTION;

    SELECT DB_NAME() AS [DatabaseName], ORIGINAL_LOGIN() AS [ExecutedBy],
        CASE WHEN @CommitChanges = 1 THEN N'COMMITTED - all alerts and RCAs permanently deleted'
            ELSE N'ROLLED BACK - test only, no deletion persisted' END AS [Result];

    -- Rollback-mode counts describe the tested state before rollback.
    SELECT [TableName], [IsPresent], [RowsBefore],
        [RowsAfter] AS [RowsAfterDeletionBeforeCommitOrRollback]
    FROM @Tables ORDER BY [Sequence];
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    SELECT N'FAILED - transaction rolled back if active' AS [Result],
        DB_NAME() AS [DatabaseName], @Step AS [FailedStep],
        ERROR_NUMBER() AS [ErrorNumber], ERROR_MESSAGE() AS [ErrorMessage];
    THROW;
END CATCH;
