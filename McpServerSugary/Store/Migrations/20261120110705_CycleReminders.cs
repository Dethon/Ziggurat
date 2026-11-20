using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class CycleReminders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CycleReminders",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                SentFor = table.Column<DateOnly>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CycleReminders", x => new { x.UserId, x.TenantId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_CycleReminders_UserId",
            table: "CycleReminders",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CycleReminders");
    }
}