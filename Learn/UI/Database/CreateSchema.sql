-- UILearning schema for SQLite.
-- Example: sqlite3 ui-learning.db ".read Database/CreateSchema.sql"

PRAGMA foreign_keys = ON;

BEGIN TRANSACTION;

CREATE TABLE "Departments" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Departments" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL
);

CREATE TABLE "TimelineEvents" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TimelineEvents" PRIMARY KEY AUTOINCREMENT,
    "OccurredAt" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "Severity" INTEGER NOT NULL
);

CREATE TABLE "UserProfiles" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserProfiles" PRIMARY KEY AUTOINCREMENT,
    "DisplayName" TEXT NOT NULL,
    "Email" TEXT NOT NULL,
    "Department" TEXT NOT NULL,
    "TimeZone" TEXT NOT NULL,
    "ReceiveReports" INTEGER NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE "Teams" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Teams" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "DepartmentId" INTEGER NOT NULL,
    CONSTRAINT "FK_Teams_Departments_DepartmentId"
        FOREIGN KEY ("DepartmentId") REFERENCES "Departments" ("Id") ON DELETE CASCADE
);

CREATE TABLE "People" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_People" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "JobTitle" TEXT NOT NULL,
    "TeamId" INTEGER NOT NULL,
    CONSTRAINT "FK_People_Teams_TeamId"
        FOREIGN KEY ("TeamId") REFERENCES "Teams" ("Id") ON DELETE CASCADE
);

CREATE TABLE "PerformanceMetrics" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_PerformanceMetrics" PRIMARY KEY AUTOINCREMENT,
    "TeamId" INTEGER NOT NULL,
    "RecordedAt" TEXT NOT NULL,
    "Region" TEXT NOT NULL,
    "HealthScore" INTEGER NOT NULL,
    "ResponseTimeMs" REAL NOT NULL,
    "Throughput" REAL NOT NULL,
    CONSTRAINT "FK_PerformanceMetrics_Teams_TeamId"
        FOREIGN KEY ("TeamId") REFERENCES "Teams" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_People_TeamId" ON "People" ("TeamId");
CREATE INDEX "IX_PerformanceMetrics_TeamId" ON "PerformanceMetrics" ("TeamId");
CREATE INDEX "IX_Teams_DepartmentId" ON "Teams" ("DepartmentId");

COMMIT;
