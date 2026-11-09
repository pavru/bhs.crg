namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Куда уходит объём: «стройка в целом» — явный выбор «не делить», или раздел стройки (ТЗ PLAN-5.1).</summary>
public enum DistributionTargetKind
{
    Whole,
    Section,
}

/// <summary>
/// Цель распределения (change <c>plan-10-distribution</c>, выбор П2): «стройка в целом» или раздел стройки.
/// «Нет правила» — не цель, а отсутствие записи: смета без распределения работает как стройка целиком, но
/// счётчик «не распределено» её строки считает — решения о них ещё не было.
/// </summary>
/// <param name="SectionId">Раздел стройки — запись ядра; у «стройки в целом» — <c>null</c>.</param>
public sealed record DistributionTarget(DistributionTargetKind Kind, Guid? SectionId)
{
    public static readonly DistributionTarget WholeConstruction = new(DistributionTargetKind.Whole, null);

    public static DistributionTarget Section(Guid sectionId) => new(DistributionTargetKind.Section, sectionId);

    public static string Code(DistributionTargetKind kind) => kind == DistributionTargetKind.Whole ? "whole" : "section";
}

/// <summary>
/// Правило «раздел ЛСР → цель» редакции сметы (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1): весь объём строк
/// раздела ЛСР уходит в цель. Строка с частями (<see cref="DistributionPart" />) правилу не подчиняется — части
/// заменяют его для своей строки целиком.
///
/// <para>⚠️ <b>Ключ — <see cref="EstimateRow.LsrSectionCode" /> редакции</b>, как его группируют разделы ЛСР
/// (<c>RevisionSectionsEndpoints</c>). Это НЕ код ГРАНД-Сметы, а порядковый номер главы в файле
/// (<c>chapter.Index</c>, <c>EstimateImportRows</c>): внутри одной редакции он однозначен, но между редакциями
/// сдвигается от вставленной главы. Поэтому перенос сопоставляет разделы по названию
/// (<c>DistributionCarryOverPlan</c>), а не этим ключом. <c>null</c> — строки вне разделов ЛСР.</para>
/// </summary>
public sealed class DistributionRule
{
    private DistributionRule() { }

    public Guid Id { get; private set; }

    public Guid RevisionId { get; private set; }

    /// <summary>Номер главы в файле, как у строк редакции; <c>null</c> — строки вне разделов.</summary>
    public string? LsrSectionCode { get; private set; }

    public DistributionTargetKind Kind { get; private set; }

    /// <summary>Раздел стройки — запись ядра, идентификатором без внешнего ключа (ТЗ CORE-34.1).</summary>
    public Guid? SectionId { get; private set; }

    public DistributionTarget Target => new(Kind, SectionId);

    internal static DistributionRule Create(Guid revisionId, string? lsrSectionCode, DistributionTarget target) =>
        new()
        {
            Id = Guid.NewGuid(),
            RevisionId = revisionId,
            LsrSectionCode = lsrSectionCode,
            Kind = target.Kind,
            SectionId = target.Kind == DistributionTargetKind.Section ? target.SectionId : null,
        };

    internal void Point(DistributionTarget target)
    {
        Kind = target.Kind;
        SectionId = target.Kind == DistributionTargetKind.Section ? target.SectionId : null;
    }
}
