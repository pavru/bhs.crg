using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Запомненное для строк поставщика — вопрос формы счёта (задача C3, issue #1079, ТЗ COST-7.1).
///
/// <para><b>Адрес ЧИТАЕТ, а не пишет</b>, хотя он и POST: строк в счёте десятки, и в адресную строку
/// наименования из бумаги не поместить. Позицию с пометкой форма кладёт в строки сама и отправляет
/// обычным сохранением строк — с версией счёта. Подставь сервер позицию здесь, запись шла бы мимо
/// защиты от устаревшей формы.</para>
///
/// <para>⚠️ <b>Не под <c>/api/costs/invoices/{id}/…</c> нарочно:</b> поставщик берётся из запроса, а не
/// из счёта. Форма спрашивает о строках, которых в базе ещё нет (вставка из буфера, набор).</para>
///
/// <para>Правом правки счёта, а не правом номенклатуры (ТЗ COST-7.1): сопоставляет тот, кто вводит
/// счета. Читающему счета вопрос ни к чему — подставить ему некуда.</para>
/// </summary>
public static class SupplierMatchEndpoints
{
    /// <summary>
    /// Сколько строк принимает один вопрос. Счёт на тысячу строк — редкость, а вопрос на сто тысяч —
    /// это уже не форма счёта.
    /// </summary>
    public const int MaxLines = 2000;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/costs/supplier-matches/suggestions", SuggestAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");

    private static async Task<Ok<MatchSuggestionsView>> SuggestAsync(
        MatchSuggestionsRequest body, CostsDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        if (body.SupplierId is not { } supplierId)
            throw new InvalidRequestException(
                "Поставщик не назван («supplierId»). Соответствия запоминаются по поставщику: одно и то же " +
                "наименование у двух поставщиков — разные строки, и без поставщика искать не по чему.");

        if (body.Lines is not { } lines)
            throw new InvalidRequestException("Строки не присланы («lines»): спрашивать не о чем.");

        if (lines.Count > MaxLines)
            throw new InvalidRequestException(
                $"Строк в вопросе — {lines.Count}, больше {MaxLines}. Спросите частями.");

        var found = await SupplierMatching.FindAsync(db, supplierId,
            [.. lines.Select(l => (l.SupplierCode, l.SupplierText))], ct);

        var positions = found.OfType<SupplierMatch>().Select(m => m.NomenclatureId).Distinct().ToList();
        var refs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, positions, ct);
        if (refs is null && positions.Count > 0)
            throw new ConflictException(
                $"Тип «{CostsRecordTypes.NomenclatureCode}» в системе не заведён, а соответствия на него " +
                "ссылаются. Подставлять нечего — и это не «ничего не запомнено».");
        var known = (refs ?? []).ToDictionary(r => r.Id);

        var items = new List<MatchSuggestion>();
        for (var index = 0; index < found.Count; index++)
        {
            if (found[index] is not { } match) continue;

            // Подстановка — НОВАЯ ссылка, и правило у неё то же, что у выбора руками (ТЗ CORE-34.4):
            // архивную позицию не подставляем. Но и не молчим о ней — иначе строка выглядела бы
            // незнакомой, и человек запомнил бы её заново, не узнав, что прежний выбор в архиве.
            var position = known.GetValueOrDefault(match.NomenclatureId);
            var issue = position is null ? MatchSuggestion.Lost
                : position.Archived ? MatchSuggestion.Archived
                : null;

            items.Add(new(index, match.Id, InvoiceLineMatchView.Name(match.Kind), match.SourceText,
                match.NomenclatureId, position?.DisplayName, position?.EntityType, issue,
                match.UpdatedAt, match.UpdatedByName));
        }

        return TypedResults.Ok(new MatchSuggestionsView(items));
    }
}

/// <summary>Вопрос формы: что запомнено для этих строк этого поставщика.</summary>
public sealed record MatchSuggestionsRequest(Guid? SupplierId, IReadOnlyList<MatchSuggestionLine>? Lines);

/// <summary>Строка вопроса — ровно то, по чему её узнают.</summary>
public sealed record MatchSuggestionLine(string? SupplierCode, string? SupplierText);

/// <summary>
/// Запомненное для строки вопроса.
/// </summary>
/// <param name="Index">Место строки в вопросе, с нуля. Строк без запомненного в ответе нет.</param>
/// <param name="By"><c>code</c> — узнана по артикулу, <c>name</c> — по наименованию.</param>
/// <param name="Source">Как ключ записан в бумаге, с которой его запомнили.</param>
/// <param name="Issue">Почему подставлять НЕЛЬЗЯ: <c>archived</c> — позиция в архиве, <c>lost</c> — её
/// больше нет. <c>null</c> — можно.</param>
public sealed record MatchSuggestion(
    int Index, Guid MatchId, string By, string Source, Guid NomenclatureId, string? NomenclatureName,
    string? NomenclatureType, string? Issue, DateTimeOffset RememberedAt, string? RememberedBy)
{
    public const string Archived = "archived";
    public const string Lost = "lost";
}

public sealed record MatchSuggestionsView(IReadOnlyList<MatchSuggestion> Items);
