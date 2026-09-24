using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Перепись справочника ядра ДО и ПОСЛЕ миграции (ТЗ CORE-29/CORE-31, issue #960).
///
/// <para>Зачем. На стройках и разделах стоит весь <c>CatalogScope</c>: комплекты, документы, общие
/// данные, наборы и профили уровней адресуют уровни через них. Миграция, которая потеряет стройку
/// или обрубит ссылку, не сообщит об этом ничем: приложение поднимется, а пропажа обнаружится
/// открытым комплектом без документов — днями позже и у заказчика.</para>
///
/// <para>Поэтому расхождение переписи — ОТКАЗ СТАРТА, а не запись в журнале: половинчатый старт
/// запрещён (CORE-31), и «поднялось, но данных меньше» — ровно тот случай, ради которого правило
/// записано. Журнал прочитали бы после звонка заказчика.</para>
///
/// <para>⚠️ Читается СЫРЫМ SQL, а не через EF. До миграции модель и схема не совпадают по
/// определению — это и есть причина миграции, — и запрос через контекст упал бы на первой же новой
/// колонке, то есть сторож ронял бы старт вместо того, чтобы его проверить.</para>
/// </summary>
public sealed record MigrationCensus(
    long Constructions, long Sections, long DocumentSets,
    long OrphanSections, long OrphanSets, long OrphanObjects)
{
    /// <summary>
    /// Перепись или <c>null</c>, если справочника ещё нет — пустая база, первый запуск. Отличать
    /// обязательно: «ноль строек» и «таблицы нет» — разные вещи, и первую миграцию сверять не с чем.
    /// </summary>
    public static async Task<MigrationCensus?> ReadAsync(DbContext db, CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // БАЗЫ ЕЩЁ НЕТ — первый запуск: её создаст сама миграция. Сверять не с чем, и это
                // не отказ. Без этой ветки сторож ронял бы ЧИСТУЮ УСТАНОВКУ: приложение падало бы
                // до первой миграции, то есть install.sh у заказчика не поднялся бы вовсе
                // (поймано CI PR #1046 — локально база уже была, и проверка прошла вхолостую).
                return null;
            }
        }
        try
        {
            await using var exists = new NpgsqlCommand(
                "SELECT to_regclass('public.constructions') IS NOT NULL " +
                "AND to_regclass('public.sections') IS NOT NULL " +
                "AND to_regclass('public.document_sets') IS NOT NULL " +
                "AND to_regclass('public.domain_objects') IS NOT NULL", conn);
            if (await exists.ExecuteScalarAsync(ct) is not true) return null;

            await using var cmd = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM constructions),
                  (SELECT count(*) FROM sections),
                  (SELECT count(*) FROM document_sets),
                  -- Сироты: ссылка есть, а того, на что она указывает, нет. Считаются отдельно от
                  -- итогов, потому что «стройки все на месте, но разделы отвязались» — это тоже
                  -- потеря, и по одним лишь счётчикам она не видна.
                  (SELECT count(*) FROM sections s
                     WHERE NOT EXISTS (SELECT 1 FROM constructions c WHERE c."Id" = s."ConstructionId")),
                  (SELECT count(*) FROM document_sets d
                     WHERE NOT EXISTS (SELECT 1 FROM sections s WHERE s."Id" = d."SectionId")),
                  (SELECT count(*) FROM domain_objects o
                     WHERE o."ScopeId" IS NOT NULL
                       AND (  (o."ScopeLevel" = 'Construction'
                               AND NOT EXISTS (SELECT 1 FROM constructions c WHERE c."Id" = o."ScopeId"))
                           OR (o."ScopeLevel" = 'Section'
                               AND NOT EXISTS (SELECT 1 FROM sections s WHERE s."Id" = o."ScopeId"))
                           OR (o."ScopeLevel" = 'Set'
                               AND NOT EXISTS (SELECT 1 FROM document_sets d WHERE d."Id" = o."ScopeId"))))
                """, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new MigrationCensus(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Сверить перепись до и после. Расхождение — исключение, то есть отказ старта.
    ///
    /// <para>Сверяются и итоги, и сироты. Сирот стало больше — значит миграция обрубила ссылку:
    /// сама строка осталась, но уровень, на который она смотрит, исчез. По итогам это невидимо —
    /// количество не изменилось.</para>
    /// </summary>
    public static void EnsureUnchanged(MigrationCensus? before, MigrationCensus? after)
    {
        // Нечего сверять: базы не было, миграция создала её с нуля. Пустая перепись ПОСЛЕ при
        // непустой ДО — другое дело, и она сравнится ниже как расхождение.
        if (before is null) return;

        var problems = new List<string>();
        void Check(string what, long was, long now)
        {
            if (was != now) problems.Add($"{what}: было {was}, стало {now}");
        }

        if (after is null)
        {
            problems.Add("справочник исчез целиком: таблиц строек, разделов или комплектов после миграции нет");
        }
        else
        {
            Check("строек", before.Constructions, after.Constructions);
            Check("разделов", before.Sections, after.Sections);
            Check("комплектов", before.DocumentSets, after.DocumentSets);
            Check("разделов без своей стройки", before.OrphanSections, after.OrphanSections);
            Check("комплектов без своего раздела", before.OrphanSets, after.OrphanSets);
            Check("объектов без своего уровня", before.OrphanObjects, after.OrphanObjects);
        }

        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "Миграция изменила состав справочника ядра — приложение остановлено (ТЗ CORE-31).\n  " +
            string.Join("\n  ", problems) + "\n" +
            "Половинчатый старт запрещён: на стройках и разделах стоит адресация комплектов, общих " +
            "данных, наборов и профилей уровней, и потеря здесь выглядит потом как «пропали " +
            "документы». Восстановите базу из резервной копии прежней версией и сообщите о находке.");
    }
}
