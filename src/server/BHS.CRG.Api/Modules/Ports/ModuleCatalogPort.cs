using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Ports;
using MediatR;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочники ядра для модуля — через те же запросы, что и раздел общих данных (ТЗ CORE-34).
///
/// ⚠️ Через MediatR, а не прямым чтением набора: у справочника есть обработчики, и завтра в них
/// появится то, что модулю обязательно тоже (скажем, разрешение ссылок или отбор по правам, если
/// учётные записи выйдут за пределы компании). Прямое чтение прошло бы мимо, и разошлось бы это
/// молча — данные-то вернулись бы.
///
/// <para>⚠️ <b>Читаются ОБЩИЕ ДАННЫЕ</b> (<c>DomainObject</c>), а не прежняя таблица
/// <c>catalog_entities</c>. Разница не в имени набора: в <c>catalog_entities</c> не пишет ни один
/// экран и её не читает ни один адрес клиента, а ссылка <c>{"$ref":"catalog","entryId":…}</c>
/// разрешается по общим данным — и ядром при генерации (<c>EntityResolver</c>), и проверкой привязок.
/// Порт, читавший прежнюю таблицу, отвечал на вопрос «как называется эта организация» пустотой для
/// КАЖДОГО настоящего поставщика, а в реестре счетов это выглядело бы как «ссылка есть, названия
/// нет» — то есть как потеря данных. Поймано на форме счёта (issue #1076), прожило один PR.</para>
/// </summary>
public sealed class ModuleCatalogPort(IMediator mediator, IRepository<DocumentType> types) : IModuleCatalog
{
    public async Task<IReadOnlyList<ModuleCatalogEntry>> ListAsync(
        string entityType, CancellationToken ct = default)
    {
        var all = await types.GetAllAsync(ct);
        var root = all.FirstOrDefault(t => string.Equals(t.Code, entityType, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException(
                $"Вида записей «{entityType}» в системе нет: типа с таким кодом не существует. " +
                "Проверьте код вида в модуле — пустой список на этом месте означал бы, что записей " +
                "нет вовсе, и различить опечатку от пустого справочника было бы нечем.");

        // Вид — это тип И его подтипы: подтип организации остаётся организацией, и поставщиком
        // заказчика может быть любой из них. Считаем так же, как ядро в `isSubtypeOf`.
        var codes = Family(root, all);
        var entries = new List<DomainObject>();
        foreach (var id in codes.Keys)
            entries.AddRange(await mediator.Send(new ListCommonDataEntriesQuery(null, null, id), ct));

        // Порядок задаёт ПОРТ. Запрос ядра не сортирует вовсе — порядок приходит от Postgres и
        // меняется после правок и уборки, — а список каталога на экране сортирует клиент. Обещание
        // «по названию» в контракте без сортировки здесь было бы ложным: оно сбывалось бы, пока
        // записи не правили (ревью PR #1106). Сравнение культурное, как у клиента: список читает
        // человек, и «Ёлка» в нём стоит между «Дубом» и «Жасмином», а не после латиницы.
        return [.. entries
            .Select(e => Map(e, codes))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCulture)];
    }

    public async Task<ModuleCatalogEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await mediator.Send(new GetCommonDataEntryQuery(id), ct);
        if (entry is null) return null;

        var all = await types.GetAllAsync(ct);
        return Map(entry, all.ToDictionary(t => t.Id, t => t.Code));
    }

    /// <summary>
    /// Тип и все его потомки: идентификатор → код. Обход итеративный и слоями — цепочка родителей в
    /// базе ничем не мешает циклу, а рекурсия по ней кончилась бы переполнением стека вместо отказа
    /// (та же причина, что у <c>isSubtypeOf</c> на клиенте).
    /// </summary>
    private static Dictionary<Guid, string> Family(DocumentType root, IReadOnlyList<DocumentType> all)
    {
        var family = new Dictionary<Guid, string> { [root.Id] = root.Code };
        var frontier = new List<Guid> { root.Id };

        while (frontier.Count > 0)
        {
            var children = all.Where(t => t.ParentId is { } p && frontier.Contains(p) && !family.ContainsKey(t.Id))
                .ToList();
            foreach (var child in children) family[child.Id] = child.Code;
            frontier = [.. children.Select(t => t.Id)];
        }

        return family;
    }

    /// <summary>
    /// Данные уходят СТРОКОЙ. Не потому, что так удобнее: <c>JsonDocument</c> — объект,
    /// принадлежащий чтению, и отдать его модулю значит отдать владение временем жизни. Строка
    /// ничьей жизни не переживает и разбирается модулем тем, чем он разбирает свои данные.
    /// </summary>
    private static ModuleCatalogEntry Map(DomainObject e, IReadOnlyDictionary<Guid, string> codes) =>
        new(e.Id,
            codes.TryGetValue(e.CompositeTypeId, out var code) ? code : string.Empty,
            e.DisplayName,
            e.Data.RootElement.GetRawText());
}
