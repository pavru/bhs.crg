using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Тэг сокращённого названия организации — действующим установкам (ТЗ CORE-20.4, issue #1473).
    ///
    /// <para><b>Зачем.</b> Модули называют организацию сокращённым названием, если оно заполнено, а
    /// находят его по тэгу <c>org.shortName</c>. У заказчика сокращённое лежит частью «Сокращённое»
    /// составного «Наименования» (сверено на копии базы 11.11.2026), и тэга на ней нет: без переноса
    /// обновление ничего бы не показало, пока администратор не узнает о тэге.</para>
    ///
    /// <para><b>Правило осторожное — ставит только туда, где свободно.</b> Носитель роли
    /// <c>role.organization</c> ровно один и составной; поле с <c>org.name</c> — ближайшее у носителя
    /// или выше по родителям, составного вида; его составной тип — часть с ключом «Сокращённое»
    /// строкового вида получает тэг. Тэг уже стоит на любом поле справочника или любой части — ничего
    /// не делается: это решение человека.</para>
    ///
    /// <para>⚠️ <b>То же правило — в коде</b>: <c>DirectoryRoleDefaults.ApplyParts</c>, им пользуется
    /// восстановление копии, снятой до этого переноса. Расходиться им нельзя: сверяет
    /// <c>OrganizationShortNameTests</c> на одних и тех же схемах. Правите одно — правьте второе.</para>
    ///
    /// <para>Один раз, а не при каждом старте: снять тэг — решение администратора, и возврат при
    /// запуске отменял бы его молча (тот же довод, что у <c>DirectoryRoles</c>).</para>
    /// </summary>
    public partial class OrgShortNameTag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    bearer    uuid;
                    chain     uuid[];
                    parts     uuid[];
                    cur       uuid;
                    whole     jsonb;
                    part_type uuid;
                BEGIN
                    -- ── 1. Носитель роли: ровно один, составной ────────────────────────────
                    SELECT t."Id" INTO bearer FROM document_types t
                     WHERE jsonb_typeof(t."Schema"->'tags') = 'array' AND t."Schema"->'tags' ? 'role.organization'
                       AND t."Kind" = 'Composite'
                       AND (SELECT count(*) FROM document_types b
                             WHERE jsonb_typeof(b."Schema"->'tags') = 'array'
                               AND b."Schema"->'tags' ? 'role.organization') = 1;
                    IF bearer IS NULL THEN RETURN; END IF;

                    -- Носитель и его предки, ближайший первым; цикл в цепочке обход помнит.
                    chain := ARRAY[]::uuid[];
                    cur := bearer;
                    WHILE cur IS NOT NULL AND NOT (cur = ANY (chain)) LOOP
                        chain := chain || cur;
                        SELECT "ParentId" INTO cur FROM document_types WHERE "Id" = cur;
                    END LOOP;

                    -- ── 2. Поле названия: ближайшее с org.name, составное ────────────────────
                    whole := NULL;
                    FOREACH cur IN ARRAY chain LOOP
                        SELECT g INTO whole
                          FROM document_types t,
                               jsonb_array_elements(
                                   CASE WHEN jsonb_typeof(t."Schema"->'fields') = 'array'
                                        THEN t."Schema"->'fields' ELSE '[]'::jsonb END) WITH ORDINALITY AS a(g, o)
                         WHERE t."Id" = cur AND jsonb_typeof(g) = 'object'
                           AND jsonb_typeof(g->'tags') = 'array' AND g->'tags' ? 'org.name'
                         ORDER BY o LIMIT 1;
                        EXIT WHEN whole IS NOT NULL;
                    END LOOP;
                    IF whole IS NULL OR whole->>'type' IS DISTINCT FROM 'complex' THEN RETURN; END IF;

                    SELECT "Id" INTO part_type FROM document_types
                     WHERE "Id"::text = lower(whole->>'typeId') AND "Kind" = 'Composite';
                    IF part_type IS NULL THEN RETURN; END IF;

                    parts := ARRAY[]::uuid[];
                    cur := part_type;
                    WHILE cur IS NOT NULL AND NOT (cur = ANY (parts)) LOOP
                        parts := parts || cur;
                        SELECT "ParentId" INTO cur FROM document_types WHERE "Id" = cur;
                    END LOOP;

                    -- ── 3. Тэг уже стоит — у справочника или у частей: решение человека ──────
                    IF EXISTS (
                        SELECT 1
                          FROM document_types t,
                               jsonb_array_elements(
                                   CASE WHEN jsonb_typeof(t."Schema"->'fields') = 'array'
                                        THEN t."Schema"->'fields' ELSE '[]'::jsonb END) AS g
                         WHERE t."Id" = ANY (chain || parts)
                           AND jsonb_typeof(g) = 'object'
                           AND jsonb_typeof(g->'tags') = 'array' AND g->'tags' ? 'org.shortName')
                    THEN RETURN; END IF;

                    -- ── 4. Первая собственная часть «Сокращённое» — строкового вида ──────────
                    UPDATE document_types t
                       SET "Schema" = jsonb_set(t."Schema", '{fields}', (
                               SELECT jsonb_agg(
                                          CASE WHEN ord = (
                                                        SELECT min(o) FROM jsonb_array_elements(t."Schema"->'fields')
                                                               WITH ORDINALITY AS b(g, o)
                                                         WHERE jsonb_typeof(g) = 'object' AND g->>'key' = 'Сокращённое')
                                               THEN jsonb_set(f, '{tags}',
                                                        CASE WHEN jsonb_typeof(f->'tags') = 'array'
                                                             THEN f->'tags' ELSE '[]'::jsonb END
                                                        || to_jsonb('org.shortName'::text))
                                               ELSE f END
                                          ORDER BY ord)
                                 FROM jsonb_array_elements(t."Schema"->'fields') WITH ORDINALITY AS a(f, ord))),
                           "UpdatedAt" = now()
                     WHERE t."Id" = part_type
                       AND jsonb_typeof(t."Schema"->'fields') = 'array'
                       AND EXISTS (
                           SELECT 1 FROM jsonb_array_elements(t."Schema"->'fields') WITH ORDINALITY AS c(g, o)
                            WHERE jsonb_typeof(g) = 'object' AND g->>'key' = 'Сокращённое'
                              AND g->>'type' = ANY (ARRAY['string', 'text'])
                              AND o = (SELECT min(o2) FROM jsonb_array_elements(t."Schema"->'fields')
                                              WITH ORDINALITY AS d(g2, o2)
                                        WHERE jsonb_typeof(g2) = 'object' AND g2->>'key' = 'Сокращённое'));
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Назад тэг не снимаем: поставленное переносом неотличимо от поставленного администратором,
            // а прежний код тэга не читает вовсе (тот же довод, что у DirectoryRoles).
        }
    }
}
