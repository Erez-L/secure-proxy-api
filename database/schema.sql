-- Secure Proxy - SQL Server schema.
-- Matches the EF Core model in src/SecureProxy.Api/Data. Use this script to create the database
-- by hand, or let the application create it (see README.md, "Database").

CREATE TABLE [Destinations] (
    [Id]                   uniqueidentifier NOT NULL CONSTRAINT [PK_Destinations] PRIMARY KEY,
    [Key]                  nvarchar(100)    NOT NULL,
    [Name]                 nvarchar(200)    NOT NULL,
    [BaseUrl]              nvarchar(500)    NOT NULL,
    [IsActive]             bit              NOT NULL,
    [TimeoutSeconds]       int              NOT NULL,
    [MaxResponseBytes]     bigint           NOT NULL,
    [AllowedPorts]         nvarchar(100)    NULL,
    [AuthProfileReference] nvarchar(200)    NULL,
    [CreatedAtUtc]         datetime2        NOT NULL,
    [UpdatedAtUtc]         datetime2        NOT NULL,
    [RowVersion]           rowversion       NOT NULL
);
CREATE UNIQUE INDEX [IX_Destinations_Key] ON [Destinations] ([Key]);

CREATE TABLE [EncryptionProfiles] (
    [Id]           uniqueidentifier NOT NULL CONSTRAINT [PK_EncryptionProfiles] PRIMARY KEY,
    [Name]         nvarchar(100)    NOT NULL,
    [Algorithm]    nvarchar(30)     NOT NULL,
    [KeyReference] nvarchar(200)    NOT NULL,   -- a reference into the secret store, never key material
    [IsActive]     bit              NOT NULL,
    [CreatedAtUtc] datetime2        NOT NULL,
    [UpdatedAtUtc] datetime2        NOT NULL
);
CREATE UNIQUE INDEX [IX_EncryptionProfiles_Name] ON [EncryptionProfiles] ([Name]);

CREATE TABLE [Operations] (
    [Id]                     uniqueidentifier NOT NULL CONSTRAINT [PK_Operations] PRIMARY KEY,
    [OperationKey]           nvarchar(100)    NOT NULL,
    [DestinationId]          uniqueidentifier NOT NULL,
    [HttpMethod]             nvarchar(10)     NOT NULL,
    [PathTemplate]           nvarchar(500)    NOT NULL,
    [IsActive]               bit              NOT NULL,
    [MaxRequestBytes]        int              NOT NULL,
    [RequiresEncryption]     bit              NOT NULL,
    [EncryptionProfileId]    uniqueidentifier NULL,
    [AllowUnknownParameters] bit              NOT NULL,
    [TimeoutSeconds]         int              NULL,
    [CreatedAtUtc]           datetime2        NOT NULL,
    [UpdatedAtUtc]           datetime2        NOT NULL,
    [RowVersion]             rowversion       NOT NULL,
    CONSTRAINT [FK_Operations_Destinations_DestinationId] FOREIGN KEY ([DestinationId]) REFERENCES [Destinations] ([Id]),
    CONSTRAINT [FK_Operations_EncryptionProfiles_EncryptionProfileId] FOREIGN KEY ([EncryptionProfileId]) REFERENCES [EncryptionProfiles] ([Id])
);
CREATE UNIQUE INDEX [IX_Operations_OperationKey] ON [Operations] ([OperationKey]);
CREATE INDEX [IX_Operations_DestinationId] ON [Operations] ([DestinationId]);
CREATE INDEX [IX_Operations_EncryptionProfileId] ON [Operations] ([EncryptionProfileId]);

CREATE TABLE [ParameterRules] (
    [Id]          uniqueidentifier NOT NULL CONSTRAINT [PK_ParameterRules] PRIMARY KEY,
    [OperationId] uniqueidentifier NOT NULL,
    [Name]        nvarchar(100)    NOT NULL,
    [IsRequired]  bit              NOT NULL,
    [DataType]    nvarchar(20)     NOT NULL,   -- String | Integer | Decimal | Boolean | Guid
    [Location]    nvarchar(20)     NOT NULL,   -- Path | Query | Body
    [MaxLength]   int              NULL,
    [MinValue]    decimal(28,6)    NULL,
    [MaxValue]    decimal(28,6)    NULL,
    [Pattern]     nvarchar(500)    NULL,
    CONSTRAINT [FK_ParameterRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
);
CREATE UNIQUE INDEX [IX_ParameterRules_OperationId_Name] ON [ParameterRules] ([OperationId], [Name]);

CREATE TABLE [HeaderRules] (
    [Id]          uniqueidentifier NOT NULL CONSTRAINT [PK_HeaderRules] PRIMARY KEY,
    [OperationId] uniqueidentifier NOT NULL,
    [HeaderName]  nvarchar(100)    NOT NULL,
    CONSTRAINT [FK_HeaderRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
);
CREATE UNIQUE INDEX [IX_HeaderRules_OperationId_HeaderName] ON [HeaderRules] ([OperationId], [HeaderName]);

CREATE TABLE [RequestLogs] (
    [Id]                 bigint IDENTITY  NOT NULL CONSTRAINT [PK_RequestLogs] PRIMARY KEY,
    [CorrelationId]      nvarchar(64)     NOT NULL,
    [Caller]             nvarchar(100)    NULL,
    [OperationKey]       nvarchar(100)    NULL,
    [DestinationId]      uniqueidentifier NULL,      -- no FK on purpose: audit rows outlive configuration
    [StartedAtUtc]       datetime2        NOT NULL,
    [CompletedAtUtc]     datetime2        NOT NULL,
    [DurationMs]         bigint           NOT NULL,
    [Forwarded]          bit              NOT NULL,
    [UpstreamStatusCode] int              NULL,
    [ProxyStatusCode]    int              NOT NULL,
    [Outcome]            nvarchar(30)     NOT NULL,
    [Reason]             nvarchar(100)    NULL
);
CREATE INDEX [IX_RequestLogs_CorrelationId] ON [RequestLogs] ([CorrelationId]);
CREATE INDEX [IX_RequestLogs_StartedAtUtc] ON [RequestLogs] ([StartedAtUtc]);

CREATE TABLE [ConfigurationHistory] (
    [Id]           bigint IDENTITY  NOT NULL CONSTRAINT [PK_ConfigurationHistory] PRIMARY KEY,
    [EntityType]   nvarchar(50)     NOT NULL,
    [EntityId]     uniqueidentifier NOT NULL,
    [ChangeType]   nvarchar(20)     NOT NULL,   -- Added | Modified | Deleted
    [ChangedAtUtc] datetime2        NOT NULL,
    [ChangedBy]    nvarchar(100)    NOT NULL,
    [Changes]      nvarchar(max)    NOT NULL    -- JSON: property -> { old, new }; secret references redacted
);
CREATE INDEX [IX_ConfigurationHistory_EntityType_EntityId] ON [ConfigurationHistory] ([EntityType], [EntityId]);
