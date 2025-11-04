-- 01-init.sql
-- Creates the KafkaVotes database and the raw votes table.
-- NOTE: The official SQL Server Linux container does NOT auto-run scripts in /docker-entrypoint-initdb.d
-- out-of-the-box like Postgres/MySQL. You can execute this manually once the container is healthy:
--   docker exec -it mssql /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "Your_strong_password123!" -d master -i /docker-entrypoint-initdb.d/01-init.sql
-- For local dev convenience, we still mount the directory.

IF DB_ID('KafkaVotes') IS NULL
BEGIN
    PRINT 'Creating database KafkaVotes';
    CREATE DATABASE KafkaVotes;
END
GO

USE KafkaVotes;
GO

IF OBJECT_ID('dbo.VotesRaw', 'U') IS NULL
BEGIN
    PRINT 'Creating table dbo.VotesRaw';
    SET ANSI_NULLS ON;
    SET QUOTED_IDENTIFIER ON;
    CREATE TABLE dbo.VotesRaw
    (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NOT NULL,
        [Option] NVARCHAR(100) NOT NULL,
        TimestampUtc DATETIME2(3) NOT NULL,
        CityTopic NVARCHAR(200) NULL,
        City NVARCHAR(200) NULL,
        ZipCode INT NULL,
        IngestedUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID('dbo.VotesRaw', 'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VotesRaw_Option' AND object_id = OBJECT_ID('dbo.VotesRaw'))
    BEGIN
        PRINT 'Creating index IX_VotesRaw_Option';
        CREATE INDEX IX_VotesRaw_Option ON dbo.VotesRaw([Option]);
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VotesRaw_CityTopic_Option' AND object_id = OBJECT_ID('dbo.VotesRaw'))
    BEGIN
        PRINT 'Creating index IX_VotesRaw_CityTopic_Option';
        CREATE INDEX IX_VotesRaw_CityTopic_Option ON dbo.VotesRaw(CityTopic, [Option]) WHERE CityTopic IS NOT NULL;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VotesRaw_TimestampUtc' AND object_id = OBJECT_ID('dbo.VotesRaw'))
    BEGIN
        PRINT 'Creating index IX_VotesRaw_TimestampUtc';
        CREATE INDEX IX_VotesRaw_TimestampUtc ON dbo.VotesRaw(TimestampUtc);
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VotesRaw_UserId' AND object_id = OBJECT_ID('dbo.VotesRaw'))
    BEGIN
        PRINT 'Creating index IX_VotesRaw_UserId';
        CREATE INDEX IX_VotesRaw_UserId ON dbo.VotesRaw(UserId);
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VotesRaw_ZipCode' AND object_id = OBJECT_ID('dbo.VotesRaw'))
    BEGIN
        PRINT 'Creating index IX_VotesRaw_ZipCode';
        CREATE INDEX IX_VotesRaw_ZipCode ON dbo.VotesRaw(ZipCode);
    END
END
GO
