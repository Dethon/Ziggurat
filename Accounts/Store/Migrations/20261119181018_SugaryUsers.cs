using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class SugaryUsers : Migration
{
    // On for every person already there, so nobody loses the Sugary they had (user's decision,
    // 2026-11-19); the store writes the column on every person it makes after, off unless asked.
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "UsesSugary",
            table: "Users",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "UsesSugary",
            table: "Users");
    }
}