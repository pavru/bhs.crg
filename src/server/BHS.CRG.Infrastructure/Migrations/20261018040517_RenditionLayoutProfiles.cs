using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenditionLayoutProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LayoutProfileId",
                table: "renditions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LayoutStamp",
                table: "renditions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rendition_layout_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Paper = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Orientation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Fit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MarginTop = table.Column<int>(type: "integer", nullable: false),
                    MarginBottom = table.Column<int>(type: "integer", nullable: false),
                    MarginLeft = table.Column<int>(type: "integer", nullable: false),
                    MarginRight = table.Column<int>(type: "integer", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rendition_layout_profiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rendition_layout_profiles_IsSystem",
                table: "rendition_layout_profiles",
                column: "IsSystem",
                unique: true,
                filter: "\"IsSystem\"");

            // Имя профиля уникально БЕЗ учёта регистра: «Узкий» и «узкий» в меню выбора человек не
            // различит. Индекс по выражению моделью не выразить, поэтому руками. Системный профиль
            // здесь не заводится: его создаёт сидер при старте (RenditionLayoutProfileSeeder) — данные
            // в миграции стали бы замороженной историей.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_rendition_layout_profiles_name"
                    ON rendition_layout_profiles (lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rendition_layout_profiles");

            migrationBuilder.DropColumn(
                name: "LayoutProfileId",
                table: "renditions");

            migrationBuilder.DropColumn(
                name: "LayoutStamp",
                table: "renditions");
        }
    }
}
