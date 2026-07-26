using System.Reflection;
using BHS.CRG.Api.Mcp;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// MCP-сервер был целиком читающим (#415–#423), и с #425 у него ровно ОДИН записывающий инструмент —
/// <c>generate_document</c>. Инвариант закрепляем тестом: аннотация <c>ReadOnly</c> — это то, по чему
/// клиент решает, спрашивать ли подтверждение у пользователя. Новый записывающий инструмент, забывший
/// снять флаг, выполнялся бы молча; читающий, случайно потерявший его, дёргал бы подтверждение зря.
/// </summary>
public class McpToolContractTests
{
    private static readonly Type[] ToolTypes =
    [
        typeof(DataSnapshotTools), typeof(DomainSnapshotTools), typeof(DocumentActionTools),
    ];

    /// <summary>Единственный инструмент, которому позволено менять состояние системы.</summary>
    private const string WriteTool = "generate_document";

    private static IEnumerable<(string Name, McpServerToolAttribute Attr)> AllTools() =>
        ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Attr!.Name ?? x.Method.Name, x.Attr!));

    [Fact]
    public void OnlyGenerateDocument_IsWritable()
    {
        var tools = AllTools().ToList();
        Assert.NotEmpty(tools);

        var writable = tools.Where(t => t.Attr.ReadOnly != true).Select(t => t.Name).ToList();
        Assert.Equal([WriteTool], writable);
    }

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
