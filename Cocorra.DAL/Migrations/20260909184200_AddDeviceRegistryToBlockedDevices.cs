using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceRegistryToBlockedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BlockedDevices_ApplicationUserId",
                table: "BlockedDevices");

            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAt",
                table: "BlockedDevices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "BlockedDevices",
                type: "datetime2",
                nullable: true);

            // Every pre-existing row was created by the block endpoint, so its CreatedAt IS
            // the moment it was blocked. Backfill it before anything reads BlockedAt.
            migrationBuilder.Sql(@"
                UPDATE BlockedDevices
                SET BlockedAt = CreatedAt
                WHERE IsBlocked = 1 AND BlockedAt IS NULL;");

            // The old code only ever inserted after a global DeviceId lookup, so duplicate
            // (ApplicationUserId, DeviceId) pairs should not exist. Collapse them anyway —
            // a unique index that fails halfway through a production migration is far worse
            // than a no-op statement. Survivor = blocked row first, then oldest.
            migrationBuilder.Sql(@"
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
                JOIN Losers l ON ub.BlockedDeviceId = l.DropId;");

            // Separate statement: UserBlocks.BlockedDeviceId is a NoAction FK, so the
            // repoint above has to be committed before the losing rows can go.
            migrationBuilder.Sql(@"
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
                WHERE Id IN (SELECT Id FROM Ranked WHERE Rn > 1);");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedDevices_ApplicationUserId_DeviceId",
                table: "BlockedDevices",
                columns: new[] { "ApplicationUserId", "DeviceId" },
                unique: true,
                filter: "[DeviceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BlockedDevices_ApplicationUserId_DeviceId",
                table: "BlockedDevices");

            migrationBuilder.DropColumn(
                name: "BlockedAt",
                table: "BlockedDevices");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "BlockedDevices");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedDevices_ApplicationUserId",
                table: "BlockedDevices",
                column: "ApplicationUserId");
        }
    }
}
