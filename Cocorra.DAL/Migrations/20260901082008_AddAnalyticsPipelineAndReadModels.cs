using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cocorra.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsPipelineAndReadModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "UserEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "UserEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<byte>(
                name: "SchemaVersion",
                table: "UserEvents",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.CreateTable(
                name: "AggregationCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PipelineName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    LastProcessedEventId = table.Column<long>(type: "bigint", nullable: false),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregationCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyFunnelMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CohortDate = table.Column<DateTime>(type: "date", nullable: false),
                    FunnelName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    StepIndex = table.Column<byte>(type: "tinyint", nullable: false),
                    StepName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    UsersReached = table.Column<int>(type: "int", nullable: false),
                    MedianSecondsFromPrevious = table.Column<int>(type: "int", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyFunnelMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyHostMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    HostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomsCreated = table.Column<int>(type: "int", nullable: false),
                    RoomsGoneLive = table.Column<int>(type: "int", nullable: false),
                    TotalJoinersAcrossRooms = table.Column<int>(type: "int", nullable: false),
                    ReportsAboutHostRooms = table.Column<int>(type: "int", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyHostMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyPlatformMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    RoomsCreated = table.Column<int>(type: "int", nullable: false),
                    RoomsGoneLive = table.Column<int>(type: "int", nullable: false),
                    DistinctActiveHosts = table.Column<int>(type: "int", nullable: false),
                    DistinctJoiningUsers = table.Column<int>(type: "int", nullable: false),
                    DistinctSpeakingUsers = table.Column<int>(type: "int", nullable: false),
                    TotalSpokenSeconds = table.Column<long>(type: "bigint", nullable: false),
                    NewRegistrations = table.Column<int>(type: "int", nullable: false),
                    VoiceVerificationsSubmitted = table.Column<int>(type: "int", nullable: false),
                    VoiceVerificationsApproved = table.Column<int>(type: "int", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyPlatformMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyRoomMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SelectionMode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    StageCapacity = table.Column<int>(type: "int", nullable: false),
                    DistinctJoiners = table.Column<int>(type: "int", nullable: false),
                    DistinctSpeakers = table.Column<int>(type: "int", nullable: false),
                    HandRaises = table.Column<int>(type: "int", nullable: false),
                    StagePromotions = table.Column<int>(type: "int", nullable: false),
                    TotalSpokenSeconds = table.Column<int>(type: "int", nullable: false),
                    ReportsCount = table.Column<int>(type: "int", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyRoomMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetterEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PropertiesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeadLetteredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterEvents", x => x.Id);
                });

            // AN-002 steps 4-5: every pre-existing row received the all-zeros default above,
            // so UX_UserEvents_EventId (created further down) would fail on any table holding
            // two or more rows. Backfill first, in bounded batches: the table receives
            // concurrent inserts, and a single UPDATE over 180 days of events would hold locks
            // and grow the log. suppressTransaction keeps each batch its own transaction so the
            // batching actually bounds log growth instead of accumulating inside one.
            migrationBuilder.Sql(@"
DECLARE @BatchSize INT = 5000;
DECLARE @Rows INT = 1;
WHILE @Rows > 0
BEGIN
    UPDATE TOP (@BatchSize) dbo.UserEvents
       SET EventId = NEWID()
     WHERE EventId = '00000000-0000-0000-0000-000000000000';
    SET @Rows = @@ROWCOUNT;
END
", suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserEvents_CorrelationId",
                table: "UserEvents",
                column: "CorrelationId",
                filter: "[CorrelationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_UserEvents_EventId",
                table: "UserEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AggregationCheckpoints_PipelineName",
                table: "AggregationCheckpoints",
                column: "PipelineName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DailyFunnelMetrics_CohortDate_Funnel_Step",
                table: "DailyFunnelMetrics",
                columns: new[] { "CohortDate", "FunnelName", "StepIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DailyHostMetrics_Date_HostId",
                table: "DailyHostMetrics",
                columns: new[] { "Date", "HostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DailyPlatformMetrics_Date",
                table: "DailyPlatformMetrics",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DailyRoomMetrics_Date_RoomId",
                table: "DailyRoomMetrics",
                columns: new[] { "Date", "RoomId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvents_DeadLetteredAtUtc",
                table: "DeadLetterEvents",
                column: "DeadLetteredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvents_EventId",
                table: "DeadLetterEvents",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AggregationCheckpoints");

            migrationBuilder.DropTable(
                name: "DailyFunnelMetrics");

            migrationBuilder.DropTable(
                name: "DailyHostMetrics");

            migrationBuilder.DropTable(
                name: "DailyPlatformMetrics");

            migrationBuilder.DropTable(
                name: "DailyRoomMetrics");

            migrationBuilder.DropTable(
                name: "DeadLetterEvents");

            migrationBuilder.DropIndex(
                name: "IX_UserEvents_CorrelationId",
                table: "UserEvents");

            migrationBuilder.DropIndex(
                name: "UX_UserEvents_EventId",
                table: "UserEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "UserEvents");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "UserEvents");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "UserEvents");
        }
    }
}
