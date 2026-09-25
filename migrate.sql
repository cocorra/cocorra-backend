BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909175029_AddHostDisconnectedAtToRoom'
)
BEGIN
    ALTER TABLE [Rooms] ADD [HostDisconnectedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909175029_AddHostDisconnectedAtToRoom'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909175029_AddHostDisconnectedAtToRoom', N'10.0.2');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN
    DROP INDEX [IX_BlockedDevices_ApplicationUserId] ON [BlockedDevices];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN
    ALTER TABLE [BlockedDevices] ADD [BlockedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN
    ALTER TABLE [BlockedDevices] ADD [LastSeenAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN

                    UPDATE BlockedDevices
                    SET BlockedAt = CreatedAt
                    WHERE IsBlocked = 1 AND BlockedAt IS NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN

                    -- Leading semicolon is required: `dotnet ef migrations script --idempotent`
                    -- wraps each statement in BEGIN...END, and a CTE cannot follow an
                    -- unterminated BEGIN.
                    ;WITH Ranked AS (
                        SELECT Id, ApplicationUserId, DeviceId,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ApplicationUserId, DeviceId
                                   ORDER BY CAST(IsBlocked AS INT) DESC, CreatedAt ASC, Id ASC
                               ) AS Rn
                        FROM BlockedDevices
                        WHERE DeviceId IS NOT NULL
                    ),
                    Survivors AS (
                        SELECT ApplicationUserId, DeviceId, Id AS KeepId
                        FROM Ranked WHERE Rn = 1
                    ),
                    Losers AS (
                        SELECT r.Id AS DropId, s.KeepId
                        FROM Ranked r
                        JOIN Survivors s
                          ON s.ApplicationUserId = r.ApplicationUserId
                         AND s.DeviceId = r.DeviceId
                        WHERE r.Rn > 1
                    )
                    UPDATE ub
                    SET ub.BlockedDeviceId = l.KeepId
                    FROM UserBlocks ub
                    JOIN Losers l ON ub.BlockedDeviceId = l.DropId;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN

                    -- Leading semicolon is required: `dotnet ef migrations script --idempotent`
                    -- wraps each statement in BEGIN...END, and a CTE cannot follow an
                    -- unterminated BEGIN.
                    ;WITH Ranked AS (
                        SELECT Id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ApplicationUserId, DeviceId
                                   ORDER BY CAST(IsBlocked AS INT) DESC, CreatedAt ASC, Id ASC
                               ) AS Rn
                        FROM BlockedDevices
                        WHERE DeviceId IS NOT NULL
                    )
                    DELETE FROM BlockedDevices
                    WHERE Id IN (SELECT Id FROM Ranked WHERE Rn > 1);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_BlockedDevices_ApplicationUserId_DeviceId] ON [BlockedDevices] ([ApplicationUserId], [DeviceId]) WHERE [DeviceId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909184200_AddDeviceRegistryToBlockedDevices'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909184200_AddDeviceRegistryToBlockedDevices', N'10.0.2');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909233532_AddWentLiveAtToRoom'
)
BEGIN
    ALTER TABLE [Rooms] ADD [WentLiveAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909233532_AddWentLiveAtToRoom'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909233532_AddWentLiveAtToRoom', N'10.0.2');
END;

COMMIT;
GO

