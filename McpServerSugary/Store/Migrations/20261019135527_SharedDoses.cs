using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class SharedDoses : Migration
{
    // A dose's one meal becomes the first of its list before the column goes, so no link is lost.
    // The id is copied as stored, upper case, which is how the list holds a Guid and how a lookup
    // in it compares one.
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MealIds",
            table: "Doses",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.Sql("UPDATE Doses SET MealIds = json_array(MealId) WHERE MealId IS NOT NULL");

        migrationBuilder.DropIndex(
            name: "IX_Doses_MealId",
            table: "Doses");

        migrationBuilder.DropColumn(
            name: "MealId",
            table: "Doses");
    }

    // Going back keeps the first meal of each dose, the only one the old column can hold.
    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "MealId",
            table: "Doses",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE Doses SET MealId = json_extract(MealIds, '$[0]') WHERE json_array_length(MealIds) > 0");

        migrationBuilder.CreateIndex(
            name: "IX_Doses_MealId",
            table: "Doses",
            column: "MealId");

        migrationBuilder.DropColumn(
            name: "MealIds",
            table: "Doses");
    }
}