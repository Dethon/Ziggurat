using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class Periods : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Periods",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Start = table.Column<DateOnly>(type: "TEXT", nullable: false),
                End = table.Column<DateOnly>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Periods", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Periods_Start",
            table: "Periods",
            column: "Start");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Periods");
    }
}