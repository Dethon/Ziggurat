using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Foods",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                CarbsPer100g = table.Column<double>(type: "REAL", nullable: true),
                Unit = table.Column<string>(type: "TEXT", nullable: false),
                PortionGrams = table.Column<double>(type: "REAL", nullable: true),
                PortionCarbs = table.Column<double>(type: "REAL", nullable: true),
                Barcode = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Foods", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Meals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                EatenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                Outcome = table.Column<string>(type: "TEXT", nullable: false),
                Photo = table.Column<byte[]>(type: "BLOB", nullable: true),
                FirstEstimateLow = table.Column<double>(type: "REAL", nullable: true),
                FirstEstimateBest = table.Column<double>(type: "REAL", nullable: true),
                FirstEstimateHigh = table.Column<double>(type: "REAL", nullable: true),
                CarbsLow = table.Column<double>(type: "REAL", nullable: false),
                Carbs = table.Column<double>(type: "REAL", nullable: false),
                CarbsHigh = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Meals", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MealItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Portion = table.Column<string>(type: "TEXT", nullable: true),
                Grams = table.Column<double>(type: "REAL", nullable: true),
                CarbsPer100g = table.Column<double>(type: "REAL", nullable: true),
                CarbsLow = table.Column<double>(type: "REAL", nullable: false),
                Carbs = table.Column<double>(type: "REAL", nullable: false),
                CarbsHigh = table.Column<double>(type: "REAL", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Confidence = table.Column<string>(type: "TEXT", nullable: true),
                Edited = table.Column<bool>(type: "INTEGER", nullable: false),
                FoodId = table.Column<Guid>(type: "TEXT", nullable: true),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                MealId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MealItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_MealItems_Meals_MealId",
                    column: x => x.MealId,
                    principalTable: "Meals",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Foods_Barcode",
            table: "Foods",
            column: "Barcode");

        migrationBuilder.CreateIndex(
            name: "IX_MealItems_FoodId",
            table: "MealItems",
            column: "FoodId");

        migrationBuilder.CreateIndex(
            name: "IX_MealItems_MealId_Position",
            table: "MealItems",
            columns: new[] { "MealId", "Position" });

        migrationBuilder.CreateIndex(
            name: "IX_Meals_EatenAt",
            table: "Meals",
            column: "EatenAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Foods");

        migrationBuilder.DropTable(
            name: "MealItems");

        migrationBuilder.DropTable(
            name: "Meals");
    }
}