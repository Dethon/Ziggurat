using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class HouseholdKeyChecks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HouseholdKeyChecks",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                Key = table.Column<string>(maxLength: 100, nullable: false),
                Fingerprint = table.Column<string>(maxLength: 64, nullable: false),
                State = table.Column<string>(maxLength: 16, nullable: false),
                CheckedAt = table.Column<DateTime>(nullable: false),
                Reason = table.Column<string>(maxLength: 300, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HouseholdKeyChecks", x => x.Id);
                table.ForeignKey(
                    name: "FK_HouseholdKeyChecks_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_HouseholdKeyChecks_TenantId_Key",
            table: "HouseholdKeyChecks",
            columns: new[] { "TenantId", "Key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "HouseholdKeyChecks");
    }
}