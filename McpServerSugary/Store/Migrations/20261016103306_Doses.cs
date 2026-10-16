using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class Doses : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Doses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TakenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Units = table.Column<double>(type: "REAL", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Note = table.Column<string>(type: "TEXT", nullable: true),
                MealId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Doses", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Doses_MealId",
            table: "Doses",
            column: "MealId");

        migrationBuilder.CreateIndex(
            name: "IX_Doses_TakenAt",
            table: "Doses",
            column: "TakenAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Doses");
    }
}