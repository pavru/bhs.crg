using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// «Номенклатура» — НАД «Материалом», а не рядом с ним (ТЗ CORE-9, TYPE-7.1, issue #963).
    ///
    /// <para><b>Зачем.</b> ТЗ требует справочник номенклатуры, а в рабочей базе уже есть тип
    /// «Материал» — составной тип СТРОКИ: наименование, артикул, производитель, единица, документ
    /// качества и вместе с ними количество и цена. Заведи мы справочник рядом — у заказчика
    /// оказалось бы два «материала»: строки документов ссылались бы на один, справочник жил бы
    /// вторым, а сопоставление с документами качества разошлось бы между ними. TYPE-7.1 велит
    /// иначе: «Номенклатура» — новый базовый тип без количества и цены, а «Материал» становится
    /// производным от неё.</para>
    ///
    /// <para><b>Поля ПЕРЕЕЗЖАЮТ, а не объявляются заново.</b> Наверх уходят те же записи схемы, как
    /// они лежат, — со своими тэгами, ссылками и обязательностью. Объяви мы номенклатуру по примеру
    /// из ТЗ, сменился бы порядок тэгов <c>identity</c> — а из них складывается ключ, которым
    /// строка накладной находит запись справочника и которым связаны 54 существующие связки с
    /// документами качества. Ключ обязан остаться тем же, и единственный способ это обеспечить —
    /// не переписывать разметку, а перенести её.</para>
    ///
    /// <para><b>Данные документов не меняются вовсе.</b> Значения лежат по ключам полей, а
    /// эффективная схема наследника — это поля родителя плюс свои. Ключи те же, значит документы
    /// открываются и печатаются как раньше; проверено генерацией на копии рабочей базы.</para>
    ///
    /// <para>⚠️ <b>Ссылку у «Работы» заводит только эта миграция, и она одноразовая.</b> Не
    /// сложились её условия — классификатор позже заведёт проекция (она повторяется каждым
    /// стартом), а ссылки у «Работы» не будет уже никогда, и никто об этом не скажет. Условий,
    /// которые к этому ведут, два: тип с кодом «ВидРаботы» уже есть и принадлежит не ядру, либо
    /// имя «Вид работы» занято. Оба — про базу, где человек завёл свой классификатор; оба видны в
    /// журнале запуска как пропуск справочника. Тогда ссылку добавляет администратор сам, и найдёт
    /// её код по тэгу <c>ref.workType</c>, а не по названию поля — ровно для этого тэг и нужен
    /// (нашло ревью PR #1055).</para>
    ///
    /// <para>⚠️ <b>Всё под условиями, и каждое — про чужую работу.</b> Нет «Материала» (чистая
    /// установка) — переносить нечего. У «Материала» уже есть родитель — значит иерархию строил
    /// человек, и встраиваться в неё миграция не вправе. Код или имя «Номенклатура» заняты — второй
    /// такой тип означал бы, что из редактора не сохранится ни один из двух (уникального индекса на
    /// имя в базе нет, наступали в #1052). Не сложилось — миграция не делает НИЧЕГО: база остаётся
    /// рабочей, справочник просто не появляется.</para>
    /// </summary>
    public partial class NomenclatureAboveMaterial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    mat_id      uuid;
                    mat_parent  uuid;
                    mat_schema  jsonb;
                    mat_group   varchar;
                    mat_storage varchar;
                    mat_vis     varchar;
                    nomen_id    uuid;
                    lifted      jsonb;
                    kept        jsonb;
                    cls_id      uuid;
                    work_id     uuid;
                    work_schema jsonb;
                BEGIN
                    SELECT "Id", "ParentId", "Schema", "Group", "Storage", "Visibility"
                      INTO mat_id, mat_parent, mat_schema, mat_group, mat_storage, mat_vis
                      FROM document_types WHERE "Code" = 'Материал';

                    -- ── 1. Номенклатура над материалом ────────────────────────────────
                    IF mat_id IS NOT NULL
                       AND mat_parent IS NULL
                       AND NOT EXISTS (SELECT 1 FROM document_types WHERE "Code" = 'Номенклатура')
                       AND NOT EXISTS (SELECT 1 FROM document_types
                                        WHERE lower(btrim("Name")) = lower('Номенклатура'))
                    THEN
                        -- Наверх — всё, кроме количества и цены: они принадлежат СТРОКЕ, а не
                        -- справочнику (TYPE-7.1 называет их дословно). Порядок полей сохраняется.
                        SELECT coalesce(jsonb_agg(f ORDER BY ord), '[]'::jsonb) INTO lifted
                          FROM jsonb_array_elements(coalesce(mat_schema->'fields', '[]'::jsonb))
                               WITH ORDINALITY AS a(f, ord)
                         WHERE f->>'key' NOT IN ('Количество', 'Цена');

                        SELECT coalesce(jsonb_agg(f ORDER BY ord), '[]'::jsonb) INTO kept
                          FROM jsonb_array_elements(coalesce(mat_schema->'fields', '[]'::jsonb))
                               WITH ORDINALITY AS a(f, ord)
                         WHERE f->>'key' IN ('Количество', 'Цена');

                        nomen_id := gen_random_uuid();

                        -- Уровень «открытый» (TYPE-7): схему справочника класса A заказчик меняет
                        -- целиком (CORE-7.3). Хранение, видимость и группу берём у материала — это
                        -- тот же справочник, просто теперь у него есть основание.
                        INSERT INTO document_types
                            ("Id", "Name", "Code", "Schema", "PluginBindings", "CreatedAt", "UpdatedAt",
                             "ParentId", "Kind", "IsAbstract", "Group", "AllowsProxy", "Module",
                             "ReadChannels", "Storage", "Visibility", "EditLevel")
                        VALUES
                            (nomen_id, 'Номенклатура', 'Номенклатура',
                             jsonb_build_object('fields', lifted), '[]'::jsonb, now(), now(),
                             NULL, 'Composite', false, mat_group, false, 'core',
                             '', mat_storage, mat_vis, 'Open');

                        -- У материала остаются количество и цена; остальное он теперь наследует.
                        -- Прочее в схеме (печатные блоки, группы) НЕ трогаем: блок «наименование
                        -- (артикул) — количество» печатает строку, и место ему здесь.
                        UPDATE document_types
                           SET "Schema" = jsonb_set("Schema", '{fields}', kept),
                               "ParentId" = nomen_id,
                               "UpdatedAt" = now()
                         WHERE "Id" = mat_id;
                    END IF;

                    -- ── 2. Ссылка «Работы» на классификатор видов работ ───────────────
                    --
                    -- Сам классификатор объявляет ЯДРО, и заводится он проекцией при старте — то
                    -- есть ПОСЛЕ миграций. А ссылке цель нужна здесь и сейчас, поэтому пустой тип
                    -- заводим мы, а скелет (код, наименование, единица, признаки) допишет проекция
                    -- тем же запуском. Порядок обратный был бы невозможен: идентификатор цели в
                    -- каждой установке свой, и в объявлении его нет.
                    -- ⚠️ Классификатор — только НАШ тип: составной и принадлежащий ядру (нашло
                    -- ревью PR #1055). Тип с тем же кодом мог когда-то завести человек — тогда он
                    -- чужой, и ссылка на него была бы ровно тем, что запрещает проекция: опора на
                    -- цель чужого владельца или не того рода. Проекция такой тип пропускает и
                    -- называет причину в журнале запуска; ссылку не заводим и мы.
                    SELECT "Id" INTO cls_id FROM document_types
                     WHERE "Code" = 'ВидРаботы' AND "Kind" = 'Composite' AND "Module" = 'core';

                    IF cls_id IS NULL
                       -- Код уникален индексом: тип с этим кодом есть, но он чужой (условие выше) —
                       -- вставка упала бы, а упавшая миграция означает, что приложение не
                       -- поднимается вовсе. Пропускаем и это.
                       AND NOT EXISTS (SELECT 1 FROM document_types WHERE "Code" = 'ВидРаботы')
                       -- Единицы измерения обязательны классификатору по ТЗ CORE-8. Нет их — не
                       -- заводим и тип: проекция такому типу отказала бы, а отказ в проекции
                       -- останавливает старт, то есть мы своими руками сделали бы базу неподнимаемой.
                       AND EXISTS (SELECT 1 FROM document_types WHERE "Code" = 'ЕдиницаИзмерения')
                       AND NOT EXISTS (SELECT 1 FROM document_types
                                        WHERE lower(btrim("Name")) = lower('Вид работы'))
                    THEN
                        cls_id := gen_random_uuid();
                        INSERT INTO document_types
                            ("Id", "Name", "Code", "Schema", "PluginBindings", "CreatedAt", "UpdatedAt",
                             "ParentId", "Kind", "IsAbstract", "Group", "AllowsProxy", "Module",
                             "ReadChannels", "Storage", "Visibility", "EditLevel")
                        VALUES
                            (cls_id, 'Вид работы', 'ВидРаботы', '{"fields": []}'::jsonb, '[]'::jsonb,
                             now(), now(), NULL, 'Composite', false, NULL, false, 'core',
                             '', 'SharedObject', 'Shared', 'Extendable');
                    END IF;

                    SELECT "Id", "Schema" INTO work_id, work_schema
                      FROM document_types WHERE "Code" = 'Работа';

                    -- Ссылка НЕОБЯЗАТЕЛЬНАЯ (TYPE-7.1): существующие строки работ заполнены без неё
                    -- и обязательной она сделала бы негодной каждую из них. Тэг ref.workType — то,
                    -- по чему её найдёт код, не зная названия поля.
                    IF work_id IS NOT NULL AND cls_id IS NOT NULL
                       AND NOT EXISTS (
                           SELECT 1 FROM jsonb_array_elements(coalesce(work_schema->'fields', '[]'::jsonb)) f
                            WHERE f->>'key' = 'ВидРаботы'
                               OR coalesce(f->'tags', '[]'::jsonb) @> '["ref.workType"]'::jsonb)
                    THEN
                        UPDATE document_types
                           SET "Schema" = jsonb_set("Schema", '{fields}',
                                   coalesce("Schema"->'fields', '[]'::jsonb)
                                   || jsonb_build_array(jsonb_build_object(
                                        'key', 'ВидРаботы',
                                        'title', 'Вид работы',
                                        'type', 'complex',
                                        'typeId', cls_id::text,
                                        'required', false,
                                        'tags', jsonb_build_array('ref.workType')))),
                               "UpdatedAt" = now()
                         WHERE "Id" = work_id;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Возврат ровно того, что меняли, и с теми же условиями про чужую работу: у
            // номенклатуры не должно быть ни своих объектов, ни других наследников — иначе откат
            // унёс бы данные, которых при переходе не было.
            //
            // ⚠️ Полноценная дорога назад — это не откат миграции, а прежний образ плюс резервная
            // копия (решение по G1): обратную миграцию никто не гоняет на живых данных, и второй
            // непроверенной дорогой она опаснее отсутствия. Здесь — только то, что нужно, чтобы
            // `ef migrations remove` и прогон миграций туда-обратно в тестах не оставляли мусора.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    nomen_id   uuid;
                    nomen_sch  jsonb;
                    mat_id     uuid;
                    work_id    uuid;
                    cls_id     uuid;
                BEGIN
                    SELECT "Id", "Schema" INTO nomen_id, nomen_sch
                      FROM document_types WHERE "Code" = 'Номенклатура';
                    SELECT "Id" INTO mat_id FROM document_types
                     WHERE "Code" = 'Материал' AND "ParentId" = nomen_id;

                    IF nomen_id IS NOT NULL AND mat_id IS NOT NULL
                       AND NOT EXISTS (SELECT 1 FROM domain_objects
                                        WHERE "CompositeTypeId" = nomen_id)
                       AND NOT EXISTS (SELECT 1 FROM document_types
                                        WHERE "ParentId" = nomen_id AND "Id" <> mat_id)
                    THEN
                        UPDATE document_types
                           SET "Schema" = jsonb_set("Schema", '{fields}',
                                   coalesce(nomen_sch->'fields', '[]'::jsonb)
                                   || coalesce("Schema"->'fields', '[]'::jsonb)),
                               "ParentId" = NULL,
                               "UpdatedAt" = now()
                         WHERE "Id" = mat_id;

                        DELETE FROM document_types WHERE "Id" = nomen_id;
                    END IF;

                    -- ⚠️ Ссылка у «Работы» снимается ТОЛЬКО вместе с классификатором, который
                    -- заводили мы: составной, ядра, пустой (скелет ему дописывает проекция),
                    -- без своих объектов и наследников. Безусловное снятие уносило бы поле,
                    -- заведённое администратором, — там, где Up его не добавлял вовсе, потому что
                    -- условия не сложились (нашло ревью PR #1055). Наполненный классификатор тоже
                    -- уже не наш: его и ссылку на него оставляем как есть.
                    SELECT t."Id" INTO cls_id FROM document_types t
                     WHERE t."Code" = 'ВидРаботы' AND t."Kind" = 'Composite' AND t."Module" = 'core'
                       AND coalesce(jsonb_array_length(t."Schema"->'fields'), 0) = 0
                       AND NOT EXISTS (SELECT 1 FROM domain_objects o WHERE o."CompositeTypeId" = t."Id")
                       AND NOT EXISTS (SELECT 1 FROM document_types c WHERE c."ParentId" = t."Id");

                    SELECT "Id" INTO work_id FROM document_types WHERE "Code" = 'Работа';

                    IF cls_id IS NOT NULL THEN
                        IF work_id IS NOT NULL THEN
                            UPDATE document_types
                               SET "Schema" = jsonb_set("Schema", '{fields}', coalesce((
                                       SELECT jsonb_agg(f ORDER BY ord)
                                         FROM jsonb_array_elements(coalesce("Schema"->'fields', '[]'::jsonb))
                                              WITH ORDINALITY AS a(f, ord)
                                        WHERE NOT (coalesce(f->'tags', '[]'::jsonb)
                                                   @> '["ref.workType"]'::jsonb
                                                   AND f->>'typeId' = cls_id::text)), '[]'::jsonb)),
                                   "UpdatedAt" = now()
                             WHERE "Id" = work_id;
                        END IF;

                        DELETE FROM document_types WHERE "Id" = cls_id;
                    END IF;
                END $$;
                """);
        }
    }
}
