using System.Text.Json;
using BHS.CRG.Application.Activity;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Действие агента видно в журнале (ТЗ CORE-23, CORE-28, issue #1336, design D4): удавшийся вызов
/// меняющего инструмента MCP оставляет запись <see cref="ActivityActions.McpToolCalled" />.
///
/// <para><b>Фильтр вызовов сервера, а не строка в каждом инструменте.</b> Регистрируется один раз рядом с
/// <c>AddAuthorizationFilters()</c> и видит все вызовы <c>tools/call</c>: новый инструмент журнал «забыть»
/// не может. Сторож — <c>McpToolJournalTests</c>: перечень меняющих он берёт из описи регистрации
/// (<c>McpInventory</c>), а не из списка, набранного руками.</para>
///
/// <para><b>Меняющий</b> — тот, кто не объявил себя «только чтение» (<c>ReadOnly = true</c> атрибута, в
/// протоколе — <c>readOnlyHint</c>). Тот же признак, по которому клиент решает, спрашивать ли
/// подтверждение (<c>McpToolContractTests</c>). Не объявил вовсе — считается меняющим: молчание
/// инструмента не повод молчать журналу.</para>
///
/// <para>⚠️ Запись — ПОСЛЕ удавшегося вызова, по общему правилу журнала. Отказ прав, исключение и ответ
/// <c>isError</c> записи не оставляют: журнал свидетельствует о том, что произошло.</para>
///
/// <para>Автор и канал записи — от автора журнала (<c>HttpContextActivityActor</c>): вызов идёт внутри
/// запроса к адресу <c>/mcp</c>, помеченному <c>ActivityChannel.Mcp</c>. Сам фильтр их не называет.</para>
/// </summary>
public static class McpToolJournal
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        var result = await next(context, ct);

        if (result.IsError != true
            && context.MatchedPrimitive is McpServerTool tool
            && tool.ProtocolTool.Annotations?.ReadOnlyHint != true
            && context.Services?.GetService(typeof(IActivityLog)) is IActivityLog journal)
        {
            var (targetId, targetLabel) = Target(context.Params?.Arguments);
            await journal.RecordAsync(ActivityActions.McpToolCalled, targetId, targetLabel,
                after: tool.ProtocolTool.Name, ct: ct);
        }

        return result;
    };

    /// <summary>
    /// Цель — первый аргумент-идентификатор (<c>setId</c>, <c>documentId</c>, <c>datasetId</c>…) в порядке
    /// объявления параметров. Метка — имя аргумента со значением: название объекта знает его служба, и
    /// читать его ради журнала значило бы второй запрос на каждый вызов. Нет идентификатора
    /// (<c>propose_alias</c>) — запись без цели.
    /// </summary>
    internal static (string? Id, string? Label) Target(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return (null, null);
        foreach (var (name, value) in arguments)
        {
            if (!name.EndsWith("Id", StringComparison.Ordinal) || value.ValueKind != JsonValueKind.String) continue;
            if (value.GetString() is { Length: > 0 } id) return (id, $"{name}: {id}");
        }
        return (null, null);
    }
}
