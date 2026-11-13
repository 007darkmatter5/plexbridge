using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    SourceServerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationServerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Categories = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AppliedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationAuditItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MigrationAuditId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationAuditItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationAuditItems_MigrationAudits_MigrationAuditId",
                        column: x => x.MigrationAuditId,
                        principalTable: "MigrationAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationAuditItems_MigrationAuditId",
                table: "MigrationAuditItems",
                column: "MigrationAuditId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationAuditItems");

            migrationBuilder.DropTable(
                name: "MigrationAudits");
        }
    }
}
