using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Перечисление РЕАЛЬНЫХ объектов в <c>resources/list</c> (issue #427).
///
/// Все ресурсы объявлены шаблонами URI, а шаблоны уезжают в <c>resources/templates/list</c>. Клиент,
/// показывающий только <c>resources/list</c> — распространённый случай, — не видел ничего и честно
/// сообщал пользователю, что ресурсов нет. Как способ АДРЕСАЦИИ шаблон работает, но заявленный смысл
/// ресурсов («прикрепить объект к диалогу») им не покрывается: выбирают «Комплект 250701.ЭОМ-1» из
/// списка, а не подставляют UUID в шаблон руками.
///
/// Шаблоны остаются — клиент с их поддержкой получает оба способа, и чтение перечисленных здесь URI
/// идёт через них же (конкретный URI совпадает с шаблоном).
/// </summary>
public static class McpResourceCatalog
{
    /// <summary>
    /// Витрина рабочих единиц: стройки, комплекты, наборы данных. Отдельных документов, типов и
    /// записей каталога здесь намеренно нет — документов сотни и они достижимы из комплекта, типы это
    /// конфигурация, а записи каталога — то, на что ссылаются, а не то, что прикрепляют к разговору.
    /// За полным обходом домена есть инструменты.
    ///
    /// Без усечения и без курсора: эти единицы человек заводит руками, поэтому их естественно немного,
    /// а молча обрезанный список прикрепления спрятал бы объекты от пользователя.
    /// </summary>
    public static async ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> request, CancellationToken ct)
    {
        var services = request.Services
            ?? throw new InvalidOperationException("Нет области сервисов для перечисления ресурсов.");
        return new ListResourcesResult
        {
            Resources = [.. await BuildAsync(
                (IDomainSnapshotService)services.GetService(typeof(IDomainSnapshotService))!,
                (IDataSnapshotService)services.GetService(typeof(IDataSnapshotService))!, ct)],
        };
    }

    /// <summary>Сама витрина, без привязки к транспорту — тем же приёмом, что и везде: MCP-слой тонкий.</summary>
    public static async Task<IReadOnlyList<Resource>> BuildAsync(
        IDomainSnapshotService domain, IDataSnapshotService datasets, CancellationToken ct = default)
    {
        var resources = new List<Resource>();

        var constructions = await ReadAllAsync(
            (offset, token) => domain.ListConstructionsAsync(Guid.Empty, offset, ct: token), ct);
        foreach (var c in constructions)
        {
            resources.Add(New($"bhs://construction/{c.Id}", c.Name, "Стройка",
                $"Разделов: {c.SectionCount}, комплектов: {c.SetCount}, документов: {c.DocumentCount}."));

            var detail = await domain.GetConstructionAsync(c.Id, ct);
            if (detail is null) continue;
            foreach (var section in detail.Sections)
                foreach (var set in section.Sets)
                    // Контекст в описании обязателен: одноимённые комплекты разных разделов иначе
                    // неразличимы в списке выбора.
                    resources.Add(New($"bhs://document-set/{set.Id}", set.Name, "Комплект документов",
                        $"{c.Name} / {section.Name}. Документов: {set.DocumentCount}."));
        }

        foreach (var d in await ReadAllAsync(
                     (offset, token) => datasets.ListDatasetsAsync(null, null, offset, ct: token), ct))
            // Уровень — по той же причине, что контекст у комплекта: имена наборов повторяются, и без
            // него одноимённые записи в списке выбора неразличимы.
            resources.Add(New($"bhs://dataset/{d.Id}", d.Name, "Набор данных",
                $"Уровень: {d.Scope}. Формат: {d.Format}. Источников: {d.SourceCount}."
                + (d.Stale ? " Данные могли устареть." : "")));

        return resources;
    }

    /// <summary>
    /// Дочитывает страничную выдачу до конца. Витрина обязана быть ПОЛНОЙ — молча обрезанный список
    /// прикрепления спрятал бы объекты от пользователя, — а списки стали страничными все без
    /// исключения (#590), в том числе те, чью длину задаёт структура стройки.
    /// </summary>
    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        Func<int, CancellationToken, Task<SnapshotPage<T>>> fetch, CancellationToken ct)
    {
        var all = new List<T>();
        while (true)
        {
            var page = await fetch(all.Count, ct);
            all.AddRange(page.Items);
            // Пустая страница при truncated=true означала бы, что смещение не двигается: выходим,
            // иначе цикл вечный.
            if (!page.Truncated || page.Items.Count == 0) return all;
        }
    }

    private static Resource New(string uri, string name, string kind, string description) => new()
    {
        Uri = uri,
        Name = name,
        Title = $"{kind}: {name}",
        Description = description,
        MimeType = "application/json",
    };
}
