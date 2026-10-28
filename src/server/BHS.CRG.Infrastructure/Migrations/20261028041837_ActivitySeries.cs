using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActivitySeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeriesHead",
                table: "activity_log",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "activity_log",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Прежние записи — одиночные ряды (ТЗ CORE-28.2): своя сессия, сами себе голова. Это шаг
            // схемы, а не правка свидетельства — новые колонки получают значение «одиночная запись»,
            // содержание записей не меняется. Свёртки задним числом нет нарочно.
            migrationBuilder.Sql("UPDATE activity_log SET \"SessionId\" = \"Id\", \"SeriesHead\" = true;");

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_series_heads",
                table: "activity_log",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0],
                filter: "\"SeriesHead\"");

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_SessionId",
                table: "activity_log",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_TargetId_OccurredAt_Id",
                table: "activity_log",
                columns: new[] { "TargetId", "OccurredAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activity_log_series_heads",
                table: "activity_log");

            migrationBuilder.DropIndex(
                name: "IX_activity_log_SessionId",
                table: "activity_log");

            migrationBuilder.DropIndex(
                name: "IX_activity_log_TargetId_OccurredAt_Id",
                table: "activity_log");

            migrationBuilder.DropColumn(
                name: "SeriesHead",
                table: "activity_log");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "activity_log");
        }
    }
}
