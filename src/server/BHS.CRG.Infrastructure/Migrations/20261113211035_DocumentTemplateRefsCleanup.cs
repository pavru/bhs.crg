﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Разовая чистка ссылок документов на шаблоны, которых нет (issue #1960).
    ///
    /// <para>Накопились они до #364 (24.07.2026): удаление шаблона не снимало ссылок. Жили дальше потому,
    /// что запись выбора принимала любые id, а клиент пересылал невидимые ему. Обе двери закрыты в том же
    /// PR; здесь — то, что успело войти. В копии рабочей базы от 11.11.2026 — 29 из 47 ссылок выбора у
    /// двух документов и столько же ключей параметров.</para>
    ///
    /// <para>Правило то же, что у <c>DomainObject.ForgetUnknownTemplates</c> (им пользуется
    /// восстановление копии): одиночный пин на несуществующий шаблон — null; из выбора убираются
    /// несуществующие (порядок сохраняется, повтор после уборки тоже); из переопределений — ключи не
    /// шаблонов, в том числе старый плоский вид <c>{имя:значение}</c>; не массив и не объект — null,
    /// пустое — null; чего удалённое не коснулось (повтор в чистом выборе), не трогается. Статус и выпущенные PDF не трогаются: генерация такие ссылки и так пропускала,
    /// вывод от чистки не меняется. Числа документов — сообщением NOTICE (старт переносит его в журнал).</para>
    /// </summary>
    public partial class DocumentTemplateRefsCleanup : Migration
    {
        public const string CleanupSql = """
            DO $$
            DECLARE pins int; selections int; overrides int;
            BEGIN
              CREATE TEMP TABLE known_templates ON COMMIT DROP AS SELECT "Id"::text AS id FROM templates;

              -- Одиночный пин.
              UPDATE document_facets SET "TemplateId" = NULL
              WHERE "TemplateId" IS NOT NULL AND "TemplateId"::text NOT IN (SELECT id FROM known_templates);
              GET DIAGNOSTICS pins = ROW_COUNT;

              -- Выбор для генерации.
              UPDATE document_facets f SET "TemplateIds" = (
                SELECT jsonb_agg(to_jsonb(x.v) ORDER BY x.ord) FROM (
                  SELECT e.v, min(e.ord) AS ord
                  FROM jsonb_array_elements_text(
                         CASE WHEN jsonb_typeof(f."TemplateIds") = 'array' THEN f."TemplateIds" END)
                       WITH ORDINALITY e(v, ord)
                  WHERE lower(e.v) IN (SELECT id FROM known_templates)
                  GROUP BY e.v) x)
              WHERE f."TemplateIds" IS NOT NULL AND (jsonb_typeof(f."TemplateIds") <> 'array'
                 OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(f."TemplateIds") e(v)
                            WHERE lower(e.v) NOT IN (SELECT id FROM known_templates)));
              GET DIAGNOSTICS selections = ROW_COUNT;

              -- Переопределения параметров.
              UPDATE document_facets f SET "TemplateParams" = (
                SELECT jsonb_object_agg(e.key, e.value)
                FROM jsonb_each(CASE WHEN jsonb_typeof(f."TemplateParams") = 'object' THEN f."TemplateParams" END) e
                WHERE lower(e.key) IN (SELECT id FROM known_templates))
              WHERE f."TemplateParams" IS NOT NULL AND (jsonb_typeof(f."TemplateParams") <> 'object'
                 OR EXISTS (SELECT 1 FROM jsonb_object_keys(f."TemplateParams") k
                            WHERE lower(k) NOT IN (SELECT id FROM known_templates)));
              GET DIAGNOSTICS overrides = ROW_COUNT;

              RAISE NOTICE 'Ссылки документов на несуществующие шаблоны убраны: пин — у % документов, выбор — у %, параметры — у %',
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
