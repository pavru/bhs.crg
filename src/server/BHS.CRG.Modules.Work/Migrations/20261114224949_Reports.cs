using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "report_seq",
                schema: "work");

            migrationBuilder.CreateTable(
                name: "report_changes",
                schema: "work",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    device_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    server_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_changes", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    shift = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    hours = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.id);
                    table.CheckConstraint("ck_reports_hours", "hours IS NULL OR (hours >= 0 AND hours <= 24)");
                    table.CheckConstraint("ck_reports_shift", "shift IN ('day', 'night')");
                });

            migrationBuilder.CreateTable(
                name: "report_lines",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    volume = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    is_crew = table.Column<bool>(type: "boolean", nullable: false),
                    work_plan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_lines_reports_report_id",
                        column: x => x.report_id,
                        principalSchema: "work",
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_line_materials",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_line_materials", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_line_materials_report_lines_line_id",
                        column: x => x.line_id,
                        principalSchema: "work",
                        principalTable: "report_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_line_participants",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_line_participants", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_line_participants_report_lines_line_id",
                        column: x => x.line_id,
                        principalSchema: "work",
                        principalTable: "report_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_report_changes_employee",
                schema: "work",
                table: "report_changes",
                columns: new[] { "employee_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_report_changes_site",
                schema: "work",
                table: "report_changes",
                columns: new[] { "construction_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ux_report_changes_change",
                schema: "work",
                table: "report_changes",
                columns: new[] { "report_id", "change_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_line_materials_line",
                schema: "work",
                table: "report_line_materials",
                columns: new[] { "line_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_report_line_materials_nomenclature",
                schema: "work",
                table: "report_line_materials",
                column: "nomenclature_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_line_participants_employee",
                schema: "work",
                table: "report_line_participants",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_line_participants_line",
                schema: "work",
                table: "report_line_participants",
                column: "line_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_plan_item",
                schema: "work",
                table: "report_lines",
                column: "work_plan_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_report",
                schema: "work",
                table: "report_lines",
                columns: new[] { "report_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_section",
                schema: "work",
                table: "report_lines",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_unit",
                schema: "work",
                table: "report_lines",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_variant",
                schema: "work",
                table: "report_lines",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_lines_work_type",
                schema: "work",
                table: "report_lines",
                column: "work_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_site",
                schema: "work",
                table: "reports",
                columns: new[] { "construction_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ux_reports_key",
                schema: "work",
                table: "reports",
                columns: new[] { "employee_id", "construction_id", "date", "shift" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_changes",
                schema: "work");

            migrationBuilder.DropTable(
                name: "report_line_materials",
                schema: "work");

            migrationBuilder.DropTable(
                name: "report_line_participants",
                schema: "work");

            migrationBuilder.DropTable(
                name: "report_lines",
                schema: "work");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "work");

            migrationBuilder.DropSequence(
                name: "report_seq",
                schema: "work");
        }
    }
}
