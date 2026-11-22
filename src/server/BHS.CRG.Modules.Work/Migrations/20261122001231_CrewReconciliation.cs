using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class CrewReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "crew_role",
                schema: "work",
                table: "report_lines",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.Sql("UPDATE work.report_lines SET crew_role = 'lead' WHERE is_crew;");

            migrationBuilder.CreateTable(
                name: "crew_groups",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    shift = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_revision = table.Column<Guid>(type: "uuid", nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crew_groups", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_report_lines_crew_role",
                schema: "work",
                table: "report_lines",
                sql: "(is_crew AND crew_role IS NOT NULL AND crew_role IN ('lead', 'member')) OR (NOT is_crew AND crew_role IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_crew_groups_construction_id_date_shift_work_type_id",
                schema: "work",
                table: "crew_groups",
                columns: new[] { "construction_id", "date", "shift", "work_type_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crew_groups_unit_id",
                schema: "work",
                table: "crew_groups",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_crew_groups_work_type_id",
                schema: "work",
                table: "crew_groups",
                column: "work_type_id");

            migrationBuilder.Sql("""
                INSERT INTO work.crew_groups (id, construction_id, date, shift, work_type_id, input_revision)
                SELECT gen_random_uuid(), existing.construction_id, existing.date, existing.shift,
                       existing.work_type_id, gen_random_uuid()
                FROM (
                    SELECT DISTINCT r.construction_id, r.date, r.shift, line.work_type_id
                    FROM work.reports r
                    JOIN work.report_lines line ON line.report_id = r.id
                    WHERE line.is_crew AND r.status IN ('submitted', 'accepted')
                ) existing;
                """);

            // Старые копии восстанавливаются после применения миграций и не содержат crew_role и crew_groups.
            // Модульный импорт вставляет отчёт раньше его строк, а обычный путь приложения всегда задаёт роль.
            migrationBuilder.Sql("""
                CREATE FUNCTION work.restore_legacy_crew_line() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.crew_role := CASE WHEN NEW.is_crew THEN 'lead' END;
                    INSERT INTO work.crew_groups (id, construction_id, date, shift, work_type_id, input_revision)
                    SELECT gen_random_uuid(), affected.construction_id, affected.date, affected.shift,
                           affected.work_type_id, gen_random_uuid()
                    FROM (
                        SELECT DISTINCT r.construction_id, r.date, r.shift, line.work_type_id
                        FROM (
                            SELECT NEW.report_id AS report_id, NEW.work_type_id AS work_type_id WHERE NEW.is_crew
                            UNION
                            SELECT old_line.report_id, old_line.work_type_id
                            FROM work.report_lines old_line WHERE old_line.id = NEW.id AND old_line.is_crew
                        ) line
                        JOIN work.reports r ON r.id = line.report_id
                        WHERE r.status IN ('submitted', 'accepted')
                    ) affected
                    ON CONFLICT (construction_id, date, shift, work_type_id)
                    DO UPDATE SET input_revision = EXCLUDED.input_revision;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER restore_legacy_crew_line
                    BEFORE INSERT OR UPDATE ON work.report_lines
                    FOR EACH ROW WHEN (NEW.is_crew <> (NEW.crew_role IS NOT NULL))
                    EXECUTE FUNCTION work.restore_legacy_crew_line();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER restore_legacy_crew_line ON work.report_lines;
                DROP FUNCTION work.restore_legacy_crew_line();
                """);

            migrationBuilder.DropTable(
                name: "crew_groups",
                schema: "work");

            migrationBuilder.DropCheckConstraint(
                name: "ck_report_lines_crew_role",
                schema: "work",
                table: "report_lines");

            migrationBuilder.DropColumn(
                name: "crew_role",
                schema: "work",
                table: "report_lines");
        }
    }
}
