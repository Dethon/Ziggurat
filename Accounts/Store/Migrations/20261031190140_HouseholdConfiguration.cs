using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class HouseholdConfiguration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HouseholdDataKeys",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                WrappedKey = table.Column<byte[]>(nullable: false),
                MasterKeyId = table.Column<string>(maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false),
                RewrappedAt = table.Column<DateTime>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HouseholdDataKeys", x => x.Id);
                table.ForeignKey(
                    name: "FK_HouseholdDataKeys_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "HouseholdSections",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                Section = table.Column<string>(maxLength: 32, nullable: false),
                Document = table.Column<string>(nullable: false),
                UpdatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HouseholdSections", x => x.Id);
                table.ForeignKey(
                    name: "FK_HouseholdSections_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_HouseholdDataKeys_TenantId",
            table: "HouseholdDataKeys",
            column: "TenantId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_HouseholdSections_TenantId_Section",
            table: "HouseholdSections",
            columns: new[] { "TenantId", "Section" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "HouseholdDataKeys");

        migrationBuilder.DropTable(
            name: "HouseholdSections");
    }
}