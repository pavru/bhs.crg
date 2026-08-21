using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DataSetStaleReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Порядок обязателен: сначала колонка, потом перенос, и только затем снос старых.
            // Скаффолдер расставил наоборот — с ним признак устаревания молча обнулился бы у всех.
            migrationBuilder.AddColumn<string>(
                name: "StaleReason",
                table: "dataset_sources",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // 1. Прежний булев признак источника — причина известна: его выставляла только замена файла.
            migrationBuilder.Sql(
                """
                UPDATE dataset_sources SET "StaleReason" = 'FileReplaced' WHERE "RecognitionStale";
                """);

            // 2. Устаревание, которое до сих пор жило ТОЛЬКО в группировке (GostGroupingGroup.TableStale)
            // и было видно одному лишь снимку MCP. Переносим на источник — иначе, сняв третью ветвь
            // IsStale, мы потеряли бы признак у тех, у кого он сейчас единственный. Legacy-формат
            // группировки ({Documents:[…]}) сюда не попадает: табличных проекций у него нет.
            migrationBuilder.Sql(
                """
                UPDATE dataset_sources s SET "StaleReason" = 'TableBoundariesChanged'
                FROM dataset_files f,
                     LATERAL jsonb_array_elements(f."Grouping" -> 'Groups') g
                WHERE s."FileId" = f."Id"
                  AND s."StaleReason" IS NULL
                  AND jsonb_typeof(f."Grouping" -> 'Groups') = 'array'
                  AND (g ->> 'TableStale')::boolean IS TRUE
                  AND s."SheetOrPath" = 'gost-table:' || (g ->> 'Id');
                """);

            migrationBuilder.DropColumn(
                name: "RecognitionStale",
                table: "dataset_sources");

            // На файле колонка сносится без переноса — и это не потеря: её не выставлял никто,
            // единственный писатель жил в тесте, зато читателей было двое (issue #815).
            migrationBuilder.DropColumn(
                name: "RecognitionStale",
                table: "dataset_files");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RecognitionStale",
                table: "dataset_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RecognitionStale",
                table: "dataset_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Обратно причина схлопывается в «да» — четыре значения в булев не помещаются. Это
            // ожидаемая потеря отката, а не забытая ветка.
            migrationBuilder.Sql(
                """
                UPDATE dataset_sources SET "RecognitionStale" = TRUE WHERE "StaleReason" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "StaleReason",
                table: "dataset_sources");
        }
    }
}
