using System;
using System.Collections.Generic;
using System.Linq;
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
            // здесь по нынешнему поясу — стройки, иначе компании, иначе Москвы. Имя Windows переводится в IANA тем же
            // преобразованием .NET, что у порта строек (SiteZones: TryFindSystemTimeZoneById, затем IanaId), — иначе окно
            // прежнего отчёта считалось бы по поясу компании, а нового по поясу стройки. Пояс, которого не знает и после
            // перевода PostgreSQL, пропускается. Ядро мигрирует раньше модулей: таблицы constructions и app_settings уже есть.
            // Сначала колонка без ограничения, затем окно, затем «не пусто» — иначе пустые окна ушли бы в автоподачу.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "window_ends_at",
                schema: "work",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF to_regclass('public.constructions') IS NOT NULL AND to_regclass('public.app_settings') IS NOT NULL THEN
                        WITH map(win, iana) AS ({{WindowsZones()}}),
                        site AS (
                            SELECT c."Id" AS id, COALESCE(m.iana, c."TimeZoneId") AS zone
                            FROM public.constructions c LEFT JOIN map m ON m.win = c."TimeZoneId"),
                        company AS (
                            SELECT COALESCE(m.iana, s."Value") AS zone
                            FROM public.app_settings s LEFT JOIN map m ON m.win = s."Value"
                            WHERE s."Key" = 'company.timezone')
                        UPDATE work.reports r
                        SET window_ends_at =
                            ((r.date + CASE WHEN r.shift = 'night' THEN 2 ELSE 1 END)::timestamp) AT TIME ZONE COALESCE(
                                (SELECT site.zone FROM site
                                 WHERE site.id = r.construction_id AND site.zone IN (SELECT name FROM pg_timezone_names)),
                                (SELECT company.zone FROM company
                                 WHERE company.zone IN (SELECT name FROM pg_timezone_names)),
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

        /// <summary>
        /// Пары «имя Windows → IANA» для SQL — тем же преобразованием, что у порта строек: каждое имя Windows, которое
        /// знает система (сама или через перевод IANA), переводится <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, out string)" />.
        /// Без ICU пар нет — и порт строек тогда тоже не узнал бы имя Windows, оба уходят к поясу компании.
        /// </summary>
        private static string WindowsZones()
        {
            var windows = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
            {
                if (!zone.HasIanaId) windows.Add(zone.Id);
                else if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var win)) windows.Add(win);
            }
            var pairs = windows
                .Select(w => TimeZoneInfo.TryConvertWindowsIdToIanaId(w, out var iana) ? $"('{Quote(w)}', '{Quote(iana)}')" : null)
                .Where(p => p is not null)
                .ToList();
            return "SELECT NULL::text, NULL::text WHERE false" +
                   (pairs.Count > 0 ? " UNION ALL VALUES " + string.Join(", ", pairs) : "");
        }

        private static string Quote(string value) => value.Replace("'", "''");

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
