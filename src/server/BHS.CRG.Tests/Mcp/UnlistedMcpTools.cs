using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// НАРОЧНОЕ НАРУШЕНИЕ для проверки сторожа ворот MCP (issue #1337): класс инструментов без ворот,
/// который нигде не вписан и нигде не зарегистрирован. Ровно так выглядел бы новый класс — в том числе
/// класс модуля в своей сборке, — о котором забыли все списки.
///
/// <para>Основная опись (<see cref="McpInventory.Declared()" />) тестовую сборку не сканирует, поэтому
/// на сервер и на остальных сторожей этот класс не влияет. Его подаёт описи только
/// <c>McpGateInventoryTests.Невписанный_класс_не_проходит_молча</c> — и ждёт отказа с именем
/// <see cref="Name" />.</para>
/// </summary>
[McpServerToolType]
public sealed class UnlistedMcpTools
{
    public const string Name = "unlisted_ungated_tool";

    [McpServerTool(Name = Name, ReadOnly = true), Description("Нарочное нарушение: ворот нет.")]
    public string Unlisted() => "открыт любому вошедшему";
}
