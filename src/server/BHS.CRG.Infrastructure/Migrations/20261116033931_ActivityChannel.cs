using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActivityChannel : Migration
    {
        /// <summary>
        /// Канал записи журнала (issue #1336, design D5). Старые записи — по автору: без автора — <c>system</c>,
        /// с автором — <c>ui</c>. Это не догадка: до канала ни один путь MCP журнал не писал (сверено по коду),
        /// и то же правило читает копия старого формата (<c>ActivityChannel.ForLegacy</c>).
        ///
        /// ⚠️ Правка схемы и разовое заполнение, а не правка записей: запрет на изменение журнала держит
        /// <c>SaveChanges</c>, и миграция его не касается. Умолчание колонки снимается в конце нарочно —
        /// канал даёт контекст действия, и молчаливое «ui» от базы записало бы его мимо контекста.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "activity_log",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ui");

            migrationBuilder.Sql("UPDATE activity_log SET \"Channel\" = 'system' WHERE \"ActorId\" IS NULL;");
            migrationBuilder.Sql("ALTER TABLE activity_log ALTER COLUMN \"Channel\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "activity_log");
        }
    }
}
