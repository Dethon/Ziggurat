using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class TelegramLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BoundChats",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                Channel = table.Column<string>(maxLength: 32, nullable: false),
                ChatId = table.Column<string>(maxLength: 64, nullable: false),
                BoundBy = table.Column<string>(maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BoundChats", x => x.Id);
                table.ForeignKey(
                    name: "FK_BoundChats_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LinkedIdentities",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                UserId = table.Column<string>(maxLength: 64, nullable: false),
                Channel = table.Column<string>(maxLength: 32, nullable: false),
                Identity = table.Column<string>(maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LinkedIdentities", x => x.Id);
                table.ForeignKey(
                    name: "FK_LinkedIdentities_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BoundChats_Channel_ChatId",
            table: "BoundChats",
            columns: new[] { "Channel", "ChatId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BoundChats_TenantId",
            table: "BoundChats",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_LinkedIdentities_Channel_Identity",
            table: "LinkedIdentities",
            columns: new[] { "Channel", "Identity" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LinkedIdentities_UserId_Channel",
            table: "LinkedIdentities",
            columns: new[] { "UserId", "Channel" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BoundChats");

        migrationBuilder.DropTable(
            name: "LinkedIdentities");
    }
}