using System.Reflection;
using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// Записывающие инструменты MCP перечислены ЯВНО. Аннотация <c>ReadOnly</c> — это то, по чему клиент
/// решает, спрашивать ли подтверждение у пользователя: новый записывающий инструмент, забывший снять
/// флаг, выполнялся бы молча, а читающий, случайно потерявший его, дёргал бы подтверждение зря.
///
/// Список намеренно ведётся руками: добавление права записи агенту — решение, а не побочный эффект
/// правки, и оно обязано быть заметно в дифе.
/// </summary>
public class McpToolContractTests
{
    private static readonly Type[] ToolTypes =
    [
        typeof(DataSnapshotTools), typeof(DomainSnapshotTools), typeof(DocumentActionTools),
        typeof(ObservationTools), typeof(ReconciliationTools),
    ];

    /// <summary>
    /// Инструменты, которым позволено менять состояние системы. <c>generate_document</c> выпускает PDF
    /// своего документа (#425); <c>report_observation</c> записывает утверждение агента в журнал
    /// замечаний (#440) — именно утверждение, требующее подтверждения человеком, а не результат
    /// проверки. Подтверждать замечания агент не может, такого инструмента нет.
    ///
    /// <c>retract_observation</c> (#459) — агент отзывает СОБСТВЕННОЕ утверждение: это свидетельство
    /// против себя, а не самоодобрение, поэтому оно ему доступно, в отличие от подтверждения.
    ///
    /// <c>propose_alias</c> (#448) — предложение, что две позиции суть одно; на сверку оно не влияет,
    /// пока человек не подтвердит. Подтверждения агентом нет и здесь: неподтверждённый алиас в пути
    /// сравнения означал бы модель внутри арифметики.
    /// </summary>
    private static readonly string[] WriteTools =
        ["generate_document", "report_observation", "retract_observation", "propose_alias"];

    /// <summary>
    /// Инструменты, отдающие выдачу страницей. Изначально — те, чей объём растёт вместе со стройкой
    /// (#576): список без <c>truncated</c> либо упрётся в лимит клиента целиком, либо — что хуже —
    /// приедет молча неполным, и недочитанные позиции станут неотличимы от отсутствующих.
    ///
    /// С #590 сюда входят и навигационные списки (стройки, наборы, сверки, алиасы). Не потому, что
    /// они выросли, а потому, что форма списочной выдачи обязана быть ОДНА: пока половина списков
    /// была страницами, а половина голыми массивами, клиент под массив получал объект и молча видел
    /// ноль записей — отказ без единого признака, что форма другая.
    ///
    /// Список ведётся руками по той же причине, что и <see cref="WriteTools" />: страничность здесь
    /// решение о корректности, а не деталь оформления, и её потеря обязана быть видна в дифе.
    /// </summary>
    private static readonly string[] PagedTools =
        ["get_reconciliation_findings", "get_rows", "list_aliases", "list_catalog_entries",
         "list_constructions", "list_datasets", "list_material_quality_links", "list_observations",
         "list_quality_documents", "list_reconciliations"];

    private static IEnumerable<(string Name, MethodInfo Method)> AllToolMethods() =>
        ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Attr!.Name ?? x.Method.Name, x.Method));

    /// <summary>Страница узнаётся по типу результата: <c>Task&lt;SnapshotPage&lt;T&gt;&gt;</c> либо
    /// <c>Task&lt;RowsPage?&gt;</c> — строки источников получили страничность раньше (#415).</summary>
    private static bool ReturnsPage(MethodInfo m)
    {
        var t = m.ReturnType;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>)) t = t.GetGenericArguments()[0];
        return t == typeof(RowsPage)
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(SnapshotPage<>));
    }

    [Fact]
    public void DeclaredPagedTools_ReturnPages()
    {
        var paged = AllToolMethods().Where(t => ReturnsPage(t.Method)).Select(t => t.Name).Order().ToList();
        Assert.Equal(PagedTools.Order(), paged);
    }

    /// <summary>
    /// Прямая проверка правила из #590: <c>list_*</c> — это всегда страница. Предыдущий тест сверяет
    /// со списком, который ведут руками, и новый список, забытый в нём, прошёл бы незамеченным —
    /// ровно так регрессия и появилась.
    /// </summary>
    [Fact]
    public void EveryListTool_ReturnsPage()
        => Assert.Empty(AllToolMethods()
            .Where(t => t.Name.StartsWith("list_") && !ReturnsPage(t.Method))
            .Select(t => t.Name));

    /// <summary>
    /// Страница объявляет версию контракта: по ней клиент замечает смену формы сразу, а не через
    /// неверный отчёт. Проверяем на сериализованном виде — поле должно доехать ДО агента, а
    /// вычисляемые свойства записи легко потерять настройкой сериализатора.
    /// </summary>
    [Fact]
    public void SerializedPage_CarriesContractVersion()
    {
        var page = SnapshotPage<string>.Of(["раз"], offset: 0, limit: 10, maxLimit: 100);

        var json = JsonSerializer.Serialize(page, McpSerialization.ToolOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(SnapshotContract.Version, parsed.RootElement.GetProperty("contractVersion").GetInt32());
    }

    /// <summary>Строки источника — та же страница по смыслу, и их форма меняется тем же контрактом.</summary>
    [Fact]
    public void SerializedRowsPage_CarriesContractVersion()
    {
        var rows = new RowsPage(Guid.NewGuid(), 0, 10, 1, false, ["Поз"],
            [new Dictionary<string, string?> { ["Поз"] = "1" }]);

        var json = JsonSerializer.Serialize(rows, McpSerialization.ToolOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(SnapshotContract.Version, parsed.RootElement.GetProperty("contractVersion").GetInt32());
    }

    private static IEnumerable<(string Name, McpServerToolAttribute Attr)> AllTools() =>
        ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Attr!.Name ?? x.Method.Name, x.Attr!));

    [Fact]
    public void OnlyDeclaredTools_AreWritable()
    {
        var tools = AllTools().ToList();
        Assert.NotEmpty(tools);

        var writable = tools.Where(t => t.Attr.ReadOnly != true).Select(t => t.Name).Order().ToList();
        Assert.Equal(WriteTools.Order(), writable);
    }

    /// <summary>
    /// Подтверждение замечания — решение ЧЕЛОВЕКА (#414: предложить → подтвердить → персистить).
    /// Появись такой инструмент, агент подтверждал бы собственные утверждения, и журнал перестал бы
    /// отличать проверенное от заявленного.
    /// </summary>
    [Fact]
    public void NoTool_ReviewsOrConfirms()
        => Assert.Empty(AllTools()
            .Where(t => t.Name.Contains("review") || t.Name.Contains("confirm")
                     || t.Name.Contains("approve"))
            .Select(t => t.Name));

    /// <summary>
    /// Разрушительным не является ни один: выпуск заменяет файлы СВОЕГО документа и ничего чужого не
    /// трогает. Появись здесь настоящий разрушительный инструмент — он обязан объявиться честно.
    /// </summary>
    [Fact]
    public void NoTool_IsDestructive()
        => Assert.Empty(AllTools().Where(t => t.Attr.Destructive == true).Select(t => t.Name));

    /// <summary>Имя — часть контракта с агентом: клиенты кешируют список, а переименование ломает
    /// уже написанные пользователем сценарии. Пустое имя вдобавок непредсказуемо (берётся имя метода).</summary>
    [Fact]
    public void EveryTool_HasExplicitName()
        => Assert.Empty(AllTools()
            .Where(t => string.IsNullOrWhiteSpace(t.Attr.Name))
            .Select(t => t.Name));

    /// <summary>
    /// Полный набор инструментов, объявленный СПИСКОМ (issue #600). За одну живую сессию агента
    /// набор рос 10 → 15 → 17 → 21 → 22, и каждое изменение инвалидировало кэш модели целиком: около
    /// 35 % контекста сессии ушло на перечитывание инструкций и списка инструментов вместо работы.
    ///
    /// Список нужен не чтобы запретить новые инструменты, а чтобы их добавление было ВИДНО в дифе —
    /// вместе с решением, выкатывать ли это, пока кто-то работает.
    /// </summary>
    private static readonly string[] AllToolNames =
    [
        "generate_document", "get_catalog_entry", "get_construction", "get_dataset", "get_document",
        "get_document_set", "get_document_type", "get_reconciliation_findings", "get_rows",
        "get_source", "list_aliases", "list_catalog_entries", "list_constructions", "list_datasets",
        "list_material_quality_links", "list_observations", "list_quality_documents",
        "list_reconciliations", "propose_alias", "report_observation", "retract_observation",
        "validate_document",
    ];

    [Fact]
    public void ToolSet_MatchesDeclaredList()
        => Assert.Equal(AllToolNames.Order(), AllTools().Select(t => t.Name).Order());

    [Fact]
    public void ToolNames_AreUnique()
    {
        var dupes = AllTools()
            .GroupBy(t => t.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dupes);
    }
}
