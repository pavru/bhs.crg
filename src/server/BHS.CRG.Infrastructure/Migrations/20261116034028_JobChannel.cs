using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class JobChannel : Migration
    {
        /// <summary>
        /// Канал задачи (issue #1336, design D3): у старых — по заказчику, тем же правилом, что у записей
        /// журнала (<c>Job.Create</c>): без заказчика — <c>system</c>, иначе <c>ui</c>. Задачи, оставшиеся в
        /// очереди, при старте всё равно снимаются как брошенные. Умолчание снимается: канал задаче даёт служба
        /// постановки.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "jobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ui");

            migrationBuilder.Sql("UPDATE jobs SET \"Channel\" = 'system' WHERE \"UserId\" = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("ALTER TABLE jobs ALTER COLUMN \"Channel\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "jobs");
        }
    }
}
