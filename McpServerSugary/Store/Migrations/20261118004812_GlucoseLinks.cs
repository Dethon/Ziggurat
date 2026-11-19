using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class GlucoseLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GlucoseLinks",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                SealedPassword = table.Column<byte[]>(type: "BLOB", nullable: false),
                Region = table.Column<string>(type: "TEXT", nullable: true),
                PatientId = table.Column<string>(type: "TEXT", nullable: true),
                Unit = table.Column<string>(type: "TEXT", nullable: false),
                TargetLow = table.Column<int>(type: "INTEGER", nullable: false),
                TargetHigh = table.Column<int>(type: "INTEGER", nullable: false),
                LinkedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailure = table.Column<string>(type: "TEXT", nullable: true),
                FailureDetail = table.Column<string>(type: "TEXT", nullable: true),
                FailingSince = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseLinks", x => x.UserId);
            });

        migrationBuilder.CreateTable(
            name: "GlucoseLinkConnections",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                PatientId = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                LinkUserId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseLinkConnections", x => x.Id);
                table.ForeignKey(
                    name: "FK_GlucoseLinkConnections_GlucoseLinks_LinkUserId",
                    column: x => x.LinkUserId,
                    principalTable: "GlucoseLinks",
                    principalColumn: "UserId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseLinkConnections_LinkUserId",
            table: "GlucoseLinkConnections",
            column: "LinkUserId");

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseLinks_UserId",
            table: "GlucoseLinks",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GlucoseLinkConnections");

        migrationBuilder.DropTable(
            name: "GlucoseLinks");
    }
}