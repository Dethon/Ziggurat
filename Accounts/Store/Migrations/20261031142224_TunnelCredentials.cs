using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class TunnelCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TunnelCredentials",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                CredentialHash = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                ServerName = table.Column<string>(maxLength: 63, nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false),
                RevokedAt = table.Column<DateTime>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TunnelCredentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_TunnelCredentials_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TunnelCredentials_CredentialHash",
            table: "TunnelCredentials",
            column: "CredentialHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TunnelCredentials_TenantId",
            table: "TunnelCredentials",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TunnelCredentials");
    }
}