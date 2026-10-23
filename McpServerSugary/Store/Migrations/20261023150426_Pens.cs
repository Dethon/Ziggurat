using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class Pens : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Pens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Insulin = table.Column<string>(type: "TEXT", nullable: false),
                OpenedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Days = table.Column<int>(type: "INTEGER", nullable: false),
                ReplacedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                TomorrowSentFor = table.Column<DateOnly>(type: "TEXT", nullable: true),
                TodaySentFor = table.Column<DateOnly>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Pens", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Pens_Kind",
            table: "Pens",
            column: "Kind");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Pens");
    }
}