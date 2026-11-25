using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>Колонки и полосы модуля в «Ходе работ» стройки (CORE-34, PLAN-21).</summary>
[ModuleContract("Колонка Хода работ", Many = true)]
public interface IProgressContribution
{
    /// <summary>Права колонок и полос для ворот экрана. Доступны при регистрации маршрута, без чтения базы.</summary>
    IReadOnlyList<string> AnswerPermissions { get; }

    /// <summary>Отбор по разделу и по правам читателя выполняет поставщик.</summary>
    Task<ProgressContribution> ReadAsync(
        ModuleReader reader, Guid constructionId, Guid? sectionId, CancellationToken ct = default);
}

/// <param name="PlanItemIds">Дополнительные позиции, которые читатель вправе видеть в строках.</param>
public sealed record ProgressContribution(
    IReadOnlyList<ProgressColumn> Columns, IReadOnlyList<ProgressStrip> Strips, IReadOnlyList<Guid> PlanItemIds);

public enum ProgressColumnKind { Volume, Dates }

/// <param name="Values">Ответ в правах читателя. Скрытый ответ содержит только код права.</param>
/// <param name="UnavailableReason">Причина отсутствия данных при доступной колонке, например отсутствие графика.</param>
public sealed record ProgressColumn(
    string Code, string Title, ProgressColumnKind Kind,
    ContractAnswer<IReadOnlyList<ProgressItemValue>> Values, string? UnavailableReason = null);

public sealed record ProgressItemValue(Guid PlanItemId, ProgressValue Value);

/// <summary>Объём в единице позиции или границы срока. Пустая граница означает, что даты нет.</summary>
public sealed record ProgressValue(decimal? Volume = null, DateOnly? Start = null, DateOnly? End = null);

/// <summary>Пояснение над таблицей. Скрытая полоса называет право и не несёт данных.</summary>
public sealed record ProgressStrip(
    string Code, string Text, string State = "ok", string? Link = null, string? Permission = null, string? ProviderTitle = null);
