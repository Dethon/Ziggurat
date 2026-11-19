using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class GlucoseWatches : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GlucoseWatches",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Id = table.Column<string>(type: "TEXT", nullable: false),
                SubjectUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                WriterUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                Spec = table.Column<string>(type: "TEXT", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                Once = table.Column<bool>(type: "INTEGER", nullable: false),
                DeliverTo = table.Column<string>(type: "TEXT", nullable: true),
                UserId = table.Column<string>(type: "TEXT", nullable: true),
                Delegation = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastFiredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Spent = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseWatches", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "GlucoseWatchStates",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                WatchId = table.Column<string>(type: "TEXT", nullable: false),
                TriggerIndex = table.Column<int>(type: "INTEGER", nullable: false),
                Holding = table.Column<bool>(type: "INTEGER", nullable: false),
                HoldingSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                FiredThisEpisode = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseWatchStates", x => new { x.TenantId, x.WatchId, x.TriggerIndex });
                table.ForeignKey(
                    name: "FK_GlucoseWatchStates_GlucoseWatches_TenantId_WatchId",
                    columns: x => new { x.TenantId, x.WatchId },
                    principalTable: "GlucoseWatches",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseWatches_TenantId",
            table: "GlucoseWatches",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseWatches_TenantId_SubjectUserId",
            table: "GlucoseWatches",
            columns: new[] { "TenantId", "SubjectUserId" });

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseWatchStates_TenantId",
            table: "GlucoseWatchStates",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GlucoseWatchStates");

        migrationBuilder.DropTable(
            name: "GlucoseWatches");
    }
}