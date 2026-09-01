-- EFCoreLearning schema for SQLite.
-- Example: sqlite3 efcore-learning.db ".read Database/CreateSchema.sql"
-- This script records the initial migration so Database.MigrateAsync() remains compatible.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

BEGIN TRANSACTION;

CREATE TABLE "Courses" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Courses" PRIMARY KEY AUTOINCREMENT,
    "Title" TEXT NOT NULL,
    "Credits" INTEGER NOT NULL
);

CREATE TABLE "Customers" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Customers" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Email" TEXT NOT NULL
);

CREATE TABLE "InventoryItems" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_InventoryItems" PRIMARY KEY AUTOINCREMENT,
    "Sku" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Price" TEXT NOT NULL,
    "Stock" INTEGER NOT NULL,
    "IsActive" INTEGER NOT NULL
);

CREATE TABLE "Students" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Students" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL
);

CREATE TABLE "Orders" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Orders" PRIMARY KEY AUTOINCREMENT,
    "Reference" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Total" TEXT NOT NULL,
    "IsPaid" INTEGER NOT NULL,
    "CustomerId" INTEGER NOT NULL,
    CONSTRAINT "FK_Orders_Customers_CustomerId"
        FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Enrollments" (
    "StudentId" INTEGER NOT NULL,
    "CourseId" INTEGER NOT NULL,
    "EnrolledAt" TEXT NOT NULL,
    "Grade" TEXT NULL,
    CONSTRAINT "PK_Enrollments" PRIMARY KEY ("StudentId", "CourseId"),
    CONSTRAINT "FK_Enrollments_Courses_CourseId"
        FOREIGN KEY ("CourseId") REFERENCES "Courses" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_Enrollments_Students_StudentId"
        FOREIGN KEY ("StudentId") REFERENCES "Students" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_Enrollments_CourseId" ON "Enrollments" ("CourseId");
CREATE UNIQUE INDEX "IX_InventoryItems_Sku" ON "InventoryItems" ("Sku");
CREATE INDEX "IX_Orders_CustomerId" ON "Orders" ("CustomerId");
CREATE UNIQUE INDEX "IX_Orders_Reference" ON "Orders" ("Reference");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260901180300_InitialCreate', '9.0.19');

COMMIT;
