BEGIN TRANSACTION;
ALTER TABLE [dbo].[User] ADD [PendingMobile] nvarchar(20) NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260905201859_MobileContactChange', N'10.0.11');

COMMIT;
GO

