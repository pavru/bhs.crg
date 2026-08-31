using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Одна активная задача на цель — правилом базы, а не только проверкой перед постановкой
    /// (issue #900).
    ///
    /// Индекс написан сырым SQL, а не объявлен в конфигурации EF, потому что его ключ — ВЫРАЖЕНИЕ:
    /// виды распознавания сведены в одно семейство. Иначе защита оказалась бы уже той, которую она
    /// подпирает: <c>OperationLauncher</c> отвергает запуск, если по набору идёт ЛЮБОЕ из трёх
    /// распознаваний, а ключ (цель, вид) пропустил бы одновременные RecognizeGostSet и
    /// RecognizeTable — и они переписали бы один и тот же набор, то есть ровно та двойная запись,
    /// ради которой всё и делается.
    /// </summary>
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

            // Ключ — цель и СЕМЕЙСТВО операции. Все виды, начинающиеся на Recognize, пишут один и тот
            // же набор (группировку, распознанные таблицы), поэтому одновременными быть не должны;
            // остальные виды сами себе семейство. Префикс, а не перечень: новый вид распознавания
            // получает защиту, ничего не меняя здесь, — а перечень пришлось бы дополнять, и забытое
            // дополнение молчало бы.
            //
            // Исключение одно и названо: отправка комплекта почтой. Её дубль законен — тот же
            // комплект отправляют разным получателям двумя действиями подряд.
            //
            // Kind и Status хранятся строками (HasConversion в JobConfiguration), отсюда строковые
            // сравнения и приведение обеих ветвей CASE к text.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ix_jobs_single_active_per_target
                    ON jobs ("TargetId",
                             (CASE WHEN "Kind" LIKE 'Recognize%' THEN 'Recognize' ELSE "Kind"::text END))
                 WHERE "Status" IN ('Queued', 'Running')
                   AND "Kind" <> 'SendEmail'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DROP INDEX IF EXISTS ix_jobs_single_active_per_target");
    }
}
