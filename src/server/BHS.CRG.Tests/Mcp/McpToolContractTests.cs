using System.Reflection;
using BHS.CRG.Api.Mcp;
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
