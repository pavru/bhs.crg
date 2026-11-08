using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>Строка набора: единица одна на строку; <c>Parts: null</c> — «к правилу раздела».</summary>
public sealed record LineWrite(Guid RowId, PartUnit Unit, IReadOnlyList<PartRequest>? Parts);

/// <summary>Правило одного раздела ЛСР; <c>Target: null</c> снимает правило.</summary>
/// <param name="ResetExceptions">Снять части строк раздела («Сбросить исключения раздела»).</param>
/// <param name="Lines">Набор строк этого раздела той же транзакцией — так делается «Отменить».</param>
public sealed record RuleWrite(
    string? LsrSectionCode, DistributionTarget? Target, bool ResetExceptions, IReadOnlyList<LineWrite>? Lines);

/// <summary>Решение по позиции отчёта переноса.</summary>
public enum CarryOverAction
{
    Apply,
    Skip,
    Acknowledge,
}

public sealed record CarryOverDecision(CarryOverAction Action, Guid? RowId);

/// <summary>
/// Разбор тел записи распределения (change <c>plan-10-distribution</c>, «Адреса» в <c>design.md</c>) — руками, а не
/// привязкой к записи: «поля нет» и «поле равно <c>null</c>» здесь значат разное. <c>lsrSectionCode: null</c> —
/// строки вне разделов, а забытое поле — 400; <c>target: null</c> снимает правило, а забытая цель — 400; <c>parts:
/// null</c> возвращает строку к правилу. Привязка к записи сделала бы из забытого поля снятие правила.
/// </summary>
public static class DistributionRequests
{
    public static RuleWrite Rule(JsonElement body)
    {
        Object(body, "Правило раздела");
        if (!body.TryGetProperty("lsrSectionCode", out var code))
            throw new InvalidRequestException(
                "Раздел ЛСР не назван («lsrSectionCode»): поле обязательно, а null — строки вне разделов ЛСР.");
        if (code.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidRequestException("«lsrSectionCode» — строка: код раздела ЛСР из ответа распределения.");
        if (!body.TryGetProperty("target", out var target))
            throw new InvalidRequestException(
                "Цель правила не названа («target»): раздел стройки, «стройка в целом» или null — снять правило.");

        var reset = body.TryGetProperty("resetExceptions", out var r) && r.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new InvalidRequestException("«resetExceptions» — true или false."),
        };
        IReadOnlyList<LineWrite>? lines = body.TryGetProperty("lines", out var l) && l.ValueKind != JsonValueKind.Null
            ? LineList(l)
            : null;
        return new RuleWrite(code.ValueKind == JsonValueKind.Null ? null : code.GetString(), Target(target), reset, lines);
    }

    public static IReadOnlyList<LineWrite> Lines(JsonElement body)
    {
        Object(body, "Набор строк");
        if (!body.TryGetProperty("lines", out var lines) || lines.ValueKind == JsonValueKind.Null)
            throw new InvalidRequestException("Строки не названы («lines»): набор из одной и больше строк.");
        return LineList(lines);
    }

    public static CarryOverDecision Decision(JsonElement body)
    {
        Object(body, "Решение по переносу");
        var action = body.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        Guid? rowId = body.TryGetProperty("rowId", out var row) && row.ValueKind != JsonValueKind.Null
            ? Id(row, "rowId")
            : null;
        return action switch
        {
            "apply" => new CarryOverDecision(CarryOverAction.Apply,
                rowId ?? throw new InvalidRequestException("Строка-кандидат не названа («rowId»).")),
            "skip" => new CarryOverDecision(CarryOverAction.Skip, null),
            "acknowledge" => new CarryOverDecision(CarryOverAction.Acknowledge, null),
            _ => throw new InvalidRequestException(
                "Решение не названо («action»): apply — на строку-кандидат, skip — не переносить, acknowledge — понятно."),
        };
    }

    private static DistributionTarget? Target(JsonElement target)
    {
        if (target.ValueKind == JsonValueKind.Null) return null;
        Object(target, "Цель правила");
        var kind = target.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return kind switch
        {
            "whole" => DistributionTarget.WholeConstruction,
            "section" => DistributionTarget.Section(target.TryGetProperty("sectionId", out var s)
                ? Id(s, "sectionId")
                : throw new InvalidRequestException("Раздел стройки не назван («target.sectionId»).")),
            _ => throw new InvalidRequestException("Вид цели («target.kind») — whole или section."),
        };
    }

    private static List<LineWrite> LineList(JsonElement lines)
    {
        if (lines.ValueKind != JsonValueKind.Array)
            throw new InvalidRequestException("«lines» — массив строк.");
        var list = lines.EnumerateArray().Select(Line).ToList();
        if (list.GroupBy(x => x.RowId).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidRequestException($"Строка {twice.Key} названа в наборе дважды — у строки одно распределение.");
        return list;
    }

    private static LineWrite Line(JsonElement line)
    {
        Object(line, "Строка набора");
        var rowId = line.TryGetProperty("rowId", out var id)
            ? Id(id, "rowId")
            : throw new InvalidRequestException("Строка набора без «rowId».");
        if (!line.TryGetProperty("parts", out var parts))
            throw new InvalidRequestException(
                $"У строки {rowId} не названы части («parts»): массив частей или null — вернуть строку к правилу раздела.");
        var unit = !line.TryGetProperty("unit", out var u) || u.ValueKind == JsonValueKind.Null ? PartUnit.Row
            : u.ValueKind == JsonValueKind.String && u.GetString() == "row" ? PartUnit.Row
            : u.ValueKind == JsonValueKind.String && u.GetString() == "plan" ? PartUnit.Plan
            : throw new InvalidRequestException($"Единица строки {rowId} («unit») — row (как в файле) или plan (единица плана).");
        if (parts.ValueKind == JsonValueKind.Null) return new LineWrite(rowId, unit, null);
        if (parts.ValueKind != JsonValueKind.Array)
            throw new InvalidRequestException($"Части строки {rowId} («parts») — массив или null.");

        var list = parts.EnumerateArray().Select(p =>
        {
            Object(p, "Часть строки");
            var section = p.TryGetProperty("sectionId", out var s) ? Id(s, "sectionId")
                : throw new InvalidRequestException($"У части строки {rowId} не назван раздел стройки («sectionId»).");
            var quantity = p.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetDecimal(out var d)
                ? d
                : throw new InvalidRequestException($"У части строки {rowId} нет количества («quantity») — числа.");
            return new PartRequest(section, quantity);
        }).ToList();
        if (list.Count == 0)
            throw new InvalidRequestException(
                $"У строки {rowId} пустой набор частей. Чтобы вернуть строку к правилу раздела, пришлите «parts»: null.");
        return new LineWrite(rowId, unit, list);
    }

    private static Guid Id(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id
            : throw new InvalidRequestException($"«{field}» — не идентификатор.");

    private static void Object(JsonElement value, string what)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException($"{what} — JSON-объект.");
    }
}
