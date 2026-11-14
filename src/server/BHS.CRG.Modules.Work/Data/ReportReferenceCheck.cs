using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Ссылка отчёта: где стоит (место и поле), на что указывает и какой вид цели.</summary>
/// <param name="Place">Чья это ссылка — ключ сравнения «до и после»: «шапка» или «строка {id}». По идентификатору,
/// а не по номеру: перестановка строк не делает их ссылки новыми.</param>
/// <param name="Field">Поле словами — оно и попадёт в отказ: «материал», «вид работы».</param>
/// <param name="Shown">Место словами для отказа: «шапка», «строка 3» — человек ищет строку по номеру.</param>
public sealed record ReportReference(string Place, string Field, ReferenceTarget Target, Guid Id, string Shown);

/// <summary>
/// Новая ссылка отчёта не ведёт в архив (ТЗ CORE-34.4; change <c>work-13-report-model</c>): материал, вид работы,
/// вариант, единица, участник, сотрудник, стройка и раздел.
///
/// <para><b>Спрашивается только о НОВЫХ значениях</b> — тех, которых не было в этом поле этой записи до правки.
/// Материал, ушедший в архив после сохранения строки, правку объёма не останавливает: монтажник не выбирал его
/// заново, и отказ означал бы, что строку с ним больше нельзя тронуть вовсе.</para>
///
/// <para>Ответ ядра — <see cref="IModuleReferenceTargets.StatesAsync" />, по одному вызову на вид цели. Пропавшая
/// запись (<see cref="ReferenceState.Lost" />) — тоже отказ: ссылка в никуда хуже ссылки в архив.</para>
/// </summary>
public sealed class ReportReferenceCheck(IModuleReferenceTargets targets)
{
    /// <summary>Отказ 400 с полем, если хоть одна новая ссылка ведёт в архив или в никуда.</summary>
    /// <param name="before">Ссылки до правки; пусто — заведение.</param>
    /// <param name="after">Ссылки после правки.</param>
    public async Task EnsureAsync(
        IReadOnlyCollection<ReportReference> before, IReadOnlyCollection<ReportReference> after, CancellationToken ct)
    {
        var old = before.Select(r => (r.Place, r.Field, r.Id)).ToHashSet();
        var fresh = after.Where(r => r.Id != Guid.Empty && !old.Contains((r.Place, r.Field, r.Id))).ToList();
        foreach (var group in fresh.GroupBy(r => r.Target))
        {
            var states = await targets.StatesAsync(group.Key, group.Select(r => r.Id).Distinct().ToList(), ct);
            foreach (var reference in group)
            {
                var state = states.TryGetValue(reference.Id, out var s) ? s : ReferenceState.Lost;
                if (state == ReferenceState.Archived)
                    throw new InvalidRequestException(
                        $"Поле «{reference.Field}» ({reference.Shown}): выбранная запись в архиве — новую ссылку на " +
                        "архивную запись поставить нельзя. Выберите действующую.");
                if (state == ReferenceState.Lost)
                    throw new InvalidRequestException(
                        $"Поле «{reference.Field}» ({reference.Shown}): выбранная запись не найдена — её удалили или " +
                        "идентификатор неверен.");
            }
        }
    }

    /// <summary>Ссылки шапки отчёта.</summary>
    public static IEnumerable<ReportReference> OfHeader(Guid employeeId, Guid constructionId) =>
    [
        new("шапка", "сотрудник", ReferenceTarget.Record, employeeId, "шапка"),
        new("шапка", "стройка", ReferenceTarget.Construction, constructionId, "шапка"),
    ];

    /// <summary>Ссылки строки — по значениям, разобранным из запроса; <paramref name="position" /> — её порядок с нуля.</summary>
    public static IEnumerable<ReportReference> OfLine(ReportLineValues line, int position) =>
        Line(line.Id, position, line.WorkTypeId, line.VariantId, line.UnitId, line.SectionId,
            line.Materials.Select(m => m.NomenclatureId ?? Guid.Empty), line.Participants);

    /// <summary>Ссылки сохранённой строки — для сравнения «до правки».</summary>
    public static IEnumerable<ReportReference> OfLine(ReportLine line) =>
        Line(line.Id, line.Position, line.WorkTypeId, line.VariantId, line.UnitId, line.SectionId,
            line.Materials.OrderBy(m => m.Position).Select(m => m.NomenclatureId), line.Participants.Select(p => p.EmployeeId));

    private static IEnumerable<ReportReference> Line(
        Guid lineId, int position, Guid workTypeId, Guid? variantId, Guid unitId, Guid? sectionId,
        IEnumerable<Guid> materials, IEnumerable<Guid> participants)
    {
        var at = $"строка {lineId}";
        var shown = $"строка {position + 1}";
        yield return new(at, "вид работы", ReferenceTarget.Record, workTypeId, shown);
        if (variantId is { } variant) yield return new(at, "вариант", ReferenceTarget.Record, variant, shown);
        yield return new(at, "единица", ReferenceTarget.Record, unitId, shown);
        if (sectionId is { } section) yield return new(at, "раздел", ReferenceTarget.Section, section, shown);
        // Материал и участник сравниваются по строке, а не по порядку: перестановка материалов — не новая ссылка.
        foreach (var material in materials) yield return new(at, "материал", ReferenceTarget.Record, material, shown);
        foreach (var employee in participants) yield return new(at, "участник", ReferenceTarget.Record, employee, shown);
    }
}
