using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddTextRoomsDictionaryAndReportContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContextAfterRequested",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextBeforeRequested",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContextCompletedAt",
                table: "Reports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MatchedDictionaryEntryId",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedTerm",
                table: "Reports",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReportedTextRoomId",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReportedTextRoomMessageId",
                table: "Reports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Severity",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Reports",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginRoomId",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginTextRoomId",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportMessageContextEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextRoomMessageId = table.Column<long>(type: "bigint", nullable: false),
                    Offset = table.Column<int>(type: "int", nullable: false),
                    SenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderDisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WasDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportMessageContextEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportMessageContextEntries_Reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TextRoomGlobalSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Defaults_IsAlwaysOn = table.Column<bool>(type: "bit", nullable: false),
                    Defaults_DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    Defaults_RotateTopicEveryMinutes = table.Column<int>(type: "int", nullable: true),
                    Defaults_ClearHistoryOnRotate = table.Column<bool>(type: "bit", nullable: false),
                    Defaults_MaxParticipants = table.Column<int>(type: "int", nullable: true),
                    Defaults_AutoOverflowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Defaults_OverflowTriggerCount = table.Column<int>(type: "int", nullable: true),
                    Defaults_MaxOverflowInstances = table.Column<int>(type: "int", nullable: true),
                    Defaults_ShowOnlineCountToUsers = table.Column<bool>(type: "bit", nullable: false),
                    Defaults_AllowPrivateChatFromRoom = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Defaults_SlowModeSeconds = table.Column<int>(type: "int", nullable: false),
                    Defaults_MaxMessageLength = table.Column<int>(type: "int", nullable: false, defaultValue: 500),
                    Defaults_MessageRetentionHours = table.Column<int>(type: "int", nullable: true),
                    Defaults_ReportContextMessagesBefore = table.Column<int>(type: "int", nullable: true),
                    Defaults_ReportContextMessagesAfter = table.Column<int>(type: "int", nullable: true),
                    ReportContextMessagesBefore = table.Column<int>(type: "int", nullable: false),
                    ReportContextMessagesAfter = table.Column<int>(type: "int", nullable: false),
                    DefaultMessageRetentionHours = table.Column<int>(type: "int", nullable: false),
                    AllowModeratorsToManageRooms = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextRoomGlobalSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TextRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DiscussionPrompts = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TopicPool = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TopicRotationIndex = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Settings_IsAlwaysOn = table.Column<bool>(type: "bit", nullable: false),
                    Settings_DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    Settings_RotateTopicEveryMinutes = table.Column<int>(type: "int", nullable: true),
                    Settings_ClearHistoryOnRotate = table.Column<bool>(type: "bit", nullable: false),
                    Settings_MaxParticipants = table.Column<int>(type: "int", nullable: true),
                    Settings_AutoOverflowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Settings_OverflowTriggerCount = table.Column<int>(type: "int", nullable: true),
                    Settings_MaxOverflowInstances = table.Column<int>(type: "int", nullable: true),
                    Settings_ShowOnlineCountToUsers = table.Column<bool>(type: "bit", nullable: false),
                    Settings_AllowPrivateChatFromRoom = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Settings_SlowModeSeconds = table.Column<int>(type: "int", nullable: false),
                    Settings_MaxMessageLength = table.Column<int>(type: "int", nullable: false, defaultValue: 500),
                    Settings_MessageRetentionHours = table.Column<int>(type: "int", nullable: true),
                    Settings_ReportContextMessagesBefore = table.Column<int>(type: "int", nullable: true),
                    Settings_ReportContextMessagesAfter = table.Column<int>(type: "int", nullable: true),
                    OpensAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NextRotationAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HistoryClearedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ParentRoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OverflowIndex = table.Column<int>(type: "int", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextRooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TextRoomSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SpawnEveryMinutes = table.Column<int>(type: "int", nullable: false),
                    WindowStartMinuteOfDay = table.Column<int>(type: "int", nullable: true),
                    WindowEndMinuteOfDay = table.Column<int>(type: "int", nullable: true),
                    TopicPool = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NextTopicIndex = table.Column<int>(type: "int", nullable: false),
                    Settings_IsAlwaysOn = table.Column<bool>(type: "bit", nullable: false),
                    Settings_DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    Settings_RotateTopicEveryMinutes = table.Column<int>(type: "int", nullable: true),
                    Settings_ClearHistoryOnRotate = table.Column<bool>(type: "bit", nullable: false),
                    Settings_MaxParticipants = table.Column<int>(type: "int", nullable: true),
                    Settings_AutoOverflowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Settings_OverflowTriggerCount = table.Column<int>(type: "int", nullable: true),
                    Settings_MaxOverflowInstances = table.Column<int>(type: "int", nullable: true),
                    Settings_ShowOnlineCountToUsers = table.Column<bool>(type: "bit", nullable: false),
                    Settings_AllowPrivateChatFromRoom = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Settings_SlowModeSeconds = table.Column<int>(type: "int", nullable: false),
                    Settings_MaxMessageLength = table.Column<int>(type: "int", nullable: false, defaultValue: 500),
                    Settings_MessageRetentionHours = table.Column<int>(type: "int", nullable: true),
                    Settings_ReportContextMessagesBefore = table.Column<int>(type: "int", nullable: true),
                    Settings_ReportContextMessagesAfter = table.Column<int>(type: "int", nullable: true),
                    ClosePreviousOnSpawn = table.Column<bool>(type: "bit", nullable: false),
                    NextSpawnAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSpawnAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextRoomSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WordDictionaryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Term = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedTerm = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MatchType = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordDictionaryEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TextRoomMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TextRoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsMasked = table.Column<bool>(type: "bit", nullable: false),
                    IsReported = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextRoomMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextRoomMessages_AspNetUsers_SenderId",
                        column: x => x.SenderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TextRoomMessages_TextRooms_TextRoomId",
                        column: x => x.TextRoomId,
                        principalTable: "TextRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TextRoomParticipants",
                columns: table => new
                {
                    TextRoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstJoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MutedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MutedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsKicked = table.Column<bool>(type: "bit", nullable: false),
                    KickedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    KickedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextRoomParticipants", x => new { x.TextRoomId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TextRoomParticipants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TextRoomParticipants_TextRooms_TextRoomId",
                        column: x => x.TextRoomId,
                        principalTable: "TextRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ContextCompletedAt_ReportedTextRoomMessageId",
                table: "Reports",
                columns: new[] { "ContextCompletedAt", "ReportedTextRoomMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReportedTextRoomId",
                table: "Reports",
                column: "ReportedTextRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReportedTextRoomMessageId",
                table: "Reports",
                column: "ReportedTextRoomMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId_ReportedTextRoomMessageId",
                table: "Reports",
                columns: new[] { "ReporterId", "ReportedTextRoomMessageId" },
                unique: true,
                filter: "[ReportedTextRoomMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Severity_CreatedAt",
                table: "Reports",
                columns: new[] { "Severity", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportMessageContextEntries_ReportId_TextRoomMessageId",
                table: "ReportMessageContextEntries",
                columns: new[] { "ReportId", "TextRoomMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TextRoomMessages_IsReported_CreatedAt",
                table: "TextRoomMessages",
                columns: new[] { "IsReported", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TextRoomMessages_SenderId",
                table: "TextRoomMessages",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "IX_TextRoomMessages_TextRoomId_Id",
                table: "TextRoomMessages",
                columns: new[] { "TextRoomId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TextRoomParticipants_UserId",
                table: "TextRoomParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TextRooms_ParentRoomId",
                table: "TextRooms",
                column: "ParentRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_TextRooms_ScheduleId",
                table: "TextRooms",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_TextRooms_Status_OpensAt",
                table: "TextRooms",
                columns: new[] { "Status", "OpensAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TextRoomSchedules_IsEnabled_NextSpawnAt",
                table: "TextRoomSchedules",
                columns: new[] { "IsEnabled", "NextSpawnAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WordDictionaryEntries_IsActive",
                table: "WordDictionaryEntries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WordDictionaryEntries_NormalizedTerm_MatchType",
                table: "WordDictionaryEntries",
                columns: new[] { "NormalizedTerm", "MatchType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_TextRooms_ReportedTextRoomId",
                table: "Reports",
                column: "ReportedTextRoomId",
                principalTable: "TextRooms",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reports_TextRooms_ReportedTextRoomId",
                table: "Reports");

            migrationBuilder.DropTable(
                name: "ReportMessageContextEntries");

            migrationBuilder.DropTable(
                name: "TextRoomGlobalSettings");

            migrationBuilder.DropTable(
                name: "TextRoomMessages");

            migrationBuilder.DropTable(
                name: "TextRoomParticipants");

            migrationBuilder.DropTable(
                name: "TextRoomSchedules");

            migrationBuilder.DropTable(
                name: "WordDictionaryEntries");

            migrationBuilder.DropTable(
                name: "TextRooms");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ContextCompletedAt_ReportedTextRoomMessageId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReportedTextRoomId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReportedTextRoomMessageId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReporterId_ReportedTextRoomMessageId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_Severity_CreatedAt",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ContextAfterRequested",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ContextBeforeRequested",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ContextCompletedAt",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MatchedDictionaryEntryId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MatchedTerm",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReportedTextRoomId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReportedTextRoomMessageId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "OriginRoomId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "OriginTextRoomId",
                table: "Messages");
        }
    }
}
