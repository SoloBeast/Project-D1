BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_UserRole_BranchId_UserId] ON [dbo].[UserRole] ([BranchId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_User_UserType_CreatedAtUtc_Id] ON [dbo].[User] ([UserType], [CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_Subscription_BranchId_CreatedAtUtc_Id] ON [dbo].[Subscription] ([BranchId], [CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_Payment_CustomerId_CreatedAtUtc_Id] ON [dbo].[Payment] ([CustomerId], [CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_Payment_Status_CreatedAtUtc_Id] ON [dbo].[Payment] ([Status], [CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_AuditLog_CreatedAtUtc_Id] ON [dbo].[AuditLog] ([CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    CREATE INDEX [IX_AuditLog_UserId_CreatedAtUtc_Id] ON [dbo].[AuditLog] ([UserId], [CreatedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817185305_Phase12ReportIndexes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260817185305_Phase12ReportIndexes', N'10.0.11');
END;

COMMIT;
GO

