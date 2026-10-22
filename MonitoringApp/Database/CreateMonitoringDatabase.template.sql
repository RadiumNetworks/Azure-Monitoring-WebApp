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

IF OBJECT_ID(N'[dbo].[AlertHistory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AlertHistory]
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
        [Comments]         nvarchar(4000)    NOT NULL,
        [RawJson]          nvarchar(max)     NOT NULL,
        CONSTRAINT [PK_AlertHistory] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_AlertHistory_AlertId'
      AND [object_id] = OBJECT_ID(N'[dbo].[AlertHistory]')
)
BEGIN
    CREATE INDEX [IX_AlertHistory_AlertId]
        ON [dbo].[AlertHistory] ([AlertId]);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_AlertHistory_ReceivedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[AlertHistory]')
)
BEGIN
    CREATE INDEX [IX_AlertHistory_ReceivedAt]
        ON [dbo].[AlertHistory] ([ReceivedAt]);
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
        [IsCritical]        bit               NOT NULL CONSTRAINT [DF_AlertRules_IsCritical] DEFAULT 0,
        [AnalysisClassification] nvarchar(64) NOT NULL CONSTRAINT [DF_AlertRules_AnalysisClassification] DEFAULT N'None',
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
        [PrivateIP]      nvarchar(256) NULL,
        [LastHeartbeatAt] datetimeoffset NULL,
        [NtdsFileSizeInGb] float NULL,
        [CertificatesJson] nvarchar(max) NOT NULL CONSTRAINT [DF_ComputerInventory_CertificatesJson] DEFAULT N'[]',
        [ServicesJson]    nvarchar(max) NOT NULL CONSTRAINT [DF_ComputerInventory_ServicesJson] DEFAULT N'[]',
        [Maintenance]    bit NOT NULL CONSTRAINT [DF_ComputerInventory_Maintenance] DEFAULT 0,
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

IF COL_LENGTH(N'[dbo].[AlertRules]', N'IsCritical') IS NULL
BEGIN
    ALTER TABLE [dbo].[AlertRules]
        ADD [IsCritical] bit NOT NULL
            CONSTRAINT [DF_AlertRules_IsCritical] DEFAULT 0;
END;

IF COL_LENGTH(N'[dbo].[AlertRules]', N'AnalysisClassification') IS NULL
BEGIN
    ALTER TABLE [dbo].[AlertRules]
        ADD [AnalysisClassification] nvarchar(64) NOT NULL
            CONSTRAINT [DF_AlertRules_AnalysisClassification] DEFAULT N'None';
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'ResourceGroup') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [ResourceGroup] nvarchar(256) NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'Maintenance') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [Maintenance] bit NOT NULL
        CONSTRAINT [DF_ComputerInventory_Maintenance] DEFAULT 0;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'Role') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [Role] nvarchar(256) NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'PrivateIP') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [PrivateIP] nvarchar(256) NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'LastHeartbeatAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [LastHeartbeatAt] datetimeoffset NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'NtdsFileSizeInGb') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [NtdsFileSizeInGb] float NULL;
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'CertificatesJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [CertificatesJson] nvarchar(max) NOT NULL
        CONSTRAINT [DF_ComputerInventory_CertificatesJson] DEFAULT N'[]';
END;

IF COL_LENGTH(N'[dbo].[ComputerInventory]', N'ServicesJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[ComputerInventory] ADD [ServicesJson] nvarchar(max) NOT NULL
        CONSTRAINT [DF_ComputerInventory_ServicesJson] DEFAULT N'[]';
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
        [QueryResultRefreshedAt]  datetimeoffset    NULL,
        [AlertName]               nvarchar(512)     NOT NULL,
        [ResourceGroup]           nvarchar(256)     NOT NULL,
        [IsCritical]              bit               NOT NULL CONSTRAINT [DF_ParsedAlerts_IsCritical] DEFAULT 0,
        [ResolvedAt]              datetimeoffset    NULL,
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

IF COL_LENGTH(N'[dbo].[ParsedAlerts]', N'IsCritical') IS NULL
BEGIN
    ALTER TABLE [dbo].[ParsedAlerts]
        ADD [IsCritical] bit NOT NULL
            CONSTRAINT [DF_ParsedAlerts_IsCritical] DEFAULT 0;
END;

IF COL_LENGTH(N'[dbo].[ParsedAlerts]', N'ResolvedAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[ParsedAlerts] ADD [ResolvedAt] datetimeoffset NULL;
END;

IF COL_LENGTH(N'[dbo].[ParsedAlerts]', N'QueryResultRefreshedAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[ParsedAlerts] ADD [QueryResultRefreshedAt] datetimeoffset NULL;
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

IF OBJECT_ID(N'[dbo].[LogbookEntries]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[LogbookEntries]
    (
        [Id]        uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset    NOT NULL,
        [User]      nvarchar(256)     NOT NULL,
        [Comment]   nvarchar(max)     NOT NULL,
        [AlertRecordId] uniqueidentifier NULL,
        CONSTRAINT [PK_LogbookEntries] PRIMARY KEY ([Id])
    );
END;
GO

IF COL_LENGTH(N'[dbo].[LogbookEntries]', N'AlertRecordId') IS NULL
BEGIN
    ALTER TABLE [dbo].[LogbookEntries] ADD [AlertRecordId] uniqueidentifier NULL;
END;
GO

IF COL_LENGTH(N'[dbo].[LogbookEntries]', N'Comment') <> -1
BEGIN
    ALTER TABLE [dbo].[LogbookEntries] ALTER COLUMN [Comment] nvarchar(max) NOT NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_LogbookEntries_AlertRecordId'
      AND [object_id] = OBJECT_ID(N'[dbo].[LogbookEntries]')
)
BEGIN
    CREATE INDEX [IX_LogbookEntries_AlertRecordId]
        ON [dbo].[LogbookEntries] ([AlertRecordId]);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_LogbookEntries_CreatedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[LogbookEntries]')
)
BEGIN
    CREATE INDEX [IX_LogbookEntries_CreatedAt]
        ON [dbo].[LogbookEntries] ([CreatedAt]);
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisQueryDefinitions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisQueryDefinitions]
    (
        [Id] uniqueidentifier NOT NULL,
        [Key] nvarchar(128) NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [Description] nvarchar(2000) NOT NULL,
        [Category] nvarchar(128) NOT NULL,
        [Kql] nvarchar(max) NOT NULL,
        [Scope] nvarchar(32) NOT NULL DEFAULT N'SelectedSystems',
        [VisualizationType] nvarchar(32) NOT NULL,
        [VisualizationJson] nvarchar(max) NOT NULL DEFAULT N'{}',
        [Unit] nvarchar(64) NOT NULL,
        [DefaultTimeRangeMinutes] int NOT NULL,
        [MaxTimeRangeMinutes] int NOT NULL,
        [DefaultBinMinutes] int NOT NULL,
        [MinimumRole] nvarchar(16) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [Enabled] bit NOT NULL,
        [Version] int NOT NULL,
        [PublishedVersionId] uniqueidentifier NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(256) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AnalysisQueryDefinitions] PRIMARY KEY ([Id])
    );
END;
GO

IF COL_LENGTH(N'dbo.AnalysisQueryDefinitions', N'VisualizationJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[AnalysisQueryDefinitions]
        ADD [VisualizationJson] nvarchar(max) NOT NULL DEFAULT N'{}';
END;
GO

IF COL_LENGTH(N'dbo.AnalysisQueryDefinitions', N'Scope') IS NULL
BEGIN
    ALTER TABLE [dbo].[AnalysisQueryDefinitions]
        ADD [Scope] nvarchar(32) NOT NULL DEFAULT N'SelectedSystems';
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisQueryCategories]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisQueryCategories]
    (
        [Path] nvarchar(128) NOT NULL,
        CONSTRAINT [PK_AnalysisQueryCategories] PRIMARY KEY ([Path])
    );
    INSERT INTO [dbo].[AnalysisQueryCategories] ([Path])
    SELECT DISTINCT [Category] FROM [dbo].[AnalysisQueryDefinitions];
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisQueryVersions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisQueryVersions]
    (
        [Id] uniqueidentifier NOT NULL,
        [QueryDefinitionId] uniqueidentifier NOT NULL,
        [Version] int NOT NULL,
        [SnapshotJson] nvarchar(max) NOT NULL,
        [PublishedAt] datetimeoffset NOT NULL,
        [PublishedBy] nvarchar(256) NOT NULL,
        CONSTRAINT [PK_AnalysisQueryVersions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AnalysisQueryVersions_AnalysisQueryDefinitions_QueryDefinitionId]
            FOREIGN KEY ([QueryDefinitionId]) REFERENCES [dbo].[AnalysisQueryDefinitions] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisQueryWorkspaces]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisQueryWorkspaces]
    (
        [QueryDefinitionId] uniqueidentifier NOT NULL,
        [WorkspaceKey] nvarchar(128) NOT NULL,
        [Enabled] bit NOT NULL,
        [LastValidatedAt] datetimeoffset NULL,
        [LastValidationStatus] nvarchar(32) NOT NULL,
        [LastValidationMessage] nvarchar(1000) NOT NULL,
        CONSTRAINT [PK_AnalysisQueryWorkspaces] PRIMARY KEY ([QueryDefinitionId], [WorkspaceKey]),
        CONSTRAINT [FK_AnalysisQueryWorkspaces_AnalysisQueryDefinitions_QueryDefinitionId]
            FOREIGN KEY ([QueryDefinitionId]) REFERENCES [dbo].[AnalysisQueryDefinitions] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisQueryDefinitions_Key' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisQueryDefinitions]'))
    CREATE UNIQUE INDEX [IX_AnalysisQueryDefinitions_Key] ON [dbo].[AnalysisQueryDefinitions] ([Key]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisQueryDefinitions_Status_Enabled' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisQueryDefinitions]'))
    CREATE INDEX [IX_AnalysisQueryDefinitions_Status_Enabled] ON [dbo].[AnalysisQueryDefinitions] ([Status], [Enabled]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisQueryVersions_QueryDefinitionId_Version' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisQueryVersions]'))
    CREATE UNIQUE INDEX [IX_AnalysisQueryVersions_QueryDefinitionId_Version] ON [dbo].[AnalysisQueryVersions] ([QueryDefinitionId], [Version]);
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[AnalysisQueryDefinitions]
    WHERE [Id] = 'a1000000-0000-0000-0000-000000000001' OR [Key] = N'cpu-usage'
)
BEGIN
    INSERT INTO [dbo].[AnalysisQueryDefinitions]
        ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType], [Unit],
         [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes], [MinimumRole],
         [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
    VALUES
        ('a1000000-0000-0000-0000-000000000001', N'cpu-usage', N'CPU usage',
         N'Average total processor utilization per selected computer.', N'Performance',
         N'Perf
| where Computer in~ (_SelectedSystems)
| where ObjectName == "Processor"
| where CounterName == "% Processor Time"
| where InstanceName == "_Total"
| summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
| project Timestamp, Series, Value
| order by Timestamp asc',
         N'LineChart', N'Percent', 1440, 10080, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[AnalysisQueryDefinitions]
    WHERE [Id] = 'a1000000-0000-0000-0000-000000000002' OR [Key] = N'ram-usage'
)
BEGIN
    INSERT INTO [dbo].[AnalysisQueryDefinitions]
        ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType], [Unit],
         [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes], [MinimumRole],
         [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
    VALUES
        ('a1000000-0000-0000-0000-000000000002', N'ram-usage', N'RAM usage',
         N'Average committed memory utilization per selected computer.', N'Performance',
         N'Perf
| where Computer in~ (_SelectedSystems)
| where ObjectName == "Memory"
| where CounterName == "% Committed Bytes In Use"
| summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
| project Timestamp, Series, Value
| order by Timestamp asc',
         N'LineChart', N'Percent', 1440, 10080, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[AnalysisQueryDefinitions]
    WHERE [Id] = 'a1000000-0000-0000-0000-000000000003' OR [Key] = N'kerberos-authentications'
)
BEGIN
    INSERT INTO [dbo].[AnalysisQueryDefinitions]
        ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType], [Unit],
         [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes], [MinimumRole],
         [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
    VALUES
        ('a1000000-0000-0000-0000-000000000003', N'kerberos-authentications', N'Kerberos authentications',
         N'Counts Kerberos ticket-granting and service-ticket events 4768 and 4769.', N'Security',
         N'SecurityEvent
| where Computer in~ (_SelectedSystems)
| where EventID in (4768, 4769)
| summarize Value = count() by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer, Dimension = tostring(EventID)
| project Timestamp, Series, Value, Dimension
| order by Timestamp asc',
         N'LineChart', N'Count', 240, 1440, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisDashboards]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisDashboards]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [OwnerKey] nvarchar(256) NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [DocumentJson] nvarchar(max) NOT NULL,
        [DocumentVersion] int NOT NULL,
        [Revision] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AnalysisDashboards] PRIMARY KEY ([Id])
    );
END;
GO

IF OBJECT_ID(N'[dbo].[AnalysisDashboardTemplates]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AnalysisDashboardTemplates]
    (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [Version] int NOT NULL,
        [MinimumRole] nvarchar(16) NOT NULL,
        [Published] bit NOT NULL,
        [DocumentJson] nvarchar(max) NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(256) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AnalysisDashboardTemplates] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisDashboards_OwnerKey' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisDashboards]'))
    CREATE UNIQUE INDEX [IX_AnalysisDashboards_OwnerKey] ON [dbo].[AnalysisDashboards] ([OwnerKey]);
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisDashboardTemplates_Name_Version' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisDashboardTemplates]') AND [is_unique] = 1)
    DROP INDEX [IX_AnalysisDashboardTemplates_Name_Version] ON [dbo].[AnalysisDashboardTemplates];
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisDashboardTemplates_Name_Version' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisDashboardTemplates]'))
    CREATE INDEX [IX_AnalysisDashboardTemplates_Name_Version] ON [dbo].[AnalysisDashboardTemplates] ([Name], [Version]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AnalysisDashboardTemplates_Published' AND [object_id] = OBJECT_ID(N'[dbo].[AnalysisDashboardTemplates]'))
    CREATE INDEX [IX_AnalysisDashboardTemplates_Published] ON [dbo].[AnalysisDashboardTemplates] ([Published]);
GO

IF OBJECT_ID(N'[dbo].[AgentAnalysisJobs]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AgentAnalysisJobs]
    (
        [Id]                    uniqueidentifier NOT NULL,
        [AnalysisId]            uniqueidentifier NOT NULL,
        [IdempotencyKey]        nvarchar(64)     NOT NULL,
        [ResultVersion]         int              NOT NULL,
        [Status]                nvarchar(32)     NOT NULL,
        [StatusReason]          nvarchar(1024)   NOT NULL,
        [Revision]              int              NOT NULL,
        [Mode]                  nvarchar(32)     NOT NULL,
        [ExecutionProfile]      nvarchar(64)     NOT NULL,
        [PromptVersion]         nvarchar(64)     NOT NULL,
        [SnapshotVersion]       nvarchar(64)     NOT NULL,
        [ResultContractVersion] nvarchar(64)     NOT NULL,
        [ScopeType]             nvarchar(32)     NOT NULL,
        [ScopeValue]            nvarchar(512)    NOT NULL,
        [PrimaryEventIdsJson]   nvarchar(max)    NOT NULL,
        [SnapshotJson]          nvarchar(max)    NOT NULL,
        [ResultJson]            nvarchar(max)    NOT NULL,
        [TeamsSummaryJson]      nvarchar(max)    NOT NULL,
        [DetailedAnalysisJson]  nvarchar(max)    NOT NULL,
        [SourceIdsJson]         nvarchar(max)    NOT NULL,
        [ResultStoredAt]        datetimeoffset   NULL,
        [TeamsDeliveryStatus]   nvarchar(32)     NOT NULL,
        [TeamsDeliveredResultVersion] int       NULL,
        [TeamsDeliveredAt]      datetimeoffset   NULL,
        [SnapshotGeneratedAt]   datetimeoffset   NOT NULL,
        [DataFrom]              datetimeoffset   NOT NULL,
        [DataTo]                datetimeoffset   NOT NULL,
        [CreatedBy]             nvarchar(256)    NOT NULL,
        [CreatedAt]             datetimeoffset   NOT NULL,
        [UpdatedAt]             datetimeoffset   NOT NULL,
        [StartedAt]             datetimeoffset   NULL,
        [CompletedAt]           datetimeoffset   NULL,
        [ExpiresAt]             datetimeoffset   NOT NULL,
        [ErrorCode]             nvarchar(128)    NOT NULL,
        [ErrorMessage]          nvarchar(4000)   NOT NULL,
        CONSTRAINT [PK_AgentAnalysisJobs] PRIMARY KEY ([Id])
    );
END;
GO

IF OBJECT_ID(N'[dbo].[AgentAnalysisReviewEvents]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AgentAnalysisReviewEvents]
    (
        [Id]                 uniqueidentifier NOT NULL,
        [AgentAnalysisJobId] uniqueidentifier NOT NULL,
        [AnalysisId]         uniqueidentifier NOT NULL,
        [ResultVersion]      int              NOT NULL,
        [Action]             nvarchar(32)     NOT NULL,
        [Comment]            nvarchar(4000)   NOT NULL,
        [Actor]              nvarchar(256)    NOT NULL,
        [CreatedAt]          datetimeoffset   NOT NULL,
        CONSTRAINT [PK_AgentAnalysisReviewEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AgentAnalysisReviewEvents_AgentAnalysisJobs_AgentAnalysisJobId]
            FOREIGN KEY ([AgentAnalysisJobId]) REFERENCES [dbo].[AgentAnalysisJobs] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AgentAnalysisReviewEvents_AgentAnalysisJobId_CreatedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[AgentAnalysisReviewEvents]')
)
BEGIN
    CREATE INDEX [IX_AgentAnalysisReviewEvents_AgentAnalysisJobId_CreatedAt]
        ON [dbo].[AgentAnalysisReviewEvents] ([AgentAnalysisJobId], [CreatedAt]);
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'IdempotencyKey') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [IdempotencyKey] nvarchar(64) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_IdempotencyKey] DEFAULT N'';

    UPDATE [dbo].[AgentAnalysisJobs]
    SET [IdempotencyKey] = CONCAT(N'LEGACY-', CONVERT(nvarchar(36), [Id]))
    WHERE [IdempotencyKey] = N'';
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AgentAnalysisJobs_IdempotencyKey'
      AND [object_id] = OBJECT_ID(N'[dbo].[AgentAnalysisJobs]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_AgentAnalysisJobs_IdempotencyKey]
        ON [dbo].[AgentAnalysisJobs] ([IdempotencyKey])
        WHERE [Status] IN (N'Queued', N'Running', N'Completed');
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'ResultJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [ResultJson] nvarchar(max) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_ResultJson] DEFAULT N'';
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'TeamsSummaryJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [TeamsSummaryJson] nvarchar(max) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_TeamsSummaryJson] DEFAULT N'';
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'DetailedAnalysisJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [DetailedAnalysisJson] nvarchar(max) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_DetailedAnalysisJson] DEFAULT N'';
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'SourceIdsJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [SourceIdsJson] nvarchar(max) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_SourceIdsJson] DEFAULT N'[]';
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'ResultStoredAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs] ADD [ResultStoredAt] datetimeoffset NULL;
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'TeamsDeliveryStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs]
        ADD [TeamsDeliveryStatus] nvarchar(32) NOT NULL
            CONSTRAINT [DF_AgentAnalysisJobs_TeamsDeliveryStatus] DEFAULT N'NotRequested';
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'TeamsDeliveredResultVersion') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs] ADD [TeamsDeliveredResultVersion] int NULL;
END;
GO

IF COL_LENGTH(N'[dbo].[AgentAnalysisJobs]', N'TeamsDeliveredAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[AgentAnalysisJobs] ADD [TeamsDeliveredAt] datetimeoffset NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AgentAnalysisJobs_AnalysisId'
      AND [object_id] = OBJECT_ID(N'[dbo].[AgentAnalysisJobs]')
)
BEGIN
    CREATE INDEX [IX_AgentAnalysisJobs_AnalysisId]
        ON [dbo].[AgentAnalysisJobs] ([AnalysisId]);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AgentAnalysisJobs_CreatedBy_CreatedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[AgentAnalysisJobs]')
)
BEGIN
    CREATE INDEX [IX_AgentAnalysisJobs_CreatedBy_CreatedAt]
        ON [dbo].[AgentAnalysisJobs] ([CreatedBy], [CreatedAt]);
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AgentAnalysisJobs_Status_CreatedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[AgentAnalysisJobs]')
)
BEGIN
    CREATE INDEX [IX_AgentAnalysisJobs_Status_CreatedAt]
        ON [dbo].[AgentAnalysisJobs] ([Status], [CreatedAt]);
END;
GO

IF OBJECT_ID(N'[dbo].[InboxLayouts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[InboxLayouts]
    (
        [Id]              int IDENTITY(1,1) NOT NULL,
        [OwnerKey]        nvarchar(256)     NOT NULL,
        [Name]            nvarchar(256)     NOT NULL,
        [DocumentJson]    nvarchar(max)     NOT NULL,
        [DocumentVersion] int               NOT NULL,
        [Revision]        int               NOT NULL,
        [CreatedAt]       datetimeoffset    NOT NULL,
        [UpdatedAt]       datetimeoffset    NOT NULL,
        CONSTRAINT [PK_InboxLayouts] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_InboxLayouts_OwnerKey'
      AND [object_id] = OBJECT_ID(N'[dbo].[InboxLayouts]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_InboxLayouts_OwnerKey]
        ON [dbo].[InboxLayouts] ([OwnerKey]);
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
        N'{"Layer1":[{"Value":"Subscription","Label":"Subscription"},{"Value":"ResourceGroup","Label":"Resourcegroup"},{"Value":"Domain","Label":"Domain"},{"Value":"Role","Label":"Role"}],"Layer2":[{"Value":"AlertName","Label":"AlertName"},{"Value":"ResourceGroup","Label":"Resourcegroup"},{"Value":"Site","Label":"Site"},{"Value":"Domain","Label":"Domain"},{"Value":"Role","Label":"Role"}],"Layer3":[{"Value":"Target","Label":"Target"},{"Value":"Site","Label":"Site"},{"Value":"Domain","Label":"Domain"},{"Value":"Role","Label":"Role"}],"DefaultLayer1":"ResourceGroup","DefaultLayer2":"Site","DefaultLayer3":"Target","SiteLocations":[{"DisplayName":"New York","Latitude":40.7128,"Longitude":-74.006,"LabelOffsetX":-30,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["New York","NewYork","NYC"]},{"DisplayName":"Singapore","Latitude":1.3521,"Longitude":103.8198,"LabelOffsetX":30,"LabelOffsetY":-31,"TextAnchor":"start","Aliases":["Singapore","Singapure"]},{"DisplayName":"Frankfurt","Latitude":50.1109,"Longitude":8.6821,"LabelOffsetX":35,"LabelOffsetY":38,"TextAnchor":"start","Aliases":["Frankfurt"]},{"DisplayName":"London","Latitude":51.5074,"Longitude":-0.1278,"LabelOffsetX":-35,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["London"]}]}'
    );
END;

IF ISJSON((SELECT [JsonValue] FROM [dbo].[Settings] WHERE [Name] = N'AlertGraph')) = 1
   AND JSON_QUERY((SELECT [JsonValue] FROM [dbo].[Settings] WHERE [Name] = N'AlertGraph'), '$.SiteLocations') IS NULL
BEGIN
    UPDATE [dbo].[Settings]
    SET [JsonValue] = JSON_MODIFY(
        [JsonValue],
        '$.SiteLocations',
        JSON_QUERY(N'[{"DisplayName":"New York","Latitude":40.7128,"Longitude":-74.006,"LabelOffsetX":-30,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["New York","NewYork","NYC"]},{"DisplayName":"Singapore","Latitude":1.3521,"Longitude":103.8198,"LabelOffsetX":30,"LabelOffsetY":-31,"TextAnchor":"start","Aliases":["Singapore","Singapure"]},{"DisplayName":"Frankfurt","Latitude":50.1109,"Longitude":8.6821,"LabelOffsetX":35,"LabelOffsetY":38,"TextAnchor":"start","Aliases":["Frankfurt"]},{"DisplayName":"London","Latitude":51.5074,"Longitude":-0.1278,"LabelOffsetX":-35,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["London"]}]'))
    WHERE [Name] = N'AlertGraph';
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

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'InboxTargetOutputDisplay')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES
    (
        N'InboxTargetOutputDisplay',
        N'{"CountColors":[{"MinimumCount":1,"Color":"yellow"},{"MinimumCount":2,"Color":"orange"},{"MinimumCount":3,"Color":"red"}],"SuppressedColor":"green","CriticalOutageAlwaysRed":true,"BoldOutageNames":true,"ShowDomain":true}'
    );
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'AgentAnalysisExecution')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES
    (
        N'AgentAnalysisExecution',
        N'{"Version":1,"Mode":"CopilotPrompt","TeamsWorkflowProfile":null,"ApiProfile":null}'
    );
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'AgentAnalysisContext')
BEGIN
    INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
    VALUES
    (
        N'AgentAnalysisContext',
        N'{"InventoryCorrelationLayers":["Domain","Role"]}'
    );
END;
GO

UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer1', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer1') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');
UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer1', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer1') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');
UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer2', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer2') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');
UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer2', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer2') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');
UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer3', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer3') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');
UPDATE [dbo].[Settings]
SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer3', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
    AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer3') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');
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

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_AlertRules_RuleType'
      AND [object_id] = OBJECT_ID(N'[dbo].[AlertRules]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_AlertRules_RuleType]
        ON [dbo].[AlertRules] ([RuleType])
        WHERE [RuleType] = N'InventoryHeartbeatFreshness';
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[AlertRules]
    WHERE [RuleType] = N'InventoryHeartbeatFreshness'
)
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains],
         [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName],
         [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
    VALUES
        ('d7ab8e49-2cfa-4a2e-a635-964bee230001', N'Inventory heartbeat freshness', 1, 0,
         N'InventoryHeartbeatFreshness', N'', N'', N'', 5, N'', N'',
         0, 0, N'info', N'', 0, N'None');
END;
GO

/*
    Seed and synchronize the built-in categorization rules. Re-running this
    script restores their required values while leaving additional rules intact.
*/
UPDATE [dbo].[AlertRules]
SET [Name] = N'Port failures indicate system outage',
    [Enabled] = 1,
    [Priority] = 30,
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
    [Tone] = N'failure',
    [IsCritical] = 1,
    [AnalysisClassification] = N'SystemOutage'
WHERE [Id] = '47a96c56-ccf5-4f4e-97ce-6a72bb462f91';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [IsCritical], [AnalysisClassification])
    VALUES
        ('47a96c56-ccf5-4f4e-97ce-6a72bb462f91', N'Port failures indicate system outage', 1, 30, N'Port', N'DCPort', N'RowCountGreaterThan', 10, N'', N'System Outage', 1, 0, N'failure', 1, N'SystemOutage');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'Domain controller majority indicates domain service outage',
    [Enabled] = 1,
    [Priority] = 10,
    [RuleType] = N'Categorization',
    [InventoryRole] = N'',
    [AlertNameContains] = N'',
    [QueryResultType] = N'',
    [ConditionType] = N'DomainControllerPercentageInDomain',
    [Threshold] = 50,
    [FailedItemName] = N'',
    [CategoryName] = N'Domain Service Outage',
    [ApplyToTarget] = 0,
    [Collapsed] = 0,
    [Tone] = N'failure',
    [IsCritical] = 1,
    [AnalysisClassification] = N'SystemOutage'
WHERE [Id] = 'c4c346f4-2f67-465e-94b0-c018a6de0001';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
    VALUES
        ('c4c346f4-2f67-465e-94b0-c018a6de0001', N'Domain controller majority indicates domain service outage', 1, 10, N'Categorization', N'', N'', N'DomainControllerPercentageInDomain', 50, N'', N'Domain Service Outage', 0, 0, N'failure', N'', 1, N'SystemOutage');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'All site domain controllers indicate site outage',
    [Enabled] = 1,
    [Priority] = 20,
    [RuleType] = N'Categorization',
    [InventoryRole] = N'',
    [AlertNameContains] = N'',
    [QueryResultType] = N'',
    [ConditionType] = N'AllDomainControllersInSite',
    [Threshold] = 0,
    [FailedItemName] = N'',
    [CategoryName] = N'Domain Service Site Outage',
    [ApplyToTarget] = 0,
    [Collapsed] = 0,
    [Tone] = N'failure',
    [IsCritical] = 1,
    [AnalysisClassification] = N'SystemOutage'
WHERE [Id] = 'c4c346f4-2f67-465e-94b0-c018a6de0002';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
    VALUES
        ('c4c346f4-2f67-465e-94b0-c018a6de0002', N'All site domain controllers indicate site outage', 1, 20, N'Categorization', N'', N'', N'AllDomainControllersInSite', 0, N'', N'Domain Service Site Outage', 0, 0, N'failure', N'', 1, N'SystemOutage');
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
    [ConditionType] = N'OnlyFailedItems',
    [Threshold] = 0,
    [FailedItemName] = N'DFSREvent',
    [CategoryName] = N'Suppressed alerts',
    [ApplyToTarget] = 0,
    [Collapsed] = 1,
    [Tone] = N'info',
    [IsCritical] = 0,
    [AnalysisClassification] = N'Suppressed'
WHERE [Id] = 'd82b566a-ce6e-4201-b7b9-9a366426e7b8';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [AnalysisClassification])
    VALUES
        ('d82b566a-ce6e-4201-b7b9-9a366426e7b8', N'Suppress isolated DFSREvent failure', 1, 20, N'DCDiag', N'DCDiag', N'OnlyFailedItems', 0, N'DFSREvent', N'Suppressed alerts', 0, 1, N'info', N'Suppressed');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'Suppress healthy DCDiag results',
    [Enabled] = 1,
    [Priority] = 30,
    [RuleType] = N'Categorization',
    [InventoryRole] = N'',
    [AlertNameContains] = N'DCDiag',
    [QueryResultType] = N'DCDiag',
    [ConditionType] = N'NoFailedItems',
    [Threshold] = 0,
    [FailedItemName] = N'',
    [CategoryName] = N'Suppressed alerts',
    [ApplyToTarget] = 0,
    [Collapsed] = 1,
    [Tone] = N'info',
    [IsCritical] = 0,
    [AnalysisClassification] = N'Suppressed'
WHERE [Id] = 'd82b566a-ce6e-4201-b7b9-9a366426e7b9';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
    VALUES
        ('d82b566a-ce6e-4201-b7b9-9a366426e7b9', N'Suppress healthy DCDiag results', 1, 30, N'Categorization', N'DCDiag', N'DCDiag', N'NoFailedItems', 0, N'', N'Suppressed alerts', 0, 1, N'info', N'', 0, N'Suppressed');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[AlertRules]
    WHERE [Id] = 'd82b566a-ce6e-4201-b7b9-9a366426e7ba'
)
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains],
         [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName],
         [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
    VALUES
        ('d82b566a-ce6e-4201-b7b9-9a366426e7ba', N'Suppress healthy DCPort results', 1, 5,
         N'Categorization', N'', N'DCPort', N'RowCountEquals', 0, N'', N'Suppressed alerts',
         0, 1, N'info', N'', 0, N'Suppressed');
END;
GO

UPDATE [dbo].[AlertRules]
SET [Name] = N'Inventory Heartbeat Status',
    [Enabled] = 1,
    [Priority] = 1,
    [RuleType] = N'InventoryStatus',
    [AlertNameContains] = N'Inventory Heartbeat Status',
    [QueryResultType] = N'InventoryHeartbeat',
    [ConditionType] = N'AnyMatchingAlert',
    [Threshold] = 0,
    [FailedItemName] = N'',
    [CategoryName] = N'',
    [ApplyToTarget] = 0,
    [Collapsed] = 0,
    [Tone] = N'info',
    [InventoryRole] = N'',
    [IsCritical] = 0
WHERE [Id] = '7b28f935-087b-4c20-8635-84e2c65484e9';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical])
    VALUES
        ('7b28f935-087b-4c20-8635-84e2c65484e9', N'Inventory Heartbeat Status', 1, 1, N'InventoryStatus', N'Inventory Heartbeat Status', N'InventoryHeartbeat', N'AnyMatchingAlert', 0, N'', N'', 0, 0, N'info', N'', 0);
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
    [InventoryRole] = N'Domain Controller',
    [IsCritical] = 0
WHERE [Id] = '956fbb9c-cadb-4f49-ad1e-78c09a8a1301';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole])
    VALUES
        ('956fbb9c-cadb-4f49-ad1e-78c09a8a1301', N'DCDiag targets are domain controllers', 1, 100, N'InventoryRoleAssignment', N'', N'DCDiag', N'', 0, N'', N'', 0, 0, N'info', N'Domain Controller');
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
    [InventoryRole] = N'Domain Controller',
    [IsCritical] = 0
WHERE [Id] = '956fbb9c-cadb-4f49-ad1e-78c09a8a1302';

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [dbo].[AlertRules]
        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains], [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName], [ApplyToTarget], [Collapsed], [Tone], [InventoryRole])
    VALUES
        ('956fbb9c-cadb-4f49-ad1e-78c09a8a1302', N'Replication targets are domain controllers', 1, 110, N'InventoryRoleAssignment', N'', N'Replication', N'', 0, N'', N'', 0, 0, N'info', N'Domain Controller');
END;
GO

/* Backfill critical System Outage events and lifecycle resolution timestamps. */
UPDATE fired
SET fired.[IsCritical] = 1
FROM [dbo].[ParsedAlerts] AS fired
WHERE fired.[MonitorCondition] = N'Fired'
    AND fired.[AlertName] LIKE N'%Port%'
    AND JSON_VALUE(fired.[QueryResults], '$.type') = N'DCPort'
    AND (SELECT COUNT(*) FROM OPENJSON(fired.[QueryResults], '$.rows')) > 10;

;WITH Resolutions AS
(
        SELECT parsed.[AlertId], MAX(alerts.[ReceivedAt]) AS [ResolvedAt]
        FROM [dbo].[ParsedAlerts] AS parsed
        INNER JOIN [dbo].[Alerts] AS alerts ON alerts.[Id] = parsed.[Id]
        WHERE parsed.[MonitorCondition] = N'Resolved'
            AND parsed.[AlertId] <> N''
        GROUP BY parsed.[AlertId]
)
UPDATE lifecycle
SET lifecycle.[ResolvedAt] = resolutions.[ResolvedAt],
        lifecycle.[IsCritical] = CASE
                WHEN EXISTS
                (
                        SELECT 1
                        FROM [dbo].[ParsedAlerts] AS critical
                        WHERE critical.[AlertId] = lifecycle.[AlertId]
                            AND critical.[IsCritical] = 1
                ) THEN 1
                ELSE lifecycle.[IsCritical]
        END
FROM [dbo].[ParsedAlerts] AS lifecycle
INNER JOIN Resolutions AS resolutions ON resolutions.[AlertId] = lifecycle.[AlertId];
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


IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831160000_AddInventoryGraphLayerChoices'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831160000_AddInventoryGraphLayerChoices', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831170000_AddCriticalAlertRules'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831170000_AddCriticalAlertRules', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831180000_AddParsedAlertCriticalLifecycle'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831180000_AddParsedAlertCriticalLifecycle', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831190000_AddHealthyDCDiagSuppressionRule'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831190000_AddHealthyDCDiagSuppressionRule', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831200000_AllowMultipleFailedItems'
)
BEGIN
    UPDATE [dbo].[AlertRules]
    SET [ConditionType] = N'OnlyFailedItems'
    WHERE [ConditionType] = N'OnlyFailedItem';

    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831200000_AllowMultipleFailedItems', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831210000_AddLogbook'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831210000_AddLogbook', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831220000_ExpandLogbookComments'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831220000_ExpandLogbookComments', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260902090000_AddDynamicInboxLayouts'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260902090000_AddDynamicInboxLayouts', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914101229_AddInventoryHeartbeatStatus'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260914101229_AddInventoryHeartbeatStatus', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916050733_AddAlertRuleAnalysisClassification'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916050733_AddAlertRuleAnalysisClassification', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916140000_AddAgentAnalysisExecutionSetting'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916140000_AddAgentAnalysisExecutionSetting', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916150000_AddAgentAnalysisJobs'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916150000_AddAgentAnalysisJobs', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916160000_PersistAgentAnalysisResults'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916160000_PersistAgentAnalysisResults', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916170000_AddAgentAnalysisJobIdempotency'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916170000_AddAgentAnalysisJobIdempotency', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916180000_AddAgentAnalysisReviewEvents'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916180000_AddAgentAnalysisReviewEvents', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916190000_AddAgentAnalysisContextSetting'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916190000_AddAgentAnalysisContextSetting', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002100000_AddDomainServiceOutageRules'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261002100000_AddDomainServiceOutageRules', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002104500_MarkDomainServiceOutagesCritical'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261002104500_MarkDomainServiceOutagesCritical', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006095251_AddDCPortQueryRefresh'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006095251_AddDCPortQueryRefresh', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009083929_AddInventoryHeartbeatFreshnessRule'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009083929_AddInventoryHeartbeatFreshnessRule', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009085717_AddInventoryMaintenance'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009085717_AddInventoryMaintenance', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009102400_AddHealthyDCPortSuppressionRule'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009102400_AddHealthyDCPortSuppressionRule', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009114000_AddInboxTargetOutputDisplaySetting'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009114000_AddInboxTargetOutputDisplaySetting', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012123712_AddInventoryHeartbeatDetails'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261012123712_AddInventoryHeartbeatDetails', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013102000_AddAlertGraphSiteLocations'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261013102000_AddAlertGraphSiteLocations', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015053058_AddAlertFiredHistory'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261015053058_AddAlertFiredHistory', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015055257_AddLogbookAlertReference'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261015055257_AddLogbookAlertReference', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015152546_AddAnalysisQueryRegistry'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261015152546_AddAnalysisQueryRegistry', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015173737_AddAnalysisDashboards'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261015173737_AddAnalysisDashboards', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021140000_AddAnalysisVisualizationOptions'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021140000_AddAnalysisVisualizationOptions', N'10.0.11');
END;
GO

UPDATE [dbo].[ComputerInventory]
SET [Role] = N'Domain Controller'
WHERE LOWER(LTRIM(RTRIM([Role]))) = N'domaincontrollers';

UPDATE [dbo].[AlertRules]
SET [InventoryRole] = N'Domain Controller'
WHERE LOWER(LTRIM(RTRIM([InventoryRole]))) = N'domaincontrollers';
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261019142000_NormalizeDomainControllerRole'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261019142000_NormalizeDomainControllerRole', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261020170000_AllowSharedDashboardNames'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261020170000_AllowSharedDashboardNames', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021143000_AddAnalysisQueryScope'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021143000_AddAnalysisQueryScope', N'10.0.11');
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021173000_AddAnalysisQueryCategories'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021173000_AddAnalysisQueryCategories', N'10.0.11');
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
    OBJECT_ID(N'[dbo].[AlertHistory]', N'U') AS [AlertHistoryTableObjectId],
    OBJECT_ID(N'[dbo].[AlertRules]', N'U') AS [AlertRulesTableObjectId],
    OBJECT_ID(N'[dbo].[ComputerInventory]', N'U') AS [ComputerInventoryTableObjectId],
    OBJECT_ID(N'[dbo].[AuthenticationUsers]', N'U') AS [AuthenticationUsersTableObjectId],
    OBJECT_ID(N'[dbo].[Settings]', N'U') AS [SettingsTableObjectId],
    OBJECT_ID(N'[dbo].[ParsedAlerts]', N'U') AS [ParsedAlertsTableObjectId],
    OBJECT_ID(N'[dbo].[InboxLayouts]', N'U') AS [InboxLayoutsTableObjectId],
    OBJECT_ID(N'[dbo].[AnalysisQueryDefinitions]', N'U') AS [AnalysisQueryDefinitionsTableObjectId],
    OBJECT_ID(N'[dbo].[AnalysisQueryVersions]', N'U') AS [AnalysisQueryVersionsTableObjectId],
    OBJECT_ID(N'[dbo].[AnalysisQueryWorkspaces]', N'U') AS [AnalysisQueryWorkspacesTableObjectId],
    OBJECT_ID(N'[dbo].[AnalysisDashboards]', N'U') AS [AnalysisDashboardsTableObjectId],
    OBJECT_ID(N'[dbo].[AnalysisDashboardTemplates]', N'U') AS [AnalysisDashboardTemplatesTableObjectId],
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
    [InventoryRole],
    [IsCritical],
    [AnalysisClassification]
FROM [dbo].[AlertRules]
ORDER BY [Priority], [Name];
GO