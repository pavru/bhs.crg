using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SingleActiveJobPerTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала прибрать незавершённые задачи, иначе уникальный индекс не создастся и
            // приложение не стартует вовсе: на установке, остановленной посреди работы, в базе
            // вполне может лежать пара задач с одной целью.
            //
            // Это не самовольная правка данных: ровно то же делает старт приложения
            // (Job.MarkAbandoned) — очередь живёт в памяти процесса, и подхватить брошенную задачу
            // некому. Разница лишь в порядке: миграции применяются РАНЬШЕ той уборки, и к моменту
            // создания индекса конфликты ещё на месте. Текст причины взят у неё дословно.
            migrationBuilder.Sql("""
                UPDATE jobs
                   SET "Status" = 'Failed',
                       "Error" = 'Задача прервана перезапуском сервера — запустите операцию заново.',
                       "FinishedAt" = now(),
                       "UpdatedAt" = now()
                 WHERE "Status" IN ('Queued', 'Running')
                """);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_single_active_per_target",
                table: "jobs",
                columns: new[] { "TargetId", "Kind" },
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running') AND \"Kind\" <> 'SendEmail'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_single_active_per_target",
                table: "jobs");
        }
    }
}
