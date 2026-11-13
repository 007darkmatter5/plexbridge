using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerSlot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "PlexServers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Slot",
                table: "PlexServers");
        }
    }
}
