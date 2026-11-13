using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Разовая чистка ссылок документов на шаблоны, которых у их типа нет (issue #1960).
    ///
    /// <para>Накопились они до #364 (24.07.2026): удаление шаблона не снимало ссылок. Жили дальше потому,
    /// что запись выбора принимала любые id, а клиент пересылал невидимые ему. Обе двери закрыты в том же
    /// PR; здесь — то, что успело войти. В копии рабочей базы от 11.11.2026 — 29 из 47 ссылок выбора у
    /// двух документов и столько же ключей параметров.</para>
    ///
    /// <para>Правило то же, что у <c>DomainObject.ForgetUnknownTemplates</c> (им пользуется
    /// восстановление копии), — совпадение сверяет тест. Ссылка — строка, которая без учёта регистра
    /// равна id шаблона ТИПА документа в виде «D»: шаблон чужого типа генерация тоже не видит; число,
    /// null, иной вид id — не ссылка. Одиночный пин не на ссылку — null; из выбора остаются ссылки
    /// (строчными, без повторов, в прежнем порядке); из переопределений — ключи-ссылки (строчными); не
    /// массив и не объект — null, пустое — null. Чего уборка не коснулась, не трогается. Статус и
    /// выпущенные PDF тоже: генерация такие ссылки и так пропускала, вывод от чистки не меняется. Числа
    /// документов — сообщением NOTICE (старт переносит его в журнал).</para>
    /// </summary>
    public partial class DocumentTemplateRefsCleanup : Migration
    {
        public const string CleanupSql = """
            DO $$
            DECLARE pins int; selections int; overrides int;
            BEGIN
              CREATE FUNCTION pg_temp.tpl_known(type_id uuid, v text) RETURNS boolean LANGUAGE sql STABLE AS
                $f$ SELECT v IS NOT NULL AND EXISTS (
                      SELECT 1 FROM templates WHERE "DocumentTypeId" = type_id AND "Id"::text = lower(v)) $f$;

              -- Одиночный пин.
              UPDATE document_facets f SET "TemplateId" = NULL
              FROM domain_objects o
              WHERE o."Id" = f."ObjectId" AND f."TemplateId" IS NOT NULL
                AND NOT pg_temp.tpl_known(o."CompositeTypeId", f."TemplateId"::text);
              GET DIAGNOSTICS pins = ROW_COUNT;

              -- Выбор для генерации. CASE — не ради красоты: OR в SQL не обязан вычисляться по порядку, и
              -- jsonb_array_elements от объекта упал бы ошибкой.
              UPDATE document_facets f SET "TemplateIds" = (
                SELECT jsonb_agg(to_jsonb(x.v) ORDER BY x.ord) FROM (
                  SELECT lower(e.j #>> '{}') AS v, min(e.ord) AS ord
                  FROM jsonb_array_elements(
                         CASE WHEN jsonb_typeof(f."TemplateIds") = 'array' THEN f."TemplateIds" ELSE '[]' END)
                       WITH ORDINALITY e(j, ord)
                  WHERE jsonb_typeof(e.j) = 'string' AND pg_temp.tpl_known(o."CompositeTypeId", e.j #>> '{}')
                  GROUP BY 1) x)
              FROM domain_objects o
              WHERE o."Id" = f."ObjectId" AND f."TemplateIds" IS NOT NULL
                AND (jsonb_typeof(f."TemplateIds") <> 'array'
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(
                               CASE WHEN jsonb_typeof(f."TemplateIds") = 'array' THEN f."TemplateIds" ELSE '[]' END) e(j)
                             WHERE NOT (jsonb_typeof(e.j) = 'string'
                                        AND pg_temp.tpl_known(o."CompositeTypeId", e.j #>> '{}'))));
              GET DIAGNOSTICS selections = ROW_COUNT;

              -- Переопределения параметров.
              UPDATE document_facets f SET "TemplateParams" = (
                SELECT jsonb_object_agg(lower(e.key), e.value)
                FROM jsonb_each(
                       CASE WHEN jsonb_typeof(f."TemplateParams") = 'object' THEN f."TemplateParams" ELSE '{}' END) e
                WHERE pg_temp.tpl_known(o."CompositeTypeId", e.key))
              FROM domain_objects o
              WHERE o."Id" = f."ObjectId" AND f."TemplateParams" IS NOT NULL
                AND (jsonb_typeof(f."TemplateParams") <> 'object'
                  OR EXISTS (SELECT 1 FROM jsonb_object_keys(
                               CASE WHEN jsonb_typeof(f."TemplateParams") = 'object' THEN f."TemplateParams" ELSE '{}' END) k
                             WHERE k <> lower(k) OR NOT pg_temp.tpl_known(o."CompositeTypeId", k)));
              GET DIAGNOSTICS overrides = ROW_COUNT;

              DROP FUNCTION pg_temp.tpl_known(uuid, text);
              RAISE NOTICE 'Ссылки документов на шаблоны вне их типа убраны: пин — у % документов, выбор — у %, параметры — у %',
                pins, selections, overrides;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CleanupSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Чистка не откатывается: ссылки вели на шаблоны, которых нет.
        }
    }
}
