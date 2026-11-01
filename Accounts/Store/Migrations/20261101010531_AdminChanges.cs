using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class AdminChanges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminChanges",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                ActorUserId = table.Column<string>(maxLength: 64, nullable: false),
                ActorTenantId = table.Column<string>(maxLength: 64, nullable: false),
                Route = table.Column<string>(maxLength: 300, nullable: false),
                Summary = table.Column<string>(maxLength: 500, nullable: false),
                At = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminChanges", x => x.Id);
                table.ForeignKey(
                    name: "FK_AdminChanges_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_AdminChanges_Users_ActorUserId",
                    column: x => x.ActorUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AdminChanges_ActorUserId",
            table: "AdminChanges",
            column: "ActorUserId");

        migrationBuilder.CreateIndex(
            name: "IX_AdminChanges_TenantId_At",
            table: "AdminChanges",
            columns: new[] { "TenantId", "At" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AdminChanges");
    }
}