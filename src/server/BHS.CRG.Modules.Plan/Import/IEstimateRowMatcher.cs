using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>Куда ложится редакция — то, по чему сопоставление выбирает подстановку.</summary>
/// <param name="NormativeBase">Нормативная база файла, как её назвала программа: часть ключа шифра (<c>plan-08</c>).</param>
public sealed record EstimateRowMatchTarget(Guid ConstructionId, Guid? SectionId, string? NormativeBase = null);

/// <summary>
/// Подстановка сопоставлений в строки редакции до её записи — шаг 1 создания редакции из импорта (change
/// <c>plan-07-estimate-import</c>, D8 зонтика): шифр расценки → вид работы, единица и коэффициент пересчёта,
/// наименование ресурса → номенклатура.
///
/// <para>Здесь — только точка расширения. Подстановку запомненного делает <c>plan-08</c>
/// (<c>Matching.RememberedRowMatcher</c>, регистрирует <c>PlanModule</c>); <see cref="NoEstimateRowMatcher" /> —
/// пустая реализация для тестов, которым подстановка мешает.</para>
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

/// <summary>Сопоставление, которое ничего не подставляет: каждая строка ложится несопоставленной.</summary>
public sealed class NoEstimateRowMatcher : IEstimateRowMatcher
{
    public Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct) =>
        Task.FromResult(rows);
}
