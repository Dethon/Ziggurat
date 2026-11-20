using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class DayFlowAndSymptoms : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Flow",
            table: "DayNotes",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Symptoms",
            table: "DayNotes",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Flow",
            table: "DayNotes");

        migrationBuilder.DropColumn(
            name: "Symptoms",
            table: "DayNotes");
    }
}