using System.Text.Json.Serialization;
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

    /// <summary>Месяц трудозатрат. Остальные вклады по умолчанию сохраняют свой охват.</summary>
    Task<ProgressContribution> ReadForPeriodAsync(ModuleReader reader, Guid constructionId, Guid? sectionId,
        DateOnly? laborMonth, CancellationToken ct = default) => ReadAsync(reader, constructionId, sectionId, ct);
}

/// <param name="PlanItemIds">Дополнительные позиции, которые читатель вправе видеть в строках.</param>
public sealed record ProgressContribution(
    IReadOnlyList<ProgressColumn> Columns, IReadOnlyList<ProgressStrip> Strips, IReadOnlyList<Guid> PlanItemIds);

public enum ProgressColumnKind { Volume, Dates }

/// <param name="Code">Локальный код, уникальный внутри вклада. Ядро добавляет к нему код модуля в ответе API.</param>
/// <param name="Values">Ответ в правах читателя. Скрытый ответ содержит только код права.</param>
/// <param name="UnavailableReason">Причина отсутствия данных при доступной колонке, например отсутствие графика.</param>
public sealed record ProgressColumn(
    string Code, string Title, ProgressColumnKind Kind,
    ContractAnswer<IReadOnlyList<ProgressItemValue>> Values, string? UnavailableReason = null);

public sealed record ProgressItemValue(Guid PlanItemId, ProgressValue Value);

/// <summary>Объём в единице позиции или границы срока. Пустой объём или граница означают отсутствие значения.</summary>
public sealed record ProgressValue(decimal? Volume = null, DateOnly? Start = null, DateOnly? End = null);

/// <summary>Пояснение над таблицей. Скрытая полоса называет право и не несёт данных.</summary>
public sealed record ProgressStrip(
    string Code, string Text, string State = "ok", string? Link = null, string? Permission = null, string? ProviderTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? Month = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProgressLabor? Labor = null);

public sealed record ProgressLabor(decimal? EstimatedHours, decimal? ActualHours, bool Distributed,
    decimal? OffEstimateHours, IReadOnlyList<ProgressLaborWork> OffEstimateWorks,
    decimal? UnallocatedHours, bool Incomplete);

public sealed record ProgressLaborWork(string? Name, string? Unit, decimal Volume);
