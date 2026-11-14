using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Строка, которой нужна позиция перечня: её значения и позиция, на которой она стоит сейчас.</summary>
/// <param name="Position">Порядок строки в отчёте — для слов отказа.</param>
/// <param name="StoredKey">Ключ сохранённой строки; <c>null</c> — строка новая.</param>
/// <param name="StoredItemId">Позиция сохранённой строки; <c>null</c> — строка новая.</param>
public sealed record PlanItemDemand(int Position, ReportLineValues Line, PlanItemKey? StoredKey, Guid? StoredItemId);

/// <summary>
/// Позиция перечня работ для строк отчёта (ТЗ CORE-12, MOB-37, CORE-4, D9 зонтика; change
/// <c>work-13-report-model</c>) — через порт этапа 3 <see cref="IModulePlanItems" /> (<c>plan-04</c>).
///
/// <para><b>Сначала ядро, потом строка.</b> Общей транзакции у схемы ядра и схемы модуля нет (ТЗ CORE-4): позиции
/// находятся или заводятся ДО записи строк, своей транзакцией ядра. Упало сохранение строки — позиция осталась без
/// держателя, и это допустимое состояние перечня: повтор найдёт её же.</para>
///
/// <para><b>Ключ — по строке, а не по подсказке клиента</b> (ТЗ MOB-37): идентификатор позиции из запроса здесь не
/// читается вовсе. Иначе строка «Прокладка кабеля» встала бы на позицию «Монтаж лотка», и план с фактом разошлись бы.</para>
///
/// <para><b>Порт зовётся только для нового или изменённого ключа.</b> Порт проверяет архив и у заведённой позиции:
/// вид работы, отправленный в архив, даёт отказ, даже когда позиция уже есть. Строка с прежним ключом остаётся на
/// прежней позиции — неизменённая ссылка на архивную запись принимается (ТЗ CORE-34.4).</para>
/// </summary>
public sealed class WorkPlanItemLink(IModulePlanItems planItems)
{
    /// <summary>Позиция каждой строки по её идентификатору; отказ порта — 400 с полем строки.</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> ResolveAsync(
        Guid constructionId, IReadOnlyList<PlanItemDemand> lines, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Guid>();
        var asked = new List<(PlanItemDemand Demand, PlanItemKey Key)>();
        foreach (var demand in lines)
        {
            var key = demand.Line.KeyOn(constructionId);
            if (demand.StoredKey == key && demand.StoredItemId is { } kept) result[demand.Line.Id] = kept;
            else asked.Add((demand, key));
        }
        if (asked.Count == 0) return result;

        var batch = await planItems.FindOrCreateManyAsync(asked.Select(a => a.Key).Distinct().ToList(), ct);
        foreach (var (demand, key) in asked)
        {
            if (batch.Items.TryGetValue(key, out var item))
            {
                result[demand.Line.Id] = item.Id;
                continue;
            }
            var refusal = batch.Refusals.FirstOrDefault(r => r.Key == key);
            throw new InvalidRequestException(
                $"Строка {demand.Position + 1}, поле «{FieldOf(refusal?.Part)}»: позицию перечня работ не завести — " +
                (refusal?.Reason ?? "порт перечня не ответил ни позицией, ни отказом."));
        }
        return result;
    }

    /// <summary>Поле строки, из-за которого позиция не заведена, — словами отказа.</summary>
    public static string FieldOf(PlanItemKeyPart? part) => part switch
    {
        PlanItemKeyPart.WorkType => "вид работы",
        PlanItemKeyPart.Construction => "стройка",
        PlanItemKeyPart.Section => "раздел",
        PlanItemKeyPart.Unit => "единица",
        _ => "позиция перечня",
    };
}
