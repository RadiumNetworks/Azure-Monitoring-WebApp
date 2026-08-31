/*
    MonitoringApp - Azure SQL setup template

    Replace <database-name> in both sections with the Azure SQL database name.

    This file contains two sections because Azure SQL Database does not support
    switching from master to a user database with USE.

    1. Run SECTION 1 while connected to the master database.
    2. Open a new connection to the configured database.
    3. Run SECTION 2 in that connection.

    The script is idempotent and can be executed again safely.
*/

/* ========================================================================== */
/* SECTION 1: Run against the master database                                 */
/* ========================================================================== */

DECLARE @DatabaseName sysname = N'<database-name>';

IF @DatabaseName = N'<' + N'database-name' + N'>'
BEGIN
    THROW 50000, 'Replace <database-name> before executing this script.', 1;
END;

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    EXEC(N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';');
END;
GO

/*
    STOP HERE.

    Reconnect to the configured database before executing SECTION 2.
*/

/* ========================================================================== */
/* SECTION 2: Run against the configured database                             */
/* ========================================================================== */

DECLARE @DatabaseName sysname = N'<database-name>';

IF @DatabaseName = N'<' + N'database-name' + N'>'
BEGIN
    THROW 50000, 'Replace <database-name> before executing this script.', 1;
END;

IF DB_NAME() <> @DatabaseName
BEGIN
    THROW 50001, 'SECTION 2 must be executed in the configured database.', 1;
END;
GO

IF OBJECT_ID(N'[dbo].[Alerts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Alerts]
    (
        [Id]               uniqueidentifier NOT NULL,
        [ReceivedAt]       datetimeoffset    NOT NULL,
        [AlertId]          nvarchar(512)     NOT NULL,
        [Name]             nvarchar(512)     NOT NULL,
        [Severity]         nvarchar(64)      NOT NULL,
        [Status]           nvarchar(64)      NOT NULL,
        [SignalType]       nvarchar(128)     NOT NULL,
        [MonitorCondition] nvarchar(64)      NOT NULL,
        [Target]           nvarchar(2048)    NOT NULL,
        [ResourceGroup]    nvarchar(256)     NOT NULL,
        [SubscriptionId]   nvarchar(64)      NOT NULL,
        [FiredAt]          datetimeoffset    NULL,
        [Description]      nvarchar(4000)    NOT NULL,
        [SearchResultsUrl] nvarchar(2048)    NOT NULL,
        [Comments]         nvarchar(4000)     NOT NULL
            CONSTRAINT [DF_Alerts_Comments] DEFAULT N'',
        [RawJson]          nvarchar(max)     NOT NULL,
        CONSTRAINT [PK_Alerts] PRIMARY KEY ([Id])
    );
END;
GO

IF COL_LENGTH(N'[dbo].[Alerts]', N'Comments') IS NULL
BEGIN
    ALTER TABLE [dbo].[Alerts]
        ADD [Comments] nvarchar(4000) NOT NULL
            CONSTRAINT [DF_Alerts_Comments] DEFAULT N'';
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_Alerts_AlertId_MonitorCondition'
      AND [object_id] = OBJECT_ID(N'[dbo].[Alerts]')
)
BEGIN
    CREATE INDEX [IX_Alerts_AlertId_MonitorCondition]
        ON [dbo].[Alerts] ([AlertId], [MonitorCondition]);
END;
GO

IF OBJECT_ID(N'[dbo].[AlertRules]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AlertRules]
    (
        [Id]                uniqueidentifier NOT NULL,
        [Name]              nvarchar(256)     NOT NULL,
        [Enabled]           bit               NOT NULL,
        [Priority]          int               NOT NULL,
        [RuleType]          nvarchar(64)      NOT NULL CONSTRAINT [DF_AlertRules_RuleType] DEFAULT N'Categorization',
        [AlertNameContains] nvarchar(256)     NOT NULL,
        [QueryResultType]   nvarchar(128)     NOT NULL,
        [ConditionType]     nvarchar(64)      NOT NULL,
        [Threshold]         int               NOT NULL,
        [FailedItemName]    nvarchar(256)     NOT NULL,
        [CategoryName]      nvarchar(256)     NOT NULL,
        [ApplyToTarget]     bit               NOT NULL,
        [Collapsed]         bit               NOT NULL,
        [Tone]              nvarchar(32)      NOT NULL,
        [InventoryRole]     nvarchar(256)     NOT NULL CONSTRAINT [DF_AlertRules_InventoryRole] DEFAULT N'',
        CONSTRAINT [PK_AlertRules] PRIMARY KEY ([Id])
    );
END;
GO

IF OBJECT_ID(N'[dbo].[ComputerInventory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ComputerInventory]
    (
        [SubscriptionId] nvarchar(64)  NOT NULL,
        [Domain]         nvarchar(256) NULL,
        [Site]           nvarchar(256) NULL,
        [ResourceGroup]  nvarchar(256) NULL,
        [Role]           nvarchar(256) NULL,
        [Computer]       nvarchar(256) NOT NULL,
        CONSTRAINT [PK_ComputerInventory]
            PRIMARY KEY ([SubscriptionId], [Computer])
    );
END;
GO

IF COL_LENGTH(N'[dbo].[AlertRules]', N'RuleType') IS NULL
BEGIN
    ALTER TABLE [dbo].[AlertRules]
        ADD [RuleType] nvarchar(64) NOT NULL
            CONSTRAINT [DF_AlertRules_RuleType] DEFAULT N'Categorization';
END;

IF COL_LENGTH(N'[dbo].[AlertRules]', N'InventoryRole') IS NULL
BEGIN
    ALTER TABLE [dbo].[AlertRules]
        ADD [InventoryRole] nvarchar(256) NOT NULL
            CONSTRAINT [DF_AlertRules_InventoryRole] DEFAULT N'';
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'ResourceGroup') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [ResourceGroup] nvarchar(256) NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'Role') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [Role] nvarchar(256) NULL;
END;
GO

IF OBJECT_ID(N'[dbo].[ParsedAlerts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ParsedAlerts]
    (
        [Id]                      uniqueidentifier NOT NULL,
        [FiredDateTime]           datetimeoffset   NULL,
        [AlertId]                 nvarchar(512)     NOT NULL,
        [OriginalAlertId]         nvarchar(512)     NOT NULL,
        [Severity]                nvarchar(64)      NOT NULL,
        [MonitorCondition]        nvarchar(64)      NOT NULL,
        [Dimensions]              nvarchar(max)     NOT NULL,
        [SearchQuery]             nvarchar(max)     NOT NULL,
        [QueryResults]            nvarchar(max)     NOT NULL,
        [AlertName]               nvarchar(512)     NOT NULL,
        [ResourceGroup]           nvarchar(256)     NOT NULL,
        [InventorySubscriptionId] nvarchar(64)      NULL,
        [InventoryComputer]       nvarchar(256)     NULL,
        CONSTRAINT [PK_ParsedAlerts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ParsedAlerts_Alerts_Id]
            FOREIGN KEY ([Id]) REFERENCES [dbo].[Alerts] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ParsedAlerts_ComputerInventory_InventorySubscriptionId_InventoryComputer]
            FOREIGN KEY ([InventorySubscriptionId], [InventoryComputer])
            REFERENCES [dbo].[ComputerInventory] ([SubscriptionId], [Computer]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParsedAlerts_AlertId'
      AND [object_id] = OBJECT_ID(N'[dbo].[ParsedAlerts]')
)
BEGIN
    CREATE INDEX [IX_ParsedAlerts_AlertId] ON [dbo].[ParsedAlerts] ([AlertId]);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParsedAlerts_InventorySubscriptionId_InventoryComputer'
      AND [object_id] = OBJECT_ID(N'[dbo].[ParsedAlerts]')
)
BEGIN
    CREATE INDEX [IX_ParsedAlerts_InventorySubscriptionId_InventoryComputer]
        ON [dbo].[ParsedAlerts] ([InventorySubscriptionId], [InventoryComputer]);
END;
GO

IF OBJECT_ID(N'[dbo].[AuthenticationUsers]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AuthenticationUsers]
    (
        [Username]     nvarchar(128) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [Role]         nvarchar(16)  NOT NULL CONSTRAINT [DF_AuthenticationUsers_Role] DEFAULT N'Admin',
        CONSTRAINT [PK_AuthenticationUsers] PRIMARY KEY ([Username])
    );
END;
GO

IF COL_LENGTH(N'[dbo].[AuthenticationUsers]', N'Role') IS NULL
BEGIN
    ALTER TABLE [dbo].[AuthenticationUsers]
        ADD [Role] nvarchar(16) NOT NULL
            CONSTRAINT [DF_AuthenticationUsers_Role] DEFAULT N'Admin';
END;
GO

IF OBJECT_ID(N'[dbo].[Settings]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Settings]
    (
        [Name]      nvarchar(128) NOT NULL,
        [JsonValue] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Settings] PRIMARY KEY ([Name])
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'Authentication')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES (N'Authentication', N'{"Type":"sql"}');
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'AlertHistory')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES (N'AlertHistory', N'{"Days":7}');
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'AlertGraph')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES
    (
        N'AlertGraph',
        N'{"Layer1":[{"Value":"Subscription","Label":"Subscription"},{"Value":"ResourceGroup","Label":"Resourcegroup"}],"Layer2":[{"Value":"AlertName","Label":"AlertName"},{"Value":"ResourceGroup","Label":"Resourcegroup"},{"Value":"Site","Label":"Site"}],"Layer3":[{"Value":"Target","Label":"Target"},{"Value":"Site","Label":"Site"}],"DefaultLayer1":"ResourceGroup","DefaultLayer2":"Site","DefaultLayer3":"Target"}'
    );
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'AlertSeverityDisplay')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES
    (
        N'AlertSeverityDisplay',
        N'{"Severities":[{"Severity":"Sev0","Color":"red","FontStyle":"bold"},{"Severity":"Sev1","Color":"red","FontStyle":"bold"},{"Severity":"Sev2","Color":"yellow","FontStyle":"bold"},{"Severity":"Sev3","Color":"gray","FontStyle":"normal"},{"Severity":"Sev4","Color":"green","FontStyle":"normal"}],"Default":{"Color":"black","FontStyle":"normal"}}'
    );
END;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[dbo].[ComputerInventory]')
      AND [name] = N'Domain'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [dbo].[ComputerInventory]
        ALTER COLUMN [Domain] nvarchar(256) NULL;
END;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[dbo].[ComputerInventory]')
      AND [name] = N'Site'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [dbo].[ComputerInventory]
        ALTER COLUMN [Site] nvarchar(256) NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_AlertRules_Name'
      AND [object_id] = OBJECT_ID(N'[dbo].[AlertRules]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_AlertRules_Name]
        ON [dbo].[AlertRules] ([Name]);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_AlertRules_Enabled_Priority'
      AND [object_id] = OBJECT_ID(N'[dbo].[AlertRules]')
)
BEGIN
    CREATE INDEX [IX_AlertRules_Enabled_Priority]
        ON [dbo].[AlertRules] ([Enabled], [Priority]);
END;
GO

/*
    Seed and synchronize the built-in categorization rules. Re-running this
    script restores their required values while leaving additional rules intact.
*/
UPDATE [dbo].[AlertRules]
SET [Name] = N'Port failures indicate system outage',
    [Enabled] = 1,
    [Priority] = 10,
    [RuleType] = N'Categorization',
    [InventoryRole] = N'',
    [AlertNameContains] = N'Port',
    [QueryResultType] = N'DCPort',
    [ConditionType] = N'RowCountGreaterThan',
    [Threshold] = 10,
    [FailedItemName] = N'',
    [CategoryName] = N'System Outage',
    [ApplyToTarget] = 1,
    [Collapsed] = 0,
    [Tone] = N'failure'
WHERE [Id] = '47a96c56-ccf5-4f4e-97ce-6a72bb462f91';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone])
    VALUES
        ('47a96c56-ccf5-4f4e-97ce-6a72bb462f91', N'Port failures indicate system outage', 1, 10, N'Port', N'DCPort', N'RowCountGreaterThan', 10, N'', N'System Outage', 1, 0, N'failure');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'Suppress isolated DFSREvent failure',
    [Enabled] = 1,
    [Priority] = 20,
    [RuleType] = N'Categorization',
    [InventoryRole] = N'',
    [AlertNameContains] = N'DCDiag',
    [QueryResultType] = N'DCDiag',
    [ConditionType] = N'OnlyFailedItem',
    [Threshold] = 0,
    [FailedItemName] = N'DFSREvent',
    [CategoryName] = N'Suppressed alerts',
    [ApplyToTarget] = 0,
    [Collapsed] = 1,
    [Tone] = N'info'
WHERE [Id] = 'd82b566a-ce6e-4201-b7b9-9a366426e7b8';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone])
    VALUES
        ('d82b566a-ce6e-4201-b7b9-9a366426e7b8', N'Suppress isolated DFSREvent failure', 1, 20, N'DCDiag', N'DCDiag', N'OnlyFailedItem', 0, N'DFSREvent', N'Suppressed alerts', 0, 1, N'info');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'DCDiag targets are domain controllers',
    [Enabled] = 1,
    [Priority] = 100,
    [RuleType] = N'InventoryRoleAssignment',
    [AlertNameContains] = N'',
    [QueryResultType] = N'DCDiag',
    [ConditionType] = N'',
    [Threshold] = 0,
    [FailedItemName] = N'',
    [CategoryName] = N'',
    [ApplyToTarget] = 0,
    [Collapsed] = 0,
    [Tone] = N'info',
    [InventoryRole] = N'domaincontrollers'
WHERE [Id] = '956fbb9c-cadb-4f49-ad1e-78c09a8a1301';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole])
    VALUES
        ('956fbb9c-cadb-4f49-ad1e-78c09a8a1301', N'DCDiag targets are domain controllers', 1, 100, N'InventoryRoleAssignment', N'', N'DCDiag', N'', 0, N'', N'', 0, 0, N'info', N'domaincontrollers');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'Replication targets are domain controllers',
    [Enabled] = 1,
    [Priority] = 110,
    [RuleType] = N'InventoryRoleAssignment',
    [AlertNameContains] = N'',
    [QueryResultType] = N'Replication',
    [ConditionType] = N'',
    [Threshold] = 0,
    [FailedItemName] = N'',
    [CategoryName] = N'',
    [ApplyToTarget] = 0,
    [Collapsed] = 0,
    [Tone] = N'info',
    [InventoryRole] = N'domaincontrollers'
WHERE [Id] = '956fbb9c-cadb-4f49-ad1e-78c09a8a1302';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole])
    VALUES
        ('956fbb9c-cadb-4f49-ad1e-78c09a8a1302', N'Replication targets are domain controllers', 1, 110, N'InventoryRoleAssignment', N'', N'Replication', N'', 0, N'', N'', 0, 0, N'info', N'domaincontrollers');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_Alerts_ReceivedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[Alerts]')
)
BEGIN
    CREATE INDEX [IX_Alerts_ReceivedAt]
        ON [dbo].[Alerts] ([ReceivedAt]);
END;
GO

IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[__EFMigrationsHistory]
    (
        [MigrationId]    nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32)  NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820185005_InitialAlertSchema'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260820185005_InitialAlertSchema', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821094736_AddAlertComments'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260821094736_AddAlertComments', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260829090000_AddAlertRules'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260829090000_AddAlertRules', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260829110000_ExpandSystemOutageRuleByDefault'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260829110000_ExpandSystemOutageRuleByDefault', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260829120000_AddComputerInventory'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260829120000_AddComputerInventory', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260829130000_AllowMissingComputerInventoryLocation'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260829130000_AllowMissingComputerInventoryLocation', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830120000_AddAuthenticationUsers'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260830120000_AddAuthenticationUsers', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830150000_AddAuthenticationUserRole'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260830150000_AddAuthenticationUserRole', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831100000_AddSettings'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831100000_AddSettings', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831120000_AddAlertSeverityDisplaySetting'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831120000_AddAlertSeverityDisplaySetting', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831130000_DefaultAuthenticationToSql'
)
BEGIN
    UPDATE [dbo].[Settings]
    SET [JsonValue] = N'{"Type":"sql"}'
    WHERE [Name] = N'Authentication'
      AND JSON_VALUE([JsonValue], '$.Type') = N'open';

    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831130000_DefaultAuthenticationToSql', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831140000_AddParsedAlerts'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831140000_AddParsedAlerts', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831150000_AddInventoryRolesAndFilters'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831150000_AddInventoryRolesAndFilters', N'9.0.19');
END;
GO

/*
    Optional: grant the App Service user-assigned managed identity runtime access.
    Replace <managed-identity-name>, remove the comment markers, and execute
    this block as the Microsoft Entra administrator of the SQL server.

CREATE USER [<managed-identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE [db_datareader] ADD MEMBER [<managed-identity-name>];
ALTER ROLE [db_datawriter] ADD MEMBER [<managed-identity-name>];
*/

SELECT
    DB_NAME() AS [DatabaseName],
    OBJECT_ID(N'[dbo].[Alerts]', N'U') AS [AlertsTableObjectId],
    OBJECT_ID(N'[dbo].[AlertRules]', N'U') AS [AlertRulesTableObjectId],
    OBJECT_ID(N'[dbo].[ComputerInventory]', N'U') AS [ComputerInventoryTableObjectId],
    OBJECT_ID(N'[dbo].[AuthenticationUsers]', N'U') AS [AuthenticationUsersTableObjectId],
    OBJECT_ID(N'[dbo].[Settings]', N'U') AS [SettingsTableObjectId],
    OBJECT_ID(N'[dbo].[ParsedAlerts]', N'U') AS [ParsedAlertsTableObjectId],
    (SELECT COUNT(*) FROM [dbo].[AlertRules]) AS [AlertRuleCount],
    (SELECT COUNT(*) FROM [dbo].[ComputerInventory]) AS [ComputerInventoryCount],
    (SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]) AS [AppliedMigrations];
GO

SELECT
    [Id],
    [Name],
    [Enabled],
    [Priority],
    [RuleType],
    [AlertNameContains],
    [QueryResultType],
    [ConditionType],
    [Threshold],
    [FailedItemName],
    [CategoryName],
    [ApplyToTarget],
    [Collapsed],
    [Tone],
    [InventoryRole]
FROM [dbo].[AlertRules]
ORDER BY [Priority], [Name];
GO