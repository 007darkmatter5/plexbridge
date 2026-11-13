using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlexAccountLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlexAccountLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ClientIdentifier = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ProtectedAccountToken = table.Column<string>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PlexUserId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlexAccountLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlexAccountLinks_UserId",
                table: "PlexAccountLinks",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlexAccountLinks");
        }
    }
}
