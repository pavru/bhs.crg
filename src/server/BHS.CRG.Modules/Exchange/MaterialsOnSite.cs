using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>Номенклатура, отпущенная на стройку проведёнными накладными (COST-17, COST-18).</summary>
[ModuleContract("costs", "Материалы на объекте")]
public interface IMaterialsOnSite
{
    /// <summary>
    /// Право <c>costs.materials.read</c> даёт полный перечень, <c>costs.materials.hint</c> — идентификаторы
    /// номенклатуры. Без обоих прав перечень скрыт. Задание без пользователя получает отказ с причиной.
    /// </summary>
    Task<ContractAnswer<MaterialsOnSite>> ReadAsync(ModuleReader reader, Guid constructionId, CancellationToken ct = default);

    /// <summary>Явная подсказка по любому из двух прав. Единица указана, если у позиции она однозначна.</summary>
    Task<ContractAnswer<IReadOnlyList<MaterialOnSiteHint>>> ReadHintAsync(
        ModuleReader reader, Guid constructionId, CancellationToken ct = default);

    /// <summary>Полевой срез по назначению, проверенному адаптером Work.</summary>
    Task<IReadOnlyList<MaterialOnSiteHint>> ReadFieldHintsAsync(AssignedFieldSite site, CancellationToken ct = default);

    /// <summary>Зафиксированная ревизия источника узких полевых подсказок.</summary>
    Task<string> ReadFieldRevisionAsync(CancellationToken ct = default);
}

public sealed record MaterialOnSiteHint(Guid NomenclatureId, string? Unit);

/// <summary>Данные контракта в объёме прав пользователя.</summary>
public abstract record MaterialsOnSite
{
    private MaterialsOnSite() { }

    private protected abstract void Sealed();

    public sealed record Full(IReadOnlyList<MaterialOnSite> Items, int UnmatchedLines) : MaterialsOnSite
    {
        private protected override void Sealed() { }
    }

    public sealed record Hint(IReadOnlyList<Guid> NomenclatureIds) : MaterialsOnSite
    {
        private protected override void Sealed() { }
    }
}

/// <summary>Количество одной позиции номенклатуры в одной единице измерения и даты её отпуска.</summary>
public sealed record MaterialOnSite(Guid NomenclatureId, string? Unit, decimal Quantity, DateOnly First, DateOnly Last);
