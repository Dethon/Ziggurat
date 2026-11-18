using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class GlucoseReadings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GlucoseReadings",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                At = table.Column<DateTime>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                MgPerDl = table.Column<double>(type: "REAL", nullable: false),
                Trend = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseReadings", x => new { x.UserId, x.At });
            });

        migrationBuilder.CreateTable(
            name: "GlucoseSensors",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Serial = table.Column<string>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ProductType = table.Column<int>(type: "INTEGER", nullable: true),
                LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GlucoseSensors", x => new { x.UserId, x.Serial });
            });

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseReadings_UserId",
            table: "GlucoseReadings",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_GlucoseSensors_UserId",
            table: "GlucoseSensors",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GlucoseReadings");

        migrationBuilder.DropTable(
            name: "GlucoseSensors");
    }
}