using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class LyricsSyncRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncIntervalMinutes",
                table: "LyricsSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "SyncWindowMinutes",
                table: "LyricsSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "LyricsSyncItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlexServerConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    RatingKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AlbumKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    WrittenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NextRefreshUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LyricsSyncItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LyricsSyncItems_PlexServers_PlexServerConnectionId",
                        column: x => x.PlexServerConnectionId,
                        principalTable: "PlexServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LyricsSyncItems_PlexServerConnectionId_RatingKey",
                table: "LyricsSyncItems",
                columns: new[] { "PlexServerConnectionId", "RatingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LyricsSyncItems_Status_NextRefreshUtc",
                table: "LyricsSyncItems",
                columns: new[] { "Status", "NextRefreshUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LyricsSyncItems");

            migrationBuilder.DropColumn(
                name: "SyncIntervalMinutes",
                table: "LyricsSettings");

            migrationBuilder.DropColumn(
                name: "SyncWindowMinutes",
                table: "LyricsSettings");
        }
    }
}
