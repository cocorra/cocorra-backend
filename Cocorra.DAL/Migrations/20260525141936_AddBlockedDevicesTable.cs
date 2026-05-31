using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedDevicesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockedId",
                table: "UserBlocks");

            migrationBuilder.DropForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockerId",
                table: "UserBlocks");

            migrationBuilder.AddColumn<Guid>(
                name: "BlockedDeviceId",
                table: "UserBlocks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlockedDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    DeviceName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeviceModel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeviceType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeviceOs = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsBlocked = table.Column<bool>(type: "bit", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlockedDevices_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserBlocks_BlockedDeviceId",
                table: "UserBlocks",
                column: "BlockedDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedDevices_ApplicationUserId",
                table: "BlockedDevices",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedDevices_DeviceId",
                table: "BlockedDevices",
                column: "DeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockedId",
                table: "UserBlocks",
                column: "BlockedId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockerId",
                table: "UserBlocks",
                column: "BlockerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBlocks_BlockedDevices_BlockedDeviceId",
                table: "UserBlocks",
                column: "BlockedDeviceId",
                principalTable: "BlockedDevices",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockedId",
                table: "UserBlocks");

            migrationBuilder.DropForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockerId",
                table: "UserBlocks");

            migrationBuilder.DropForeignKey(
                name: "FK_UserBlocks_BlockedDevices_BlockedDeviceId",
                table: "UserBlocks");

            migrationBuilder.DropTable(
                name: "BlockedDevices");

            migrationBuilder.DropIndex(
                name: "IX_UserBlocks_BlockedDeviceId",
                table: "UserBlocks");

            migrationBuilder.DropColumn(
                name: "BlockedDeviceId",
                table: "UserBlocks");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockedId",
                table: "UserBlocks",
                column: "BlockedId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserBlocks_AspNetUsers_BlockerId",
                table: "UserBlocks",
                column: "BlockerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
