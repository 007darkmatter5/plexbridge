using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class LyricsLibraryMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContainerFolder",
                table: "LyricsSettings",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationMatchesJson",
                table: "LyricsSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicSectionKey",
                table: "LyricsSettings",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseManualMaps",
                table: "LyricsSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerFolder",
                table: "LyricsSettings");

            migrationBuilder.DropColumn(
                name: "LocationMatchesJson",
                table: "LyricsSettings");

            migrationBuilder.DropColumn(
                name: "MusicSectionKey",
                table: "LyricsSettings");

            migrationBuilder.DropColumn(
                name: "UseManualMaps",
                table: "LyricsSettings");
        }
    }
}
