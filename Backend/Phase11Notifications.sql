BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[NotificationEvent] (
        [Id] bigint NOT NULL IDENTITY,
        [UserId] bigint NOT NULL,
        [EventType] nvarchar(100) NOT NULL,
        [EventKey] nvarchar(200) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [IsCritical] bit NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [ProcessedAtUtc] datetime2 NULL,
        [FailureCode] nvarchar(100) NULL,
        [FailureMessage] nvarchar(1000) NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationEvent] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationEvent_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[User] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[NotificationPreference] (
        [Id] bigint NOT NULL IDENTITY,
        [UserId] bigint NOT NULL,
        [EventType] nvarchar(100) NOT NULL,
        [Channel] nvarchar(30) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationPreference] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationPreference_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[User] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[NotificationTemplate] (
        [Id] bigint NOT NULL IDENTITY,
        [EventType] nvarchar(100) NOT NULL,
        [Channel] nvarchar(30) NOT NULL,
        [Language] nvarchar(10) NOT NULL,
        [TitleTemplate] nvarchar(240) NULL,
        [BodyTemplate] nvarchar(2000) NOT NULL,
        [IsActive] bit NOT NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationTemplate] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[UserDevice] (
        [Id] bigint NOT NULL IDENTITY,
        [UserId] bigint NOT NULL,
        [DeviceIdentifierHash] nvarchar(64) NOT NULL,
        [TokenHash] nvarchar(64) NOT NULL,
        [ProtectedToken] nvarchar(2000) NOT NULL,
        [Platform] nvarchar(30) NOT NULL,
        [DeviceName] nvarchar(160) NULL,
        [IsActive] bit NOT NULL,
        [RegisteredAtUtc] datetime2 NOT NULL,
        [LastSeenAtUtc] datetime2 NULL,
        [InvalidatedAtUtc] datetime2 NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_UserDevice] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserDevice_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[User] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[Notification] (
        [Id] bigint NOT NULL IDENTITY,
        [NotificationEventId] bigint NOT NULL,
        [UserId] bigint NOT NULL,
        [EventType] nvarchar(100) NOT NULL,
        [Title] nvarchar(240) NOT NULL,
        [Body] nvarchar(2000) NOT NULL,
        [DeepLink] nvarchar(500) NULL,
        [ReadAtUtc] datetime2 NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Notification] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Notification_NotificationEvent_NotificationEventId] FOREIGN KEY ([NotificationEventId]) REFERENCES [dbo].[NotificationEvent] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Notification_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[User] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[NotificationDelivery] (
        [Id] bigint NOT NULL IDENTITY,
        [NotificationId] bigint NOT NULL,
        [UserDeviceId] bigint NULL,
        [Channel] nvarchar(30) NOT NULL,
        [ProviderCode] nvarchar(80) NOT NULL,
        [DestinationReference] nvarchar(500) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [AttemptCount] int NOT NULL,
        [NextAttemptAtUtc] datetime2 NULL,
        [DeliveredAtUtc] datetime2 NULL,
        [ProviderMessageId] nvarchar(240) NULL,
        [FailureCode] nvarchar(100) NULL,
        [FailureMessage] nvarchar(1000) NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationDelivery] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_NotificationDelivery_AttemptCount] CHECK ([AttemptCount] >= 0),
        CONSTRAINT [FK_NotificationDelivery_Notification_NotificationId] FOREIGN KEY ([NotificationId]) REFERENCES [dbo].[Notification] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_NotificationDelivery_UserDevice_UserDeviceId] FOREIGN KEY ([UserDeviceId]) REFERENCES [dbo].[UserDevice] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE TABLE [dbo].[NotificationAttempt] (
        [Id] bigint NOT NULL IDENTITY,
        [NotificationDeliveryId] bigint NOT NULL,
        [AttemptNumber] int NOT NULL,
        [Outcome] nvarchar(30) NOT NULL,
        [ProviderMessageId] nvarchar(240) NULL,
        [FailureCode] nvarchar(100) NULL,
        [FailureMessage] nvarchar(1000) NULL,
        [AttemptedAtUtc] datetime2 NOT NULL,
        [PublicId] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
        CONSTRAINT [PK_NotificationAttempt] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationAttempt_NotificationDelivery_NotificationDeliveryId] FOREIGN KEY ([NotificationDeliveryId]) REFERENCES [dbo].[NotificationDelivery] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Notification_NotificationEventId] ON [dbo].[Notification] ([NotificationEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Notification_PublicId] ON [dbo].[Notification] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_Notification_UserId_ReadAtUtc_CreatedAtUtc] ON [dbo].[Notification] ([UserId], [ReadAtUtc], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationAttempt_NotificationDeliveryId_AttemptNumber] ON [dbo].[NotificationAttempt] ([NotificationDeliveryId], [AttemptNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationAttempt_PublicId] ON [dbo].[NotificationAttempt] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationDelivery_NotificationId_Channel_UserDeviceId] ON [dbo].[NotificationDelivery] ([NotificationId], [Channel], [UserDeviceId]) WHERE [UserDeviceId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationDelivery_PublicId] ON [dbo].[NotificationDelivery] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_NotificationDelivery_Status_NextAttemptAtUtc] ON [dbo].[NotificationDelivery] ([Status], [NextAttemptAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_NotificationDelivery_UserDeviceId] ON [dbo].[NotificationDelivery] ([UserDeviceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_NotificationDelivery_NonDeviceChannel] ON [dbo].[NotificationDelivery] ([NotificationId], [Channel]) WHERE [UserDeviceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationEvent_EventKey] ON [dbo].[NotificationEvent] ([EventKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationEvent_PublicId] ON [dbo].[NotificationEvent] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_NotificationEvent_Status_OccurredAtUtc] ON [dbo].[NotificationEvent] ([Status], [OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_NotificationEvent_UserId_OccurredAtUtc] ON [dbo].[NotificationEvent] ([UserId], [OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationPreference_PublicId] ON [dbo].[NotificationPreference] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationPreference_UserId_EventType_Channel] ON [dbo].[NotificationPreference] ([UserId], [EventType], [Channel]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationTemplate_EventType_Channel_Language] ON [dbo].[NotificationTemplate] ([EventType], [Channel], [Language]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_NotificationTemplate_IsActive_EventType] ON [dbo].[NotificationTemplate] ([IsActive], [EventType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationTemplate_PublicId] ON [dbo].[NotificationTemplate] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserDevice_PublicId] ON [dbo].[UserDevice] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserDevice_UserId_DeviceIdentifierHash] ON [dbo].[UserDevice] ([UserId], [DeviceIdentifierHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    CREATE INDEX [IX_UserDevice_UserId_IsActive] ON [dbo].[UserDevice] ([UserId], [IsActive]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_UserDevice_ActiveTokenHash] ON [dbo].[UserDevice] ([TokenHash]) WHERE [IsActive] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817150825_Phase11Notifications'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260817150825_Phase11Notifications', N'10.0.11');
END;

COMMIT;
GO

