using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConstructionTimeZoneAndExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalCode",
                table: "constructions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSystem",
                table: "constructions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "constructions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_constructions_ExternalSystem_ExternalCode",
                table: "constructions",
                columns: new[] { "ExternalSystem", "ExternalCode" },
                unique: true,
                filter: "\"ExternalSystem\" IS NOT NULL AND \"ExternalCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropIndex(
                name: "IX_constructions_ExternalSystem_ExternalCode",
                table: "constructions");

            migrationBuilder.DropColumn(
                name: "ExternalCode",
                table: "constructions");

            migrationBuilder.DropColumn(
                name: "ExternalSystem",
                table: "constructions");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "constructions");
        }
    }
}
