using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <para>⚠️ <b>Сбой самой записи вызов НЕ роняет</b> (ревью PR #1997). Действие инструмента к этому моменту
/// уже совершено: счёт заведён, задача поставлена. Ошибка, ушедшая агенту, сказала бы «не вышло» о том, что
/// вышло, — и агент повторил бы неидемпотентный вызов. Поэтому сбой записи — в лог сервера, а не в ответ: та
/// же плата, что названа у журнала (<c>IActivityLog</c>), — тихая потеря следа лучше ложного отказа. По той же
/// причине запись не слушает отмену запроса: клиент, оборвавший соединение, не отменяет сделанного.</para>
///
/// <para>Автор и канал записи — от автора журнала (<c>HttpContextActivityActor</c>): вызов идёт внутри
/// запроса к адресу <c>/mcp</c>, помеченному <c>ActivityChannel.Mcp</c>. Сам фильтр их не называет.</para>
///
/// <para>⚠️ Журнал пишется в <b>своей области служб</b>, а не в области вызова: контекст базы у инструмента
/// и журнала разный, и запись журнала не сохраняет того, что инструмент сохранять не стал.</para>
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
            && context.Services?.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory scopes)
        {
            var (targetId, targetLabel) = Target(tool.ProtocolTool.InputSchema, context.Params?.Arguments);
            try
            {
                // Своя область служб, а не область вызова: журнал сохраняет СВОЙ контекст базы. В общем он
                // сохранил бы и то, что инструмент оставил в трекере несохранённым, а неудавшаяся запись
                // осталась бы там Added и упала бы снова на следующем SaveChanges области (ревью PR #1997).
                // Автор и канал от этого не меняются: их даёт запрос (HttpContextAccessor), а не область.
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService(typeof(IActivityLog)) is IActivityLog journal)
                    await journal.RecordAsync(ActivityActions.McpToolCalled, targetId, targetLabel,
                        after: tool.ProtocolTool.Name, ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                (context.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
                    ?.CreateLogger(typeof(McpToolJournal).FullName!)
                    .LogError(ex, "Вызов инструмента MCP «{Tool}» удался, но запись журнала не сделана",
                        tool.ProtocolTool.Name);
            }
        }

        return result;
    };

    /// <summary>
    /// Цель — первый ОБЪЯВЛЕННЫЙ инструментом аргумент-идентификатор (<c>setId</c>, <c>documentId</c>,
    /// <c>datasetId</c>…) в порядке параметров его схемы, а не в порядке, в котором клиент сложил JSON:
    /// лишний аргумент, который инструмент молча пропускает (<c>requestId</c>), цель записи подменять не вправе
    /// (ревью PR #1997). Значение длиннее колонки — не идентификатор, и в цель не идёт: иначе отказ базы 22001
    /// пришёл бы уже после совершённого действия. Метка — имя аргумента со значением: название объекта знает
    /// его служба, и читать его ради журнала значило бы второй запрос на каждый вызов. Нет идентификатора
    /// (<c>propose_alias</c>) — запись без цели.
    /// </summary>
    public static (string? Id, string? Label) Target(JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var declared) || declared.ValueKind != JsonValueKind.Object)
            return (null, null);

        foreach (var parameter in declared.EnumerateObject())
        {
            var name = parameter.Name;
            if (!name.EndsWith("Id", StringComparison.Ordinal)
                || !arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            if (value.GetString() is { Length: > 0 and <= ActivityRecord.TargetIdMax } id) return (id, $"{name}: {id}");
        }
        return (null, null);
    }
}
