using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomIdToUserEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoomId",
                table: "UserEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserEvents_RoomId_EventType_OccurredAtUtc",
                table: "UserEvents",
                columns: new[] { "RoomId", "EventType", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserEvents_RoomId_EventType_OccurredAtUtc",
                table: "UserEvents");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "UserEvents");
        }
    }
}
