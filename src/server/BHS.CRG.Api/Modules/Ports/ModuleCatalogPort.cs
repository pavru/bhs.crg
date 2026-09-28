using BHS.CRG.Application.Catalog;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules.Ports;
using MediatR;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочники ядра для модуля — через те же запросы, что и раздел каталога (ТЗ CORE-34).
///
/// ⚠️ Через MediatR, а не прямым чтением набора: у справочника есть обработчики, и завтра в них
/// появится то, что модулю обязательно тоже (скажем, разрешение ссылок или отбор по правам, если
/// учётные записи выйдут за пределы компании). Прямое чтение прошло бы мимо, и разошлось бы это
/// молча — данные-то вернулись бы.
/// </summary>
public sealed class ModuleCatalogPort(IMediator mediator) : IModuleCatalog
{
    public async Task<IReadOnlyList<ModuleCatalogEntry>> ListAsync(
        string entityType, CancellationToken ct = default)
    {
        var entries = await mediator.Send(new ListCatalogEntitiesQuery(entityType, null), ct);

        // Порядок задаёт ПОРТ. Запрос ядра не сортирует вовсе — порядок приходит от Postgres и
        // меняется после правок и уборки, — а список каталога на экране сортирует клиент. Обещание
        // «по названию» в контракте без сортировки здесь было бы ложным: оно сбывалось бы, пока
        // записи не правили (ревью PR #1106). Сравнение культурное, как у клиента: список читает
        // человек, и «Ёлка» в нём стоит между «Дубом» и «Жасмином», а не после латиницы.
        return [.. entries.Select(Map).OrderBy(e => e.DisplayName, StringComparer.CurrentCulture)];
    }

    public async Task<ModuleCatalogEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await mediator.Send(new GetCatalogEntityQuery(id), ct);
        return entry is null ? null : Map(entry);
    }

    /// <summary>
    /// Данные уходят СТРОКОЙ. Не потому, что так удобнее: <c>JsonDocument</c> — объект,
    /// принадлежащий чтению, и отдать его модулю значит отдать владение временем жизни. Строка
    /// ничьей жизни не переживает и разбирается модулем тем, чем он разбирает свои данные.
    /// </summary>
    private static ModuleCatalogEntry Map(CatalogEntity e) =>
        new(e.Id, e.EntityType, e.DisplayName, e.Data.RootElement.GetRawText());
}
