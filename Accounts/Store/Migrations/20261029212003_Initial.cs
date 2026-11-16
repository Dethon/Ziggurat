using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounts.Store.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Tenants",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                Name = table.Column<string>(maxLength: 200, nullable: false),
                IsHome = table.Column<bool>(nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(maxLength: 200, nullable: false),
                AvatarUrl = table.Column<string>(maxLength: 2000, nullable: true),
                Role = table.Column<string>(maxLength: 16, nullable: false),
                IsOperator = table.Column<bool>(nullable: false),
                SeesAllConversations = table.Column<bool>(nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
                table.ForeignKey(
                    name: "FK_Users_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Credentials",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                UserId = table.Column<string>(maxLength: 64, nullable: false),
                CredentialId = table.Column<string>(maxLength: 1400, nullable: false),
                PublicKey = table.Column<byte[]>(nullable: false),
                SignCount = table.Column<long>(nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false),
                LastUsedAt = table.Column<DateTime>(nullable: true),
                RevokedAt = table.Column<DateTime>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Credentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_Credentials_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Links",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TokenHash = table.Column<string>(maxLength: 64, nullable: false),
                Kind = table.Column<string>(maxLength: 16, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                UserId = table.Column<string>(maxLength: 64, nullable: true),
                DisplayName = table.Column<string>(maxLength: 200, nullable: true),
                Role = table.Column<string>(maxLength: 16, nullable: true),
                CreatedBy = table.Column<string>(maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTime>(nullable: false),
                ExpiresAt = table.Column<DateTime>(nullable: false),
                UsedAt = table.Column<DateTime>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Links", x => x.Id);
                table.ForeignKey(
                    name: "FK_Links_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Links_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Sessions",
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 64, nullable: false),
                TokenHash = table.Column<string>(maxLength: 64, nullable: false),
                TenantId = table.Column<string>(maxLength: 64, nullable: false),
                UserId = table.Column<string>(maxLength: 64, nullable: false),
                CredentialId = table.Column<string>(maxLength: 64, nullable: true),
                Device = table.Column<string>(maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(nullable: false),
                LastSeenAt = table.Column<DateTime>(nullable: false),
                RevokedAt = table.Column<DateTime>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_Sessions_Credentials_CredentialId",
                    column: x => x.CredentialId,
                    principalTable: "Credentials",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Sessions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_CredentialId",
            table: "Credentials",
            column: "CredentialId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_UserId",
            table: "Credentials",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Links_TenantId",
            table: "Links",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_Links_TokenHash",
            table: "Links",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Links_UserId",
            table: "Links",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_CredentialId",
            table: "Sessions",
            column: "CredentialId");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_TokenHash",
            table: "Sessions",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_UserId",
            table: "Sessions",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Users_TenantId",
            table: "Users",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Links");

        migrationBuilder.DropTable(
            name: "Sessions");

        migrationBuilder.DropTable(
            name: "Credentials");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "Tenants");
    }
}