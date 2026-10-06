using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Правило архива при записи (ТЗ CORE-34.4, issue #1185): уже стоящая ссылка на архивную запись
/// остаётся, НОВАЯ — не появляется. До этого правило держали только экраны (в окне выбора архивных
/// записей нет) и привязка наборов; ссылку, присланную прямо в теле запроса — чужим клиентом, через
/// MCP или ошибшимся окном выбора, — сервер принимал молча.
///
/// <para>«Новая» — по ИДЕНТИФИКАТОРУ, а не по месту: ссылки, которые станут, минус ссылки, которые
/// лежат. Запись, переехавшая из одной строки таблицы в другую, новой не становится — человек её
/// не выбирал заново, и отказ на таком сохранении запер бы документ закрытого периода.</para>
///
/// <para>⚠️ У создания «как лежит» — ничего, поэтому новой считается каждая ссылка. Это верно для
/// путей, которыми пишет человек или внешний клиент: там каждая ссылка и есть выбор. Машинные пути,
/// которые переносят СТОЯВШИЕ ссылки (копия и перенос документа, восстановление копии, починки
/// данных), этой охраны не зовут вовсе — их вердикты названы в <c>RecordWriteGuardCoverageTests</c>.
/// Один машинный путь идёт человеческим адресом — вынос вложенного значения в общие данные: он
/// называет объект, из которого выносит, и ссылки, СОХРАНЁННЫЕ в том объекте, считаются стоявшими
/// (<c>alsoStanding</c>).</para>
///
/// <para>Замка между вопросом и записью нет, и он не нужен: запись, ушедшая в архив через мгновение
/// ПОСЛЕ сохранения документа, даёт ровно то же состояние — ссылку, стоявшую до архива. Эти два
/// порядка неразличимы и одинаково законны; отправка в архив ссылок не проверяет вовсе.</para>
/// </summary>
public static class ArchivedRefRule
{
    public const string ArchivedRef = "archived-ref";

    /// <summary>
    /// Ссылки, которых в лежащих данных нет, — каждая запись один раз, с первым местом, где она
    /// встретилась: отказу нужно назвать поле, а не перечислить все строки таблицы.
    /// </summary>
    public static IReadOnlyList<CatalogRefs.Placed> Added(
        JsonElement stored, JsonElement incoming, IReadOnlySet<Guid>? alsoStanding = null)
    {
        var all = CatalogRefs.PlacedIn(incoming);
        if (all.Count == 0) return all;

        var standing = CatalogRefs.IdsIn(stored);
        var seen = new HashSet<Guid>();
        return [.. all.Where(p => !standing.Contains(p.EntryId)
                               && alsoStanding?.Contains(p.EntryId) != true && seen.Add(p.EntryId))];
    }

    /// <summary>
    /// Находки правила. ⚠️ Запрос к базе — только при непустой разности: обычное сохранение ссылок
    /// не добавляет, и платить за правило оно не должно. Справочник типов (ради заголовка поля)
    /// спрашивается только на пути отказа — и у того же читателя, что у охраны схемы: второго
    /// чтения за одно сохранение нет.
    /// </summary>
    public static async Task<IReadOnlyList<AuditIssue>> RefusalsAsync(
        JsonElement stored, JsonElement incoming, Guid typeId, IReadOnlySet<Guid>? alsoStanding,
        Func<Task<IReadOnlyDictionary<Guid, DocumentType>>> typesById,
        IDomainObjectRepository objects, CancellationToken ct)
    {
        var added = Added(stored, incoming, alsoStanding);
        if (added.Count == 0) return [];

        var archived = (await objects.ArchivedAmongAsync([.. added.Select(p => p.EntryId)], ct))
            .ToDictionary(a => a.Id, a => a.DisplayName);
        if (archived.Count == 0) return [];

        var byId = await typesById();
        var titles = byId.ContainsKey(typeId)
            ? DocumentTypeSchemaReader.EffectiveFields(typeId, byId)
                .ToDictionary(f => f.Key, f => string.IsNullOrWhiteSpace(f.Title) ? f.Key : f.Title!)
            : [];

        return [.. added.Where(p => archived.ContainsKey(p.EntryId)).Select(p =>
        {
            // Имя — из базы, а не из присланной ссылки: её displayName пишет клиент, и запись под
            // этим именем человек в архиве мог бы не найти.
            var known = archived[p.EntryId];
            var name = string.IsNullOrWhiteSpace(known) ? "выбранная запись" : $"запись «{known}»";
            return new AuditIssue(ArchivedRef, AuditSeverity.Error, p.Path,
                $"{Where(p.Path, titles)}{name} в архиве, поставить ссылку на неё нельзя. " +
                "Верните запись из архива или выберите другую.");
        })];
    }

    /// <summary>
    /// «Поле «…»: » — по полю верхнего уровня: заголовок есть у него, а не у строки таблицы внутри.
    /// Ссылка в корне данных поля не имеет вовсе, основа записи — не поле схемы: называем как есть,
    /// а не пустыми кавычками.
    /// </summary>
    private static string Where(string path, IReadOnlyDictionary<string, string> titles)
    {
        if (path.Length == 0) return "";
        if (path == CatalogRefs.BaseRefKey) return "Основа: ";
        var end = path.IndexOfAny(['.', '[']);
        var key = end < 0 ? path : path[..end];
        return $"Поле «{titles.GetValueOrDefault(key, key)}»: ";
    }
}
