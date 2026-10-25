using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>Куда ложится редакция — то, по чему сопоставление выбирает подстановку.</summary>
public sealed record EstimateRowMatchTarget(Guid ConstructionId, Guid? SectionId);

/// <summary>
/// Подстановка сопоставлений в строки редакции до её записи — шаг 1 создания редакции из импорта (change
/// <c>plan-07-estimate-import</c>, D8 зонтика): шифр расценки → вид работы, единица и коэффициент пересчёта,
/// наименование ресурса → номенклатура.
///
/// <para>Здесь — только точка расширения. Запоминание и подстановку делает <c>plan-08</c>, заменяя реализацию
/// по умолчанию (<see cref="NoEstimateRowMatcher" />): до него каждая строка ложится несопоставленной.</para>
///
/// <para>Позиции перечня подстановка не заводит: их заводит создание редакции портом «найди или создай»
/// по виду работы и единице, которые она поставила (шаг 2).</para>
/// </summary>
public interface IEstimateRowMatcher
{
    /// <summary>Строки с подстановкой — тем же числом и в том же порядке, что пришли.</summary>
    Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct);
}

/// <summary>Сопоставление по умолчанию — ничего не подставляет (до <c>plan-08</c>).</summary>
public sealed class NoEstimateRowMatcher : IEstimateRowMatcher
{
    public Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct) =>
        Task.FromResult(rows);
}
