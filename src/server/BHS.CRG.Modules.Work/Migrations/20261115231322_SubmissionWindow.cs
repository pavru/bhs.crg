using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class SubmissionWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "required_missing",
                schema: "work",
                table: "reports",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_by",
                schema: "work",
                table: "reports",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            // Конец окна прежних отчётов (ТЗ WORK-52, design work-15): других мгновений у них нет, поэтому окно считается
            // здесь по нынешнему поясу — стройки, иначе компании, иначе Москвы. Пояс, которого PostgreSQL не знает (имя
            // Windows), пропускается. Ядро мигрирует раньше модулей: таблицы constructions и app_settings уже есть.
            // Сначала колонка без ограничения, затем окно, затем «не пусто» — иначе пустые окна ушли бы в автоподачу.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "window_ends_at",
                schema: "work",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.constructions') IS NOT NULL AND to_regclass('public.app_settings') IS NOT NULL THEN
                        UPDATE work.reports r
                        SET window_ends_at =
                            ((r.date + CASE WHEN r.shift = 'night' THEN 2 ELSE 1 END)::timestamp) AT TIME ZONE COALESCE(
                                (SELECT c."TimeZoneId" FROM public.constructions c
                                 WHERE c."Id" = r.construction_id
                                   AND c."TimeZoneId" IN (SELECT name FROM pg_timezone_names)),
                                (SELECT s."Value" FROM public.app_settings s
                                 WHERE s."Key" = 'company.timezone'
                                   AND s."Value" IN (SELECT name FROM pg_timezone_names)),
                                'Europe/Moscow')
                        WHERE r.window_ends_at IS NULL;
                    ELSE
                        -- База без таблиц ядра (схема модуля отдельно): пояса взять неоткуда.
                        UPDATE work.reports r
                        SET window_ends_at =
                            ((r.date + CASE WHEN r.shift = 'night' THEN 2 ELSE 1 END)::timestamp) AT TIME ZONE 'Europe/Moscow'
                        WHERE r.window_ends_at IS NULL;
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "window_ends_at",
                schema: "work",
                table: "reports",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_window",
                schema: "work",
                table: "reports",
                columns: new[] { "status", "window_ends_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reports_window",
                schema: "work",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "required_missing",
                schema: "work",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "submitted_by",
                schema: "work",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "window_ends_at",
                schema: "work",
                table: "reports");
        }
    }
}
