using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class WidgetTokens : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WidgetTokens",
            columns: table => new
            {
                Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                LogUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WidgetTokens", x => x.Hash);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WidgetTokens_TenantId_UserId_LogUserId",
            table: "WidgetTokens",
            columns: new[] { "TenantId", "UserId", "LogUserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WidgetTokens_UserId",
            table: "WidgetTokens",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WidgetTokens");
    }
}