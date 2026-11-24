using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Schedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedules",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    creation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    distribution_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    unmapped = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedules", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedules_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "schedule_works",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_row_id = table.Column<Guid>(type: "uuid", nullable: true),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_works", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedule_works_estimate_rows_source_row_id",
                        column: x => x.source_row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_schedule_works_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalSchema: "plan",
                        principalTable: "schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedule_work_assignees",
                schema: "plan",
                columns: table => new
                {
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_work_assignees", x => new { x.work_id, x.employee_id });
                    table.ForeignKey(
                        name: "FK_schedule_work_assignees_schedule_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "plan",
                        principalTable: "schedule_works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_work_assignees_employee_id",
                schema: "plan",
                table: "schedule_work_assignees",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_works_plan_item_id",
                schema: "plan",
                table: "schedule_works",
                column: "plan_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_works_schedule_id_source_row_id_section_id",
                schema: "plan",
                table: "schedule_works",
                columns: new[] { "schedule_id", "source_row_id", "section_id" },
                unique: true,
                filter: "source_row_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_works_section_id",
                schema: "plan",
                table: "schedule_works",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_works_source_row_id",
                schema: "plan",
                table: "schedule_works",
                column: "source_row_id");

            migrationBuilder.CreateIndex(
                name: "IX_schedules_construction_id_creation_key",
                schema: "plan",
                table: "schedules",
                columns: new[] { "construction_id", "creation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedules_revision_id",
                schema: "plan",
                table: "schedules",
                column: "revision_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_work_assignees",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "schedule_works",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "schedules",
                schema: "plan");
        }
    }
}
