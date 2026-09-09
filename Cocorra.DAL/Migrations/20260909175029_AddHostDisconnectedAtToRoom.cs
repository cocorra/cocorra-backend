using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddHostDisconnectedAtToRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HostDisconnectedAt",
                table: "Rooms",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostDisconnectedAt",
                table: "Rooms");
        }
    }
}
