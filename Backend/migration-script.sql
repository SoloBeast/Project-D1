BEGIN TRANSACTION;
ALTER TABLE [dbo].[OtpChallenge] ADD [PasswordResetConsumedAtUtc] datetime2 NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260905014727_PasswordResetAuthorizationConsumption', N'10.0.11');

COMMIT;
GO

