using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Роли справочников и тэги их полей — действующим установкам (ТЗ TYPE-23.1, CORE-20.4, issue #1101).
    ///
    /// <para><b>Зачем.</b> Код перестал находить справочники по именам («Организация», «ИНН»,
    /// «Наименование», «Номенклатура») и находит их по тэгам. У заказчика эти типы есть, а тэгов на
    /// них нет: без переноса обновление остановило бы сопоставление поставщика по ИНН и выбор позиции
    /// в строке — у каждого, и без единой ошибки при обновлении.</para>
    ///
    /// <para><b>Находит то же, что находил прежний код</b>: тип — по коду без учёта регистра, поле —
    /// у носителя роли и выше по цепочке родителей (ревью PR #1317). Иначе установка, где «ИНН»
    /// объявлен у родителя «Организации», обновилась бы зелёной и перестала сопоставлять
    /// поставщика.</para>
    ///
    /// <para><b>Правило осторожное — ставит только туда, где свободно.</b> Роль, которую уже несёт
    /// любой тип, не трогается. Тэг поля, который уже стоит на каком-то поле типа, — тоже. Поле не
    /// того вида тэга не получает. Установка без типа «Организация» остаётся без роли и говорит об
    /// этом словами — так и должно быть.</para>
    ///
    /// <para>⚠️ <b>То же правило — в коде</b>: <c>DirectoryRoleDefaults</c>, им пользуется
    /// восстановление копии, снятой до этого переноса. Здесь SQL, там C#, и расходиться им нельзя:
    /// сверяет <c>DirectoryRoleDefaultsTests</c> на одних и тех же схемах. Правите одно — правьте
    /// второе.</para>
    ///
    /// <para>Один раз, а не при каждом старте: снять роль — решение администратора, и возврат при
    /// запуске отменял бы его молча.</para>
    /// </summary>
    public partial class DirectoryRoles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    rule   record;
                    bearer uuid;
                    chain  uuid[];
                    cur    uuid;
                    holder  uuid;
                BEGIN
                    -- Код типа сравнивается БЕЗ РЕГИСТРА, как искал прежний код: тип «организация»
                    -- он находил, и перенос обязан найти его тоже. Правило сравнения названо явно —
                    -- встроенное, от локали базы не зависит: с локалью «C» обычный lower() кириллицу
                    -- не трогает вовсе.

                    -- ── 1. Роль — типу с прежним кодом, если её не несёт никто ────────────
                    FOR rule IN
                        SELECT * FROM (VALUES
                            ('Организация',  'role.organization'),
                            ('Номенклатура', 'role.nomenclature')) AS r(code, role)
                    LOOP
                        IF NOT EXISTS (
                            SELECT 1 FROM document_types
                             WHERE jsonb_typeof("Schema"->'tags') = 'array' AND "Schema"->'tags' ? rule.role)
                        THEN
                            UPDATE document_types
                               SET "Schema" = jsonb_set(
                                       "Schema", '{tags}',
                                       CASE WHEN jsonb_typeof("Schema"->'tags') = 'array'
                                            THEN "Schema"->'tags' ELSE '[]'::jsonb END
                                       || to_jsonb(rule.role)),
                                   "UpdatedAt" = now()
                             WHERE lower("Code" COLLATE "pg_c_utf8") = lower(rule.code COLLATE "pg_c_utf8")
                               AND "Kind" = 'Composite'
                               AND jsonb_typeof("Schema") = 'object';
                        END IF;
                    END LOOP;

                    -- ── 2. Тэги полей — у носителя роли, и только на свободное место ──────
                    --
                    -- Виды поля сверены с живой базой: «ИНН» там поле примитивного типа, а
                    -- «Наименование» — составное. Список видов совпадает с AppliesTo тэга в реестре.
                    FOR rule IN
                        SELECT * FROM (VALUES
                            ('Организация', 'role.organization', 'ИНН',          'org.inn',
                                ARRAY['string', 'text', 'number', 'primitive']),
                            ('Организация', 'role.organization', 'Наименование', 'org.name',
                                ARRAY['string', 'text', 'complex'])) AS r(code, role, key, tag, kinds)
                    LOOP
                        SELECT "Id" INTO bearer FROM document_types
                         WHERE lower("Code" COLLATE "pg_c_utf8") = lower(rule.code COLLATE "pg_c_utf8")
                           AND "Kind" = 'Composite'
                           AND jsonb_typeof("Schema"->'tags') = 'array' AND "Schema"->'tags' ? rule.role
                         LIMIT 1;
                        CONTINUE WHEN bearer IS NULL;

                        -- Носитель и его предки, ближайший первым: прежний код находил поле по
                        -- схеме С НАСЛЕДОВАНИЕМ, и «ИНН», объявленный у родителя, обязан найтись тоже.
                        -- Цикл в цепочке родителей базе ничем не запрещён — обход помнит пройденное.
                        chain := ARRAY[]::uuid[];
                        cur := bearer;
                        WHILE cur IS NOT NULL AND NOT (cur = ANY (chain)) LOOP
                            chain := chain || cur;
                            SELECT "ParentId" INTO cur FROM document_types WHERE "Id" = cur;
                        END LOOP;

                        -- Тэг уже стоит на каком-то поле цепочки — это решение человека.
                        CONTINUE WHEN EXISTS (
                            SELECT 1
                              FROM document_types t,
                                   jsonb_array_elements(
                                       CASE WHEN jsonb_typeof(t."Schema"->'fields') = 'array'
                                            THEN t."Schema"->'fields' ELSE '[]'::jsonb END) AS g
                             WHERE t."Id" = ANY (chain)
                               AND jsonb_typeof(g) = 'object'
                               AND jsonb_typeof(g->'tags') = 'array' AND g->'tags' ? rule.tag);

                        -- Ближайшее объявление поля с прежним ключом.
                        holder := NULL;
                        FOREACH cur IN ARRAY chain LOOP
                            IF EXISTS (
                                SELECT 1
                                  FROM document_types t,
                                       jsonb_array_elements(
                                           CASE WHEN jsonb_typeof(t."Schema"->'fields') = 'array'
                                                THEN t."Schema"->'fields' ELSE '[]'::jsonb END) AS g
                                 WHERE t."Id" = cur AND jsonb_typeof(g) = 'object' AND g->>'key' = rule.key)
                            THEN
                                holder := cur;
                                EXIT;
                            END IF;
                        END LOOP;
                        CONTINUE WHEN holder IS NULL;

                        UPDATE document_types t
                           SET "Schema" = jsonb_set(t."Schema", '{fields}', (
                                   SELECT jsonb_agg(
                                              CASE WHEN jsonb_typeof(f) = 'object'
                                                        AND f->>'key' = rule.key
                                                        AND ord = (
                                                            -- Ключ в схеме один, но страхуемся от двух:
                                                            -- тэг получает первое поле с этим ключом.
                                                            SELECT min(o) FROM jsonb_array_elements(t."Schema"->'fields')
                                                                   WITH ORDINALITY AS b(g, o)
                                                             WHERE jsonb_typeof(g) = 'object' AND g->>'key' = rule.key)
                                                   THEN jsonb_set(f, '{tags}',
                                                            CASE WHEN jsonb_typeof(f->'tags') = 'array'
                                                                 THEN f->'tags' ELSE '[]'::jsonb END
                                                            || to_jsonb(rule.tag))
                                                   ELSE f END
                                              ORDER BY ord)
                                     FROM jsonb_array_elements(t."Schema"->'fields') WITH ORDINALITY AS a(f, ord))),
                               "UpdatedAt" = now()
                         WHERE t."Id" = holder
                           -- первое поле с нужным ключом — подходящего вида; иначе тэг не ставится
                           -- никому: выше по цепочке это поле уже заслонено.
                           AND EXISTS (
                               SELECT 1 FROM jsonb_array_elements(t."Schema"->'fields') WITH ORDINALITY AS c(g, o)
                                WHERE jsonb_typeof(g) = 'object' AND g->>'key' = rule.key
                                  AND g->>'type' = ANY (rule.kinds)
                                  AND o = (SELECT min(o2) FROM jsonb_array_elements(t."Schema"->'fields')
                                                  WITH ORDINALITY AS d(g2, o2)
                                            WHERE jsonb_typeof(g2) = 'object' AND g2->>'key' = rule.key));
                    END LOOP;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Назад тэги не снимаем, и это не забывчивость: поставленное переносом неотличимо от
            // поставленного администратором, а снять чужое — остановить работающее сопоставление.
            // Прежний код тэгов не читает вовсе, поэтому оставшиеся ему не мешают. Полноценная дорога
            // назад — прежний образ плюс резервная копия (решение по G1).
        }
    }
}
