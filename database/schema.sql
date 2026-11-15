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
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [ConfigurationHistory] (
        [Id] bigint NOT NULL IDENTITY,
        [EntityType] nvarchar(50) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [ChangeType] nvarchar(20) NOT NULL,
        [ChangedAtUtc] datetime2 NOT NULL,
        [ChangedBy] nvarchar(100) NOT NULL,
        [Changes] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_ConfigurationHistory] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [Destinations] (
        [Id] uniqueidentifier NOT NULL,
        [Key] nvarchar(100) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [BaseUrl] nvarchar(500) NOT NULL,
        [IsActive] bit NOT NULL,
        [TimeoutSeconds] int NOT NULL,
        [MaxResponseBytes] bigint NOT NULL,
        [AllowedPorts] nvarchar(100) NULL,
        [AuthProfileReference] nvarchar(200) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Destinations] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [EncryptionProfiles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Algorithm] nvarchar(30) NOT NULL,
        [KeyReference] nvarchar(200) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_EncryptionProfiles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [RequestLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [CorrelationId] nvarchar(64) NOT NULL,
        [Caller] nvarchar(100) NULL,
        [OperationKey] nvarchar(100) NULL,
        [DestinationId] uniqueidentifier NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [CompletedAtUtc] datetime2 NOT NULL,
        [DurationMs] bigint NOT NULL,
        [Forwarded] bit NOT NULL,
        [UpstreamStatusCode] int NULL,
        [ProxyStatusCode] int NOT NULL,
        [Outcome] nvarchar(30) NOT NULL,
        [Reason] nvarchar(100) NULL,
        CONSTRAINT [PK_RequestLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [Operations] (
        [Id] uniqueidentifier NOT NULL,
        [OperationKey] nvarchar(100) NOT NULL,
        [DestinationId] uniqueidentifier NOT NULL,
        [HttpMethod] nvarchar(10) NOT NULL,
        [PathTemplate] nvarchar(500) NOT NULL,
        [IsActive] bit NOT NULL,
        [MaxRequestBytes] int NOT NULL,
        [RequiresEncryption] bit NOT NULL,
        [EncryptionProfileId] uniqueidentifier NULL,
        [AllowUnknownParameters] bit NOT NULL,
        [TimeoutSeconds] int NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Operations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Operations_Destinations_DestinationId] FOREIGN KEY ([DestinationId]) REFERENCES [Destinations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Operations_EncryptionProfiles_EncryptionProfileId] FOREIGN KEY ([EncryptionProfileId]) REFERENCES [EncryptionProfiles] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [HeaderRules] (
        [Id] uniqueidentifier NOT NULL,
        [OperationId] uniqueidentifier NOT NULL,
        [HeaderName] nvarchar(100) NOT NULL,
        CONSTRAINT [PK_HeaderRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_HeaderRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE TABLE [ParameterRules] (
        [Id] uniqueidentifier NOT NULL,
        [OperationId] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [IsRequired] bit NOT NULL,
        [DataType] nvarchar(20) NOT NULL,
        [Location] nvarchar(20) NOT NULL,
        [MaxLength] int NULL,
        [MinValue] decimal(28,6) NULL,
        [MaxValue] decimal(28,6) NULL,
        [Pattern] nvarchar(500) NULL,
        CONSTRAINT [PK_ParameterRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ParameterRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ConfigurationHistory_EntityType_EntityId] ON [ConfigurationHistory] ([EntityType], [EntityId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Destinations_Key] ON [Destinations] ([Key]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EncryptionProfiles_Name] ON [EncryptionProfiles] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_HeaderRules_OperationId_HeaderName] ON [HeaderRules] ([OperationId], [HeaderName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Operations_DestinationId] ON [Operations] ([DestinationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Operations_EncryptionProfileId] ON [Operations] ([EncryptionProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Operations_OperationKey] ON [Operations] ([OperationKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ParameterRules_OperationId_Name] ON [ParameterRules] ([OperationId], [Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RequestLogs_CorrelationId] ON [RequestLogs] ([CorrelationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RequestLogs_StartedAtUtc] ON [RequestLogs] ([StartedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115103514_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115103514_InitialCreate', N'8.0.11');
END;
GO

COMMIT;
GO

