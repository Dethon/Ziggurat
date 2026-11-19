using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpServerSugary.Store.Migrations;

/// <inheritdoc />
public partial class PersonalProfiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BodyMeasurements",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                On = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Value = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BodyMeasurements", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Profiles",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Sex = table.Column<string>(type: "TEXT", nullable: true),
                DiabetesType = table.Column<string>(type: "TEXT", nullable: true),
                DiagnosedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                BornOn = table.Column<DateOnly>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Profiles", x => x.UserId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BodyMeasurements_On",
            table: "BodyMeasurements",
            column: "On");

        migrationBuilder.CreateIndex(
            name: "IX_BodyMeasurements_UserId",
            table: "BodyMeasurements",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Profiles_UserId",
            table: "Profiles",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BodyMeasurements");

        migrationBuilder.DropTable(
            name: "Profiles");
    }
}
