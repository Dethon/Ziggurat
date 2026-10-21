using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class RecipeHelpings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RecipeId",
            table: "MealItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_MealItems_RecipeId",
            table: "MealItems",
            column: "RecipeId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MealItems_RecipeId",
            table: "MealItems");

        migrationBuilder.DropColumn(
            name: "RecipeId",
            table: "MealItems");
    }
}