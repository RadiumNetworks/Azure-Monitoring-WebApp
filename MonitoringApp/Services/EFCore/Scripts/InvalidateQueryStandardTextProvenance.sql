SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Stop all writers first. Run before re-enabling AP7 after writes by an older app.
IF COL_LENGTH(N'dbo.AnalysisQueryDefinitions', N'StandardTextProvenanceJson') IS NULL
    OR COL_LENGTH(N'dbo.AnalysisQueryVersions', N'StandardTextProvenanceJson') IS NULL
    OR COL_LENGTH(N'dbo.ScheduledQueryRules', N'StandardTextProvenanceJson') IS NULL
    THROW 50000, 'The additive query provenance schema must exist before invalidation.', 1;

BEGIN TRANSACTION;

UPDATE [dbo].[AnalysisQueryDefinitions]
SET [StandardTextProvenanceJson] = NULL
WHERE [StandardTextProvenanceJson] IS NOT NULL;
DECLARE @Definitions int = @@ROWCOUNT;

UPDATE [dbo].[AnalysisQueryVersions]
SET [StandardTextProvenanceJson] = NULL
WHERE [StandardTextProvenanceJson] IS NOT NULL;
DECLARE @Versions int = @@ROWCOUNT;

UPDATE [dbo].[ScheduledQueryRules]
SET [StandardTextProvenanceJson] = NULL
WHERE [StandardTextProvenanceJson] IS NOT NULL;
DECLARE @Rules int = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT @Definitions AS [InvalidatedDefinitions],
       @Versions AS [InvalidatedVersions],
       @Rules AS [InvalidatedRules];
