namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Заголовок распределения редакции (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1): версия распределения, кто и
/// когда менял его последним и откуда оно перенесено.
///
/// <para><b>Версия своя</b> — <c>xmin</c> этой записи — и с версией редакции не смешивается: распределение
/// правится и у зафиксированной редакции, а сопоставление строк своей версией не сдвигает распределения. Каждая
/// правка правил и частей трогает заголовок (<see cref="Touch" />), поэтому две вкладки узнают друг о друге
/// отказом, а не молча поверх. Заголовка ещё нет — версия «0»: первая правка его заводит, а проигравшая
/// одновременная получает отказ ключа.</para>
///
/// <para><b><see cref="ChangedAt" /></b> — то, с чем график сравнит момент построения («распределение изменено
/// после построения», <c>plan-19</c>), и то, по чему повторный импорт выбирает источник переноса — редакцию с
/// самым поздним изменением, не обязательно действующую.</para>
///
/// <para><b><see cref="Label" /></b> — чем назвать редакцию в отказе удалить раздел стройки (««Основная», ред.
/// 3»): ядро называет документ-держатель одной колонкой (ТЗ CORE-34.1). Запоминается на момент последней правки
/// распределения, как цель в журнале, а не обновляется переименованием сметы: обновление сдвинуло бы версию
/// распределения, и чужая вкладка получила бы отказ «распределение изменили», которого не было.</para>
/// </summary>
public sealed class RevisionDistribution
{
    public const string FirstVersion = "0";

    private RevisionDistribution() { }

    public Guid RevisionId { get; private set; }

    public Guid EstimateId { get; private set; }

    public string Label { get; private set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>Кто менял — справка, а не связь: удалению учётной записи не мешает.</summary>
    public Guid? ChangedBy { get; private set; }

    /// <summary>Имя менявшего на момент правки — им 409 называет, кто изменил распределение.</summary>
    public string? ChangedByName { get; private set; }

    /// <summary>Редакция, из которой распределение перенесено повторным импортом; <c>null</c> — не переносилось
    /// либо источник с тех пор удалён (номер остаётся — <see cref="CarriedFromNumber" />).</summary>
    public Guid? CarriedFromRevisionId { get; private set; }

    public int? CarriedFromNumber { get; private set; }

    /// <summary>Строк, перенесённых без пометки (<c>carried</c>): остальные категории — позициями отчёта.</summary>
    public int CarriedRows { get; private set; }

    internal static RevisionDistribution Create(Estimate estimate, EstimateRevision revision) =>
        new() { RevisionId = revision.Id, EstimateId = estimate.Id, Label = LabelOf(estimate, revision) };

    /// <summary>Распределение изменено: момент, автор и название редакции — на сейчас.</summary>
    internal void Touch(Estimate estimate, EstimateRevision revision, Guid? userId, string userName, DateTimeOffset now)
    {
        Label = LabelOf(estimate, revision);
        ChangedAt = now;
        ChangedBy = userId;
        ChangedByName = userName;
    }

    internal void CarriedFrom(EstimateRevision source, int carriedRows)
    {
        CarriedFromRevisionId = source.Id;
        CarriedFromNumber = source.Number;
        CarriedRows = carriedRows;
    }

    internal static string LabelOf(Estimate estimate, EstimateRevision revision) =>
        PlanText.Clip($"«{estimate.Name}», ред. {revision.Number}", LabelLength)!;

    public const int LabelLength = Estimate.NameLength + 20;
}
