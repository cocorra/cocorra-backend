using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipantLifecycleAndStatusCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "SupportTickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusCode",
                table: "SupportTickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastJoinedAt",
                table: "RoomParticipants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeftAt",
                table: "RoomParticipants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejoinCount",
                table: "RoomParticipants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "Reports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusCode",
                table: "Reports",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // AN-033: StatusCode defaulted to 0 (Open) for every existing row above, which is
            // wrong for anything already Resolved or Rejected. Derive it from the string column
            // that is already there — this is a deterministic mapping of an exact existing
            // value, not a guess.
            migrationBuilder.Sql(@"
UPDATE dbo.Reports SET StatusCode = 1 WHERE Status = 'Resolved';
UPDATE dbo.Reports SET StatusCode = 2 WHERE Status = 'Rejected';
UPDATE dbo.SupportTickets SET StatusCode = 1 WHERE Status = 'Resolved';
UPDATE dbo.SupportTickets SET StatusCode = 2 WHERE Status = 'Rejected';
");

            // ResolvedAt is deliberately left NULL for existing rows. UpdatedAt is the last
            // modification, which is only PROBABLY the resolution; copying it would produce a
            // resolution-time series that looks complete but is partly guessed. NULL is the
            // honest value — it means "we do not know when this was resolved", which is true.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "StatusCode",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "LastJoinedAt",
                table: "RoomParticipants");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "RoomParticipants");

            migrationBuilder.DropColumn(
                name: "RejoinCount",
                table: "RoomParticipants");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "StatusCode",
                table: "Reports");
        }
    }
}
