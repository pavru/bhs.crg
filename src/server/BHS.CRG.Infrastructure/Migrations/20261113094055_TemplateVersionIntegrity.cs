using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Целостность версий шаблона (id-template-version-integrity, D3, D8): разовая починка накопленных
    /// дублей, затем два уникальных индекса. Без починки индекс не создался бы на базе, где дубли уже
    /// есть (#1920, #1924), и старт упал бы.
    /// </summary>
    public partial class TemplateVersionIntegrity : Migration
    {
        /// <summary>
        /// Починка — то же правило, что <c>TemplateVersionRepair</c> в домене (им пользуется
        /// восстановление копии) и <c>DomainObject.RepinTemplates(…, selectionOnly: true)</c>. Совпадение
        /// сторожит тест: одни и те же дубли проходят через оба, итог сравнивается. Правила (решение
        /// владельца 13.11.2026, задача 0.4):
        /// <list type="number">
        /// <item>лишние действующие гаснут: остаётся наибольший номер, при равенстве — созданная позже;
        /// «по умолчанию» любой версии шаблона переходит к ней;</item>
        /// <item>повторные номера: кроме самой ранней по CreatedAt, получают max+1, max+2… в порядке
        /// появления;</item>
        /// <item>выбор погашенной версии в TemplateIds и её переопределения в TemplateParams — к
        /// действующей того же шаблона, повтор в наборе убирается. Одиночный TemplateId, статус и PDF
        /// не трогаются: удалить блобы SQL не может, а пересадка молча сменила бы PDF.</item>
        /// </list>
        /// Порядок uuid — по тексту (COLLATE "C"), как сравнивает домен. Числа — сообщением NOTICE:
        /// старт приложения переносит его в журнал.
        /// </summary>
        public const string RepairSql = """
            DO $$
            DECLARE retired int; renumbered int; repinned int;
            BEGIN
              SELECT coalesce(sum(n - 1), 0) INTO retired FROM (
                SELECT count(*) AS n FROM templates WHERE "IsActive"
                GROUP BY "DocumentTypeId", "Name" HAVING count(*) > 1) x;

              -- 1. Лишние действующие.
              WITH grp AS (
                SELECT "DocumentTypeId", "Name", bool_or("IsDefault") AS any_default
                FROM templates GROUP BY "DocumentTypeId", "Name"
                HAVING count(*) FILTER (WHERE "IsActive") > 1
              ), survivor AS (
                SELECT DISTINCT ON (t."DocumentTypeId", t."Name") t."DocumentTypeId", t."Name", t."Id", g.any_default
                FROM templates t JOIN grp g ON g."DocumentTypeId" = t."DocumentTypeId" AND g."Name" = t."Name"
                WHERE t."IsActive"
                ORDER BY t."DocumentTypeId", t."Name", t."Version" DESC, t."CreatedAt" DESC, t."Id"::text COLLATE "C" DESC
              )
              UPDATE templates t
              SET "IsActive" = (t."Id" = s."Id"),
                  "IsDefault" = (t."Id" = s."Id" AND s.any_default),
                  "UpdatedAt" = now()
              FROM survivor s
              WHERE t."DocumentTypeId" = s."DocumentTypeId" AND t."Name" = s."Name"
                AND (t."IsActive" OR t."IsDefault")
                AND NOT (t."Id" = s."Id" AND t."IsDefault" = s.any_default);

              -- 2. Повторные номера.
              WITH ranked AS (
                SELECT "Id", "DocumentTypeId", "Name", "CreatedAt",
                  row_number() OVER (PARTITION BY "DocumentTypeId", "Name", "Version"
                                     ORDER BY "CreatedAt", "Id"::text COLLATE "C") AS nth,
                  max("Version") OVER (PARTITION BY "DocumentTypeId", "Name") AS top
                FROM templates
              ), late AS (
                SELECT "Id", top + row_number() OVER (PARTITION BY "DocumentTypeId", "Name"
                                                      ORDER BY "CreatedAt", "Id"::text COLLATE "C") AS version
                FROM ranked WHERE nth > 1
              )
              UPDATE templates t SET "Version" = late.version, "UpdatedAt" = now()
              FROM late WHERE t."Id" = late."Id";
              GET DIAGNOSTICS renumbered = ROW_COUNT;

              -- 3. Выбор погашенных версий — к действующей.
              WITH map AS (
                SELECT r."Id"::text AS old_id, a."Id"::text AS new_id
                FROM templates r
                JOIN templates a ON a."DocumentTypeId" = r."DocumentTypeId" AND a."Name" = r."Name" AND a."IsActive"
                WHERE NOT r."IsActive"
              ), pinned AS (
                SELECT f."ObjectId", f."TemplateIds", f."TemplateParams"
                FROM document_facets f
                WHERE jsonb_typeof(f."TemplateIds") = 'array'
                  AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(f."TemplateIds") e(v)
                              JOIN map m ON m.old_id = lower(e.v))
              ), ids AS (
                SELECT p."ObjectId", jsonb_agg(to_jsonb(x.id) ORDER BY x.ord) AS ids
                FROM pinned p CROSS JOIN LATERAL (
                  SELECT coalesce(m.new_id, e.v) AS id, min(e.ord) AS ord
                  FROM jsonb_array_elements_text(p."TemplateIds") WITH ORDINALITY e(v, ord)
                  LEFT JOIN map m ON m.old_id = lower(e.v)
                  GROUP BY coalesce(m.new_id, e.v)
                ) x
                GROUP BY p."ObjectId"
              ), params AS (
                SELECT p."ObjectId",
                       jsonb_object_agg(coalesce(m.new_id, e.key), e.value
                                        ORDER BY (m.new_id IS NULL), e.key COLLATE "C") AS params
                FROM pinned p
                CROSS JOIN LATERAL jsonb_each(p."TemplateParams") e
                LEFT JOIN map m ON m.old_id = lower(e.key)
                  AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(p."TemplateIds") s(v) WHERE lower(s.v) = m.old_id)
                WHERE jsonb_typeof(p."TemplateParams") = 'object'
                GROUP BY p."ObjectId"
                HAVING bool_or(m.new_id IS NOT NULL)
              )
              UPDATE document_facets f
              SET "TemplateIds" = ids.ids, "TemplateParams" = coalesce(params.params, f."TemplateParams")
              FROM ids LEFT JOIN params ON params."ObjectId" = ids."ObjectId"
              WHERE f."ObjectId" = ids."ObjectId";
              GET DIAGNOSTICS repinned = ROW_COUNT;

              RAISE NOTICE 'Версии шаблонов: лишних действующих погашено %, повторных номеров перенумеровано %, документов с выбором погашенной версии пересажено %',
                retired, renumbered, repinned;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала данные, потом индексы: на непочиненной базе индекс не создастся.
            migrationBuilder.Sql(RepairSql);

            migrationBuilder.CreateIndex(
                name: "UX_templates_one_active",
                table: "templates",
                columns: new[] { "DocumentTypeId", "Name" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "UX_templates_version",
                table: "templates",
                columns: new[] { "DocumentTypeId", "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Починка данных не откатывается: погашенные лишние версии и новые номера — верное состояние.
            migrationBuilder.DropIndex(
                name: "UX_templates_one_active",
                table: "templates");

            migrationBuilder.DropIndex(
                name: "UX_templates_version",
                table: "templates");
        }
    }
}
