using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryMount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MusicSectionTitle",
                table: "LyricsSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryMounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlexServerConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerFolder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UseManualMaps = table.Column<bool>(type: "INTEGER", nullable: false),
                    PathMapsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SectionMatchesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryMounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryMounts_PlexServers_PlexServerConnectionId",
                        column: x => x.PlexServerConnectionId,
                        principalTable: "PlexServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryMounts_PlexServerConnectionId",
                table: "LibraryMounts",
                column: "PlexServerConnectionId",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO LibraryMounts (PlexServerConnectionId, ContainerFolder, UseManualMaps, PathMapsJson, SectionMatchesJson)
                SELECT PlexServerConnectionId, ContainerFolder, UseManualMaps, PathMapsJson, NULL
                FROM LyricsSettings
                WHERE NOT EXISTS (
                    SELECT 1 FROM LibraryMounts AS existing
                    WHERE existing.PlexServerConnectionId = LyricsSettings.PlexServerConnectionId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryMounts");

            migrationBuilder.DropColumn(
                name: "MusicSectionTitle",
                table: "LyricsSettings");
        }
    }
}
