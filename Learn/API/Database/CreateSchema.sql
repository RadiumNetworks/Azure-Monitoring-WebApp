-- APILearning schema for SQLite.
-- Example: sqlite3 api-learning.db ".read Database/CreateSchema.sql"

PRAGMA foreign_keys = ON;

BEGIN TRANSACTION;

CREATE TABLE "Payloads" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Payloads" PRIMARY KEY,
    "ReceivedAt" TEXT NOT NULL,
    "OccurredAt" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "Severity" INTEGER NOT NULL,
    "Summary" TEXT NOT NULL,
    "RawJson" TEXT NOT NULL
);

CREATE INDEX "IX_Payloads_Category" ON "Payloads" ("Category");
CREATE INDEX "IX_Payloads_ReceivedAt" ON "Payloads" ("ReceivedAt");
CREATE INDEX "IX_Payloads_Severity" ON "Payloads" ("Severity");
CREATE INDEX "IX_Payloads_Source" ON "Payloads" ("Source");

COMMIT;
