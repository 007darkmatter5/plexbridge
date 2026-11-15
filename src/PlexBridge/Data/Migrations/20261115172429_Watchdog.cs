using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class Watchdog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchdogIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlexServerConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ServerSlot = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaySessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TranscodeSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DisplayTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AccountTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PlayerProduct = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PlayerPlatform = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PlayerDevice = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Guids = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RatingKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Container = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    VideoResolution = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    PlayDecision = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    HardwareTranscode = table.Column<bool>(type: "INTEGER", nullable: true),
                    HardwareFullPipeline = table.Column<bool>(type: "INTEGER", nullable: true),
                    SubtitleBurn = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubtitleCodec = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    StallRule = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TimelineJson = table.Column<string>(type: "TEXT", nullable: false),
                    LogExcerpt = table.Column<string>(type: "TEXT", nullable: true),
                    ProposedAction = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ActionKind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ActionStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ActionDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ActionUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchdogIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchdogIncidents_PlexServers_PlexServerConnectionId",
                        column: x => x.PlexServerConnectionId,
                        principalTable: "PlexServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WatchdogSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlexServerConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    OffsetStallSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    SlowTranscodeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TranscodeMissingSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    LongBufferingSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoveryMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LogFolder = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    UseNotifications = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchdogSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchdogSettings_PlexServers_PlexServerConnectionId",
                        column: x => x.PlexServerConnectionId,
                        principalTable: "PlexServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchdogIncidents_PlexServerConnectionId_StartedUtc",
                table: "WatchdogIncidents",
                columns: new[] { "PlexServerConnectionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchdogSettings_PlexServerConnectionId",
                table: "WatchdogSettings",
                column: "PlexServerConnectionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchdogIncidents");

            migrationBuilder.DropTable(
                name: "WatchdogSettings");
        }
    }
}
