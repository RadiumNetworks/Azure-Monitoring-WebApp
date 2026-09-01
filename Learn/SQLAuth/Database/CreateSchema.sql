-- SQLAuthLearning schema for SQL Server / LocalDB.
-- Run with sqlcmd or SQL Server Management Studio against the configured LocalDB instance.

IF DB_ID(N'SQLAuthLearning') IS NULL
BEGIN
    CREATE DATABASE [SQLAuthLearning];
END;
GO

USE [SQLAuthLearning];
GO

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;

IF NOT EXISTS (
    SELECT 1
    FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901170854_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Username] nvarchar(128) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Username])
    );

    CREATE TABLE [Notes] (
        [Id] int NOT NULL IDENTITY(1, 1),
        [Text] nvarchar(500) NOT NULL,
        [OwnerUsername] nvarchar(128) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Notes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Notes_Users_OwnerUsername]
            FOREIGN KEY ([OwnerUsername]) REFERENCES [Users] ([Username]) ON DELETE CASCADE
    );

    CREATE INDEX [IX_Notes_OwnerUsername]
        ON [Notes] ([OwnerUsername]);

    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260901170854_InitialCreate', N'9.0.19');
END;

COMMIT;
GO
