-- UIDynamic SQLite schema
-- SQLite creates the database file when this script is executed against a new connection.
-- All commands are idempotent so this file can also upgrade an existing demo database.

PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS "SavedLayouts" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SavedLayouts" PRIMARY KEY AUTOINCREMENT,
    "OwnerKey" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "DocumentJson" TEXT NOT NULL,
    "DocumentVersion" INTEGER NOT NULL,
    "Revision" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SavedLayouts_OwnerKey"
    ON "SavedLayouts" ("OwnerKey");

CREATE TABLE IF NOT EXISTS "MetricReadings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_MetricReadings" PRIMARY KEY AUTOINCREMENT,
    "DataSourceKey" TEXT NOT NULL,
    "Label" TEXT NOT NULL,
    "Value" REAL NOT NULL,
    "Unit" TEXT NOT NULL,
    "Change" REAL NOT NULL,
    "RecordedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_MetricReadings_DataSourceKey"
    ON "MetricReadings" ("DataSourceKey");

CREATE TABLE IF NOT EXISTS "TrendSamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TrendSamples" PRIMARY KEY AUTOINCREMENT,
    "DataSourceKey" TEXT NOT NULL,
    "SeriesLabel" TEXT NOT NULL,
    "Value" REAL NOT NULL,
    "RecordedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TrendSamples_DataSourceKey_RecordedAt"
    ON "TrendSamples" ("DataSourceKey", "RecordedAt");

CREATE TABLE IF NOT EXISTS "OperationalAlerts" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_OperationalAlerts" PRIMARY KEY AUTOINCREMENT,
    "DataSourceKey" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Location" TEXT NOT NULL,
    "Priority" TEXT NOT NULL,
    "RaisedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_OperationalAlerts_DataSourceKey"
    ON "OperationalAlerts" ("DataSourceKey");

CREATE TABLE IF NOT EXISTS "TeamNotes" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TeamNotes" PRIMARY KEY AUTOINCREMENT,
    "DataSourceKey" TEXT NOT NULL,
    "Content" TEXT NOT NULL,
    "Author" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeamNotes_DataSourceKey"
    ON "TeamNotes" ("DataSourceKey");

CREATE TABLE IF NOT EXISTS "ServiceHealthEntries" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ServiceHealthEntries" PRIMARY KEY AUTOINCREMENT,
    "DataSourceKey" TEXT NOT NULL,
    "ComponentName" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "DisplayOrder" INTEGER NOT NULL,
    "CheckedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_ServiceHealthEntries_DataSourceKey_ComponentName"
    ON "ServiceHealthEntries" ("DataSourceKey", "ComponentName");
