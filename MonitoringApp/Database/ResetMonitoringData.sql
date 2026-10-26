-- MonitoringApp - operational data reset (SQL Server / Azure SQL)
--
-- Back up the database and pause the app, ingestion and refresh agents first.
-- Connect directly to the application database (not master).
-- Run this entire file as ONE batch; do not insert GO statements.
--
-- 1. Set @ExpectedDatabase to the actual database name.
-- 2. Set @Confirmation to N'RESET'.
-- 3. Leave @CommitChanges = 0 for a full test followed by ROLLBACK.
-- 4. Only set @CommitChanges = 1 to permanently delete the data.
--
-- Matches DatabaseResetService: clears operational data, restores the ten
-- default alert rules, leaves analysis queries empty. Preserves authentication
-- users/passwords/roles, Settings, schema and EF migration history.
-- Requires the current application schema; missing tables are NOT skipped.
-- Requires SELECT and DELETE on operational tables and INSERT on AlertRules.
-- No TRUNCATE, schema changes, identity reseeding or automatic migrations.
-- Completion returns row counts and explicitly states COMMITTED or ROLLED BACK.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname = N'<database-name>';
DECLARE @Confirmation nvarchar(32) = N'';
DECLARE @CommitChanges bit = 0;
DECLARE @Step nvarchar(256) = N'Validate target and confirmation';

IF @@TRANCOUNT <> 0
    THROW 50000, 'Run this script without an existing transaction.', 1;

IF @ExpectedDatabase = N'<database-name>' OR @ExpectedDatabase IS NULL
    THROW 50000, 'Set @ExpectedDatabase to the actual application database name.', 1;

IF DB_NAME() <> @ExpectedDatabase OR DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50000, 'Wrong database connection. Connect directly to @ExpectedDatabase.', 1;

IF @Confirmation IS NULL
    OR @Confirmation COLLATE Latin1_General_100_BIN2 <> N'RESET'
    OR DATALENGTH(@Confirmation) <> DATALENGTH(N'RESET')
    THROW 50000, 'Set @Confirmation to exactly RESET before running the reset or rollback test.', 1;

IF @CommitChanges IS NULL
    THROW 50000, '@CommitChanges must be 0 (rollback test) or 1 (permanent reset).', 1;

DECLARE @Tables table
(
    [Sequence] int NOT NULL PRIMARY KEY,
    [TableName] sysname NOT NULL,
    [RowsBefore] bigint NULL,
    [RowsAfter] bigint NULL
);

INSERT INTO @Tables ([Sequence], [TableName])
VALUES
    (1, N'RcaAlertLinks'),
    (2, N'AgentAnalysisReviewEvents'),
    (3, N'AgentAnalysisJobs'),
    (4, N'AnalysisDashboardTemplates'),
    (5, N'AnalysisDashboards'),
    (6, N'InboxLayouts'),
    (7, N'AnalysisQueryWorkspaces'),
    (8, N'AnalysisQueryVersions'),
    (9, N'AnalysisQueryDefinitions'),
    (10, N'LogbookEntries'),
    (11, N'ParsedAlerts'),
    (12, N'AlertHistory'),
    (13, N'Alerts'),
    (14, N'ComputerInventory'),
    (15, N'AlertRules'),
    (16, N'AnalysisQueryCategories'),
    (17, N'RcaChanges'),
    (18, N'RcaPrompts'),
    (19, N'RootCauseAnalyses');

DECLARE @Sequence int = 1;
DECLARE @TableName sysname;
DECLARE @QualifiedName nvarchar(258);
DECLARE @Sql nvarchar(max);
DECLARE @Rows bigint;
DECLARE @Message nvarchar(2048);

BEGIN TRY
    SET @Step = N'Check required tables';
    WHILE @Sequence <= 19
    BEGIN
        SELECT @TableName = [TableName] FROM @Tables WHERE [Sequence] = @Sequence;
        SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
        IF OBJECT_ID(@QualifiedName, N'U') IS NULL
        BEGIN
            SET @Message = N'Required table ' + @QualifiedName
                + N' is missing or not visible to this login. Verify database, schema version and permissions.';
            THROW 50001, @Message, 1;
        END;
        SET @Sequence += 1;
    END;

    BEGIN TRANSACTION;

    SET @Sequence = 1;
    WHILE @Sequence <= 19
    BEGIN
        SELECT @TableName = [TableName] FROM @Tables WHERE [Sequence] = @Sequence;
        SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
        SET @Step = N'Count and delete ' + @QualifiedName;
        RAISERROR(N'%s', 10, 1, @Step) WITH NOWAIT;

        SET @Sql = N'SELECT @Count = COUNT_BIG(*) FROM ' + @QualifiedName
            + N' WITH (TABLOCKX, HOLDLOCK); DELETE FROM ' + @QualifiedName + N';';
        EXEC sys.sp_executesql @Sql, N'@Count bigint OUTPUT', @Count = @Rows OUTPUT;
        UPDATE @Tables SET [RowsBefore] = @Rows WHERE [Sequence] = @Sequence;
        SET @Sequence += 1;
    END;

    SET @Step = N'Restore ten default alert rules';
    RAISERROR(N'%s', 10, 1, @Step) WITH NOWAIT;
    -- Separate scope makes missing columns and other schema errors catchable.
    EXEC sys.sp_executesql N'
        INSERT INTO [dbo].[AlertRules]
            ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains],
             [QueryResultType], [ConditionType], [Threshold], [FailedItemName],
             [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole],
             [IsCritical], [AnalysisClassification])
        VALUES
            (''d7ab8e49-2cfa-4a2e-a635-964bee230001'', N''Inventory heartbeat freshness'', 1, 0,
             N''InventoryHeartbeatFreshness'', N'''', N'''', N'''', 5, N'''', N'''', 0, 0, N''info'', N'''', 0, N''None''),
            (''7b28f935-087b-4c20-8635-84e2c65484e9'', N''Inventory Heartbeat Status'', 1, 1,
             N''InventoryStatus'', N''Inventory Heartbeat Status'', N''InventoryHeartbeat'', N''AnyMatchingAlert'',
             0, N'''', N'''', 0, 0, N''info'', N'''', 0, N''None''),
            (''d82b566a-ce6e-4201-b7b9-9a366426e7ba'', N''Suppress healthy DCPort results'', 1, 5,
             N''Categorization'', N'''', N''DCPort'', N''RowCountEquals'', 0, N'''',
             N''Suppressed alerts'', 0, 1, N''info'', N'''', 0, N''Suppressed''),
            (''c4c346f4-2f67-465e-94b0-c018a6de0001'', N''Domain controller majority indicates domain service outage'', 1, 10,
             N''Categorization'', N'''', N'''', N''DomainControllerPercentageInDomain'', 50, N'''',
             N''Domain Service Outage'', 0, 0, N''failure'', N'''', 1, N''SystemOutage''),
            (''c4c346f4-2f67-465e-94b0-c018a6de0002'', N''All site domain controllers indicate site outage'', 1, 20,
             N''Categorization'', N'''', N'''', N''AllDomainControllersInSite'', 0, N'''',
             N''Domain Service Site Outage'', 0, 0, N''failure'', N'''', 1, N''SystemOutage''),
            (''d82b566a-ce6e-4201-b7b9-9a366426e7b8'', N''Suppress isolated DFSREvent failure'', 1, 20,
             N''Categorization'', N''DCDiag'', N''DCDiag'', N''OnlyFailedItems'', 0, N''DFSREvent'',
             N''Suppressed alerts'', 0, 1, N''info'', N'''', 0, N''Suppressed''),
            (''47a96c56-ccf5-4f4e-97ce-6a72bb462f91'', N''Port failures indicate system outage'', 1, 30,
             N''Categorization'', N''Port'', N''DCPort'', N''RowCountGreaterThan'', 10, N'''',
             N''System Outage'', 1, 0, N''failure'', N'''', 1, N''SystemOutage''),
            (''d82b566a-ce6e-4201-b7b9-9a366426e7b9'', N''Suppress healthy DCDiag results'', 1, 30,
             N''Categorization'', N''DCDiag'', N''DCDiag'', N''NoFailedItems'', 0, N'''',
             N''Suppressed alerts'', 0, 1, N''info'', N'''', 0, N''Suppressed''),
            (''956fbb9c-cadb-4f49-ad1e-78c09a8a1301'', N''DCDiag targets are domain controllers'', 1, 100,
             N''InventoryRoleAssignment'', N'''', N''DCDiag'', N'''', 0, N'''', N'''',
             0, 0, N''info'', N''Domain Controller'', 0, N''None''),
            (''956fbb9c-cadb-4f49-ad1e-78c09a8a1302'', N''Replication targets are domain controllers'', 1, 110,
             N''InventoryRoleAssignment'', N'''', N''Replication'', N'''', 0, N'''', N'''',
             0, 0, N''info'', N''Domain Controller'', 0, N''None'');';

    SET @Sequence = 1;
    WHILE @Sequence <= 19
    BEGIN
        SELECT @TableName = [TableName] FROM @Tables WHERE [Sequence] = @Sequence;
        SET @QualifiedName = N'[dbo].' + QUOTENAME(@TableName);
        SET @Step = N'Validate final row count for ' + @QualifiedName;
        SET @Sql = N'SELECT @Count = COUNT_BIG(*) FROM ' + @QualifiedName + N';';
        EXEC sys.sp_executesql @Sql, N'@Count bigint OUTPUT', @Count = @Rows OUTPUT;
        UPDATE @Tables SET [RowsAfter] = @Rows WHERE [Sequence] = @Sequence;
        IF (@TableName = N'AlertRules' AND @Rows <> 10)
            OR (@TableName <> N'AlertRules' AND @Rows <> 0)
            THROW 50002, 'Unexpected row count after reset. Rolling back all changes.', 1;
        SET @Sequence += 1;
    END;

    SET @Step = N'Complete transaction';
    IF @CommitChanges = 1
        COMMIT TRANSACTION;
    ELSE
        ROLLBACK TRANSACTION;

    SELECT
        DB_NAME() AS [DatabaseName],
        ORIGINAL_LOGIN() AS [ExecutedBy],
        CASE WHEN @CommitChanges = 1 THEN N'COMMITTED - operational data permanently reset'
            ELSE N'ROLLED BACK - test only, no reset changes persisted' END AS [Result];

    -- In rollback mode these counts describe the tested state BEFORE rollback.
    SELECT [TableName], [RowsBefore], [RowsAfter] AS [RowsAfterResetBeforeCommitOrRollback]
    FROM @Tables ORDER BY [Sequence];
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    SELECT
        N'FAILED - transaction rolled back if active' AS [Result],
        DB_NAME() AS [DatabaseName],
        ORIGINAL_LOGIN() AS [ExecutedBy],
        @Step AS [FailedStep],
        ERROR_NUMBER() AS [ErrorNumber],
        ERROR_SEVERITY() AS [ErrorSeverity],
        ERROR_STATE() AS [ErrorState],
        ERROR_PROCEDURE() AS [ErrorProcedure],
        ERROR_LINE() AS [ErrorLine],
        ERROR_MESSAGE() AS [ErrorMessage];
    THROW;
END CATCH;
