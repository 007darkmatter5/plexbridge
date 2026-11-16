using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class Lyrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LyricsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlexServerConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PathMapsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AutoCheckEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoCheckIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAutoCheckUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LyricsSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LyricsSettings_PlexServers_PlexServerConnectionId",
                        column: x => x.PlexServerConnectionId,
                        principalTable: "PlexServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LyricsSettings_PlexServerConnectionId",
                table: "LyricsSettings",
                column: "PlexServerConnectionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LyricsSettings");
        }
    }
}
