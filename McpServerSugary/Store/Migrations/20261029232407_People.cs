using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class People : Migration
{
    // Every row already here is given 'legacy' (Owners.Unassigned, spelled out because a migration
    // must not change when the constant does) in each owner column: nobody's, and so seen by nobody,
    // until the migration into the home household gives it to the person whose log it was.
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_DayNotes",
            table: "DayNotes");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Recipes",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "PushSubscriptions",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "PushSubscriptions",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Periods",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "Periods",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Pens",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "Pens",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Meals",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "Meals",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Foods",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "ExerciseSessions",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "ExerciseSessions",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "Doses",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "Doses",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "UserId",
            table: "DayNotes",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            table: "DayNotes",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddPrimaryKey(
            name: "PK_DayNotes",
            table: "DayNotes",
            columns: new[] { "UserId", "Day" });

        migrationBuilder.CreateIndex(
            name: "IX_Recipes_TenantId",
            table: "Recipes",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_PushSubscriptions_UserId",
            table: "PushSubscriptions",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Periods_UserId",
            table: "Periods",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Pens_UserId",
            table: "Pens",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Meals_UserId",
            table: "Meals",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Foods_TenantId",
            table: "Foods",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_ExerciseSessions_UserId",
            table: "ExerciseSessions",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Doses_UserId",
            table: "Doses",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_DayNotes_UserId",
            table: "DayNotes",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Recipes_TenantId",
            table: "Recipes");

        migrationBuilder.DropIndex(
            name: "IX_PushSubscriptions_UserId",
            table: "PushSubscriptions");

        migrationBuilder.DropIndex(
            name: "IX_Periods_UserId",
            table: "Periods");

        migrationBuilder.DropIndex(
            name: "IX_Pens_UserId",
            table: "Pens");

        migrationBuilder.DropIndex(
            name: "IX_Meals_UserId",
            table: "Meals");

        migrationBuilder.DropIndex(
            name: "IX_Foods_TenantId",
            table: "Foods");

        migrationBuilder.DropIndex(
            name: "IX_ExerciseSessions_UserId",
            table: "ExerciseSessions");

        migrationBuilder.DropIndex(
            name: "IX_Doses_UserId",
            table: "Doses");

        migrationBuilder.DropPrimaryKey(
            name: "PK_DayNotes",
            table: "DayNotes");

        migrationBuilder.DropIndex(
            name: "IX_DayNotes_UserId",
            table: "DayNotes");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Recipes");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "PushSubscriptions");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "PushSubscriptions");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Periods");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "Periods");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Pens");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "Pens");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Meals");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "Meals");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Foods");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "ExerciseSessions");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "ExerciseSessions");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "Doses");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "Doses");

        migrationBuilder.DropColumn(
            name: "UserId",
            table: "DayNotes");

        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "DayNotes");

        migrationBuilder.AddPrimaryKey(
            name: "PK_DayNotes",
            table: "DayNotes",
            column: "Day");
    }
}