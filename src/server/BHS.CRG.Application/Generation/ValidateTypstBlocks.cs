using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>Одна проблема сборки Typst-блоков, привязанная к конкретному блоку (тип + вариант).</summary>
public record TypstBlockProblem(
    string Severity,        // "error" | "warning"
    string Code,            // "cycle" | "duplicate-fn" | "syntax" | "checker-unavailable"
    string Message,
    Guid? TypeId,
    string? TypeName,
    string? VariantName,
    string? FnName,
    int? Line);             // строка внутри блока (для синтакса), 1-based; иначе null

/// <summary>
/// Проверяет, соберётся ли typeblocks.typ ВСЕХ типов (issue #309, фаза 2). Глобальна (typeblocks
/// общий), с draft-overlay: черновик редактируемого типа (<paramref name="DraftRenders"/>) подмешивается
/// поверх персистентных блоков остальных — граф «как если бы сохранили» (иначе проверка на «Применить»
/// до «Сохранить» видела бы старую версию). Ловит: циклы взаимных ссылок и дубликаты имён (граф) +
/// синтаксические ошибки (Typst CLI). «Ссылка на несуществующую функцию» здесь НЕ ловится (наш universe
/// имён неполон — userlib/builtins) — это ошибка времени генерации.
/// </summary>
public record ValidateTypstBlocksQuery(Guid? OverlayTypeId, JsonElement? DraftRenders)
    : IRequest<IReadOnlyList<TypstBlockProblem>>;

public class ValidateTypstBlocksHandler(
    IRepository<DocumentType> docTypeRepo,
    ITypstSyntaxChecker checker
) : IRequestHandler<ValidateTypstBlocksQuery, IReadOnlyList<TypstBlockProblem>>
{
    public async Task<IReadOnlyList<TypstBlockProblem>> Handle(ValidateTypstBlocksQuery q, CancellationToken ct)
    {
        var types = await docTypeRepo.GetAllAsync(ct);

        // Собираем записи: для overlay-типа с присланным черновиком — из черновика (толерантно), иначе
        // из персистентной схемы. Единый вход → одно ядро сборки/сортировки/диагностик, что и генерация.
        var records = new List<TypstBlockRecord>();
        foreach (var t in types)
        {
            if (q.OverlayTypeId is { } oid && t.Id == oid && q.DraftRenders is { } draft)
                records.AddRange(TypstPreambleBuilder.ExtractRenders(draft, t.Id, t.Name, t.Code));
            else
                records.AddRange(TypstPreambleBuilder.ExtractRenders(t));
        }

        var built = TypstPreambleBuilder.BuildDetailed(records);
        var byFn = records
            .GroupBy(r => r.FnName)
            .ToDictionary(g => g.Key, g => g.First());

        var problems = new List<TypstBlockProblem>();

        // Диагностики графа (цикл, дубликат) → привязываем к первой участвующей функции.
        foreach (var d in built.Diagnostics)
        {
            var rec = d.FnNames.Count > 0 && byFn.TryGetValue(d.FnNames[0], out var r) ? r : null;
            problems.Add(new TypstBlockProblem(
                d.Severity == TypstBlockDiagnosticSeverity.Error ? "error" : "warning",
                d.Code, d.Message, rec?.TypeId, rec?.TypeName, rec?.VariantName, rec?.FnName, null));
        }

        // Синтаксис: компилируем отсортированный typeblocks.typ, ошибки маппим по line-map на блок.
        try
        {
            foreach (var e in await checker.CheckAsync(built.Content, ct))
            {
                var span = built.Spans.FirstOrDefault(s => e.Line >= s.StartLine && e.Line <= s.EndLine);
                var rec = span is not null && byFn.TryGetValue(span.FnName, out var r) ? r : null;
                problems.Add(new TypstBlockProblem(
                    "error", "syntax", e.Message,
                    rec?.TypeId, rec?.TypeName, rec?.VariantName, span?.FnName,
                    span is null ? null : e.Line - span.StartLine + 1));
            }
        }
        catch (Exception ex)
        {
            // CLI недоступен/сбой — не роняем проверку, помечаем предупреждением (граф-диагностики уже собраны).
            problems.Add(new TypstBlockProblem("warning", "checker-unavailable",
                $"Проверка синтаксиса недоступна: {ex.Message}", null, null, null, null, null));
        }

        return problems;
    }
}
