using Microsoft.EntityFrameworkCore.Infrastructure;
using PlexBridge.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261113170000_MigrationAuditPlexUser")]
    public partial class MigrationAuditPlexUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlexUserName",
                table: "MigrationAudits",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlexUserUuid",
                table: "MigrationAudits",
                type: "TEXT",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PlexUserName", table: "MigrationAudits");
            migrationBuilder.DropColumn(name: "PlexUserUuid", table: "MigrationAudits");
        }
    }
}
