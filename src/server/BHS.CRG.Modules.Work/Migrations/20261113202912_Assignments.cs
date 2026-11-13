using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <summary>
    /// Назначения сотрудников на стройки (change work-11-assignments, ТЗ WORK-21, WORK-46). Конец периода
    /// обязателен у монтажника и не раньше начала — проверками базы, на запись мимо сущности. Индекс по
    /// сотруднику нужен вопросу ядра «кто держит сотрудника» (CORE-34.1), по стройке — экрану стройки и
    /// охвату менеджера.
    /// </summary>
    public partial class Assignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "work");

            migrationBuilder.CreateTable(
                name: "assignments",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    date_from = table.Column<DateOnly>(type: "date", nullable: false),
                    date_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.id);
                    table.CheckConstraint("ck_assignments_installer_end", "kind = 'Manager' OR date_to IS NOT NULL");
                    table.CheckConstraint("ck_assignments_period", "date_to IS NULL OR date_to >= date_from");
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_employee",
                schema: "work",
                table: "assignments",
                columns: new[] { "employee_id", "construction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_site",
                schema: "work",
                table: "assignments",
                columns: new[] { "construction_id", "kind", "date_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignments",
                schema: "work");
        }
    }
}
