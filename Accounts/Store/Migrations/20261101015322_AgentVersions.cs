using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class AgentVersions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentVersions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AgentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                CustomInstructions = table.Column<string>(type: "TEXT", nullable: true),
                ReplacedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ReplacedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentVersions", x => x.Id);
                table.ForeignKey(
                    name: "FK_AgentVersions_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_AgentVersions_Users_ReplacedBy",
                    column: x => x.ReplacedBy,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentVersions_ReplacedBy",
            table: "AgentVersions",
            column: "ReplacedBy");

        migrationBuilder.CreateIndex(
            name: "IX_AgentVersions_TenantId_AgentId_ReplacedAt",
            table: "AgentVersions",
            columns: new[] { "TenantId", "AgentId", "ReplacedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AgentVersions");
    }
}