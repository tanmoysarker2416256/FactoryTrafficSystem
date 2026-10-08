-- Schema for the Factory Traffic Management System (SQL Server).
-- The app also creates this automatically on first start (EnsureCreated), so running this script is OPTIONAL.
-- It is provided as the "database schema" deliverable and for people who prefer to create the DB by hand.

IF DB_ID('FactoryTraffic') IS NULL CREATE DATABASE FactoryTraffic;
GO
USE FactoryTraffic;
GO

CREATE TABLE dbo.Junctions
(
    Id          nvarchar(32)   NOT NULL CONSTRAINT PK_Junctions PRIMARY KEY,
    Name        nvarchar(100)  NOT NULL,
    ConfigJson  nvarchar(max)  NOT NULL,   -- JunctionConfig: phases, timings, weights
    StateJson   nvarchar(max)  NOT NULL,   -- JunctionState snapshot: queues, desired/actual signals, mode, emergency, manual, pending command, processed event ids
    Version     bigint         NOT NULL,
    UpdatedAt   datetimeoffset NOT NULL,
    RowVersion  rowversion     NOT NULL    -- optimistic concurrency token
);

CREATE TABLE dbo.AuditLog
(
    Id            bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    JunctionId    nvarchar(32)   NOT NULL,
    EventType     nvarchar(50)   NOT NULL,
    Direction     nvarchar(10)   NULL,
    PreviousState nvarchar(50)   NULL,
    NewState      nvarchar(50)   NULL,
    CommandId     nvarchar(50)   NULL,
    Message       nvarchar(500)  NOT NULL,
    Timestamp     datetimeoffset NOT NULL,
    CONSTRAINT FK_AuditLog_Junctions FOREIGN KEY (JunctionId) REFERENCES dbo.Junctions(Id)
);

CREATE INDEX IX_AuditLog_JunctionId_Id ON dbo.AuditLog (JunctionId, Id);
GO
